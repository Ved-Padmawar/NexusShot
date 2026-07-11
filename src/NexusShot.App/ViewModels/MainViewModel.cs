using System.Collections.ObjectModel;
using NexusShot.App.Enums;
using NexusShot.App.Models;
using NexusShot.App.Services;
using Windows.Graphics;

namespace NexusShot.App.ViewModels;

public sealed class MainViewModel(AppServices services) : ObservableObject
{
    private const int MaximumHistoryItems = 200;

    private ScreenshotTile? _selected;

    public ObservableCollection<ScreenshotTile> Screenshots { get; } = [];

    public bool IsEmpty => Screenshots.Count == 0;

    /// <summary>The capture shown in the detail pane. Null whenever the history is empty.</summary>
    public ScreenshotTile? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            _selected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selected is not null;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var history = await services.Storage.LoadHistoryAsync(cancellationToken);

        // Prune entries whose files were deleted or moved externally. The existence checks run
        // off the UI thread so a slow (or network) folder cannot stall the first paint.
        var existing = await Task.Run(() => history.Where(item => File.Exists(item.FilePath)).ToList(), cancellationToken);

        foreach (var item in existing) Screenshots.Add(new ScreenshotTile(item));
        Selected = Screenshots.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));

        if (existing.Count != history.Count)
            await services.Storage.SaveHistoryAsync(existing, cancellationToken);
    }

    public async Task CaptureAsync(CaptureMode mode, RectInt32? region = null, CancellationToken cancellationToken = default)
    {
        var result = mode switch
        {
            CaptureMode.ActiveWindow => await services.Capture.CaptureActiveWindowAsync(cancellationToken),
            CaptureMode.Region when region is not null => await services.Capture.CaptureRegionAsync(region.Value, cancellationToken),
            _ => await services.Capture.CaptureFullScreenAsync(cancellationToken),
        };
        await CompleteCaptureAsync(result, cancellationToken);
    }

    private async Task CompleteCaptureAsync(CaptureResult result, CancellationToken cancellationToken)
    {
        var path = result.TemporaryFilePath;
        if (services.Settings.SaveAutomatically)
        {
            path = await services.Storage.SaveScreenshotAsync(result.TemporaryFilePath, services.Settings.ScreenshotFolder, cancellationToken);
            TryDelete(result.TemporaryFilePath);
        }

        var item = new ScreenshotHistoryItem
        {
            FilePath = path,
            Width = result.Width,
            Height = result.Height,
            CaptureMode = result.Mode,
        };

        var tile = new ScreenshotTile(item);
        Screenshots.Insert(0, tile);
        while (Screenshots.Count > MaximumHistoryItems)
        {
            // Trimming must not leave Selected pointing at a tile that is no longer in the list.
            var evicted = Screenshots[^1];
            Screenshots.RemoveAt(Screenshots.Count - 1);
            if (ReferenceEquals(Selected, evicted)) Selected = null;
        }

        // The newest capture becomes the detail pane's subject.
        Selected = tile;
        OnPropertyChanged(nameof(IsEmpty));

        // Snapshot before serializing: the collection is mutated on the UI thread.
        var history = Screenshots.Select(tile => tile.Item).ToList();
        await services.Storage.SaveHistoryAsync(history, cancellationToken);

        if (services.Settings.CopyToClipboardAutomatically)
        {
            try { await services.Clipboard.CopyImageAsync(path, cancellationToken); }
            catch (Exception exception) { services.Logger.Error("capture.clipboard_failed", exception); }
        }

        await services.Previews.ShowPreviewAsync(item, cancellationToken);
    }

    /// <summary>
    /// Rebuilds the tile for an item whose file the editor rewrote, so the sidebar and detail
    /// pane decode fresh pixels instead of serving their cached bitmaps.
    /// </summary>
    public void RefreshScreenshot(ScreenshotHistoryItem item)
    {
        var tile = Screenshots.FirstOrDefault(t => ReferenceEquals(t.Item, item) || t.Item.Id == item.Id);
        if (tile is null) return;

        var index = Screenshots.IndexOf(tile);
        var replacement = new ScreenshotTile(item);
        Screenshots[index] = replacement;
        if (ReferenceEquals(Selected, tile)) Selected = replacement;
        PersistHistory();
    }

    /// <summary>
    /// Removes a capture from the history and deletes it from disk. Selection moves to the next
    /// entry, so the detail pane never lands on nothing while captures remain.
    /// </summary>
    public async Task RemoveAsync(ScreenshotTile tile, CancellationToken cancellationToken = default)
    {
        var index = Screenshots.IndexOf(tile);
        if (index < 0) return;

        Screenshots.RemoveAt(index);
        TryDelete(tile.Item.FilePath);

        Selected = Screenshots.Count == 0
            ? null
            : Screenshots[Math.Min(index, Screenshots.Count - 1)];
        OnPropertyChanged(nameof(IsEmpty));

        var history = Screenshots.Select(item => item.Item).ToList();
        await services.Storage.SaveHistoryAsync(history, cancellationToken);
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            services.Logger.Error("capture.temp_cleanup_failed", exception);
        }
    }

    // ============================  FILESYSTEM SYNC  ============================
    // A watcher on the save folder keeps the sidebar honest when files are created, deleted,
    // renamed or moved in File Explorer. Events arrive on threadpool threads and are marshalled
    // onto the UI thread before touching the ObservableCollection.

    private FileSystemWatcher? _watcher;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    public void StartWatching(Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        RestartWatcher();
    }

    /// <summary>(Re-)binds the watcher to the current save folder; called after folder changes.</summary>
    public void RestartWatcher()
    {
        StopWatching();
        var folder = services.Settings.ScreenshotFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName,
                IncludeSubdirectories = false,
            };
            watcher.Deleted += (_, e) => OnUi(() => HandleFileDeleted(e.FullPath));
            watcher.Renamed += (_, e) => OnUi(() => HandleFileRenamed(e.OldFullPath, e.FullPath));
            watcher.Created += (_, e) => { if (IsPng(e.FullPath)) _ = HandleFileCreatedAsync(e.FullPath); };
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            services.Logger.Error("history.watch_failed", exception);
        }
    }

    public void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    private void OnUi(Action action) => _dispatcher?.TryEnqueue(() => action());

    private static bool IsPng(string path) =>
        string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase);

    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private void HandleFileDeleted(string path)
    {
        var tile = Screenshots.FirstOrDefault(t => PathEquals(t.Item.FilePath, path));
        if (tile is null) return;

        var index = Screenshots.IndexOf(tile);
        Screenshots.RemoveAt(index);
        if (ReferenceEquals(Selected, tile))
        {
            Selected = Screenshots.Count == 0
                ? null
                : Screenshots[Math.Min(index, Screenshots.Count - 1)];
        }
        OnPropertyChanged(nameof(IsEmpty));
        PersistHistory();
    }

    private void HandleFileRenamed(string oldPath, string newPath)
    {
        var tile = Screenshots.FirstOrDefault(t => PathEquals(t.Item.FilePath, oldPath));
        if (tile is null)
        {
            // Renaming *into* a .png (e.g. finishing a download) is effectively a creation.
            if (IsPng(newPath)) _ = HandleFileCreatedAsync(newPath);
            return;
        }

        if (!IsPng(newPath))
        {
            HandleFileDeleted(oldPath);
            return;
        }

        // Replace the tile so every derived string (name, subtitle) reflects the new path.
        var index = Screenshots.IndexOf(tile);
        tile.Item.FilePath = newPath;
        var replacement = new ScreenshotTile(tile.Item);
        Screenshots[index] = replacement;
        if (ReferenceEquals(Selected, tile)) Selected = replacement;
        PersistHistory();
    }

    private async Task HandleFileCreatedAsync(string path)
    {
        try
        {
            // Give the writer a moment to finish; screenshots and copies land in one burst.
            await Task.Delay(600);
            if (await Task.Run(() => ReadImageSize(path)) is not { } size) return;

            OnUi(() =>
            {
                if (Screenshots.Any(t => PathEquals(t.Item.FilePath, path))) return;

                var item = new ScreenshotHistoryItem
                {
                    FilePath = path,
                    Width = size.Width,
                    Height = size.Height,
                    CreatedAt = File.GetCreationTime(path),
                };
                Screenshots.Insert(0, new ScreenshotTile(item));
                OnPropertyChanged(nameof(IsEmpty));
                PersistHistory();
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The file vanished again or is locked; the watcher will report any later state.
        }
    }

    /// <summary>Reads only the image header for dimensions, without decoding or locking the file.</summary>
    private static (int Width, int Height)? ReadImageSize(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var image = System.Drawing.Image.FromStream(stream, false, false);
                return (image.Width, image.Height);
            }
            catch (FileNotFoundException) { return null; }
            catch (IOException) { Thread.Sleep(250); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
            {
                return null; // Locked beyond retry, or not actually an image.
            }
        }
        return null;
    }

    private async void PersistHistory()
    {
        try
        {
            var history = Screenshots.Select(tile => tile.Item).ToList();
            await services.Storage.SaveHistoryAsync(history, CancellationToken.None);
        }
        catch (Exception exception)
        {
            services.Logger.Error("history.save_failed", exception);
        }
    }
}

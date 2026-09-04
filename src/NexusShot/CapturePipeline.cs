using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;
using NexusShot.Views;

namespace NexusShot;

/// <summary>
/// Owns what happens to a capture after the pixels exist: filing it, showing a quick-access card,
/// opening/raising its editor, and keeping the history list, the shell's grid, and any open card or
/// editor in sync with each other. The shell, the previews, and the editors each fire their own
/// events; this is the one place that listens and sequences what follows.
/// </summary>
public sealed class CapturePipeline : IDisposable
{
    private readonly Storage _storage;
    private readonly AppSettings _settings;
    private readonly List<ScreenshotHistoryItem> _history;
    private readonly MainWindow _main;

    /// <summary>Editors, keyed by the file they are editing, so a second Edit on the same capture
    /// raises the window that is already open rather than opening another.</summary>
    private readonly Dictionary<string, EditorWindow> _editors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<EditorWindow> _openEditors = [];

    /// <summary>The quick-access cards, newest first. They stack upward from the bottom-left.</summary>
    private readonly List<FloatingPreview> _previews = [];

    public CapturePipeline(Storage storage, AppSettings settings,
        List<ScreenshotHistoryItem> history, MainWindow main)
    {
        _storage = storage;
        _settings = settings;
        _history = history;
        _main = main;

        _main.EditRequested += Edit;
    }

    /// <summary>Moves a fresh capture into the screenshot folder, records it, and shows its card.
    /// <paramref name="temporaryPath"/> is consumed: it is moved, or adopted as the record's own
    /// path when auto-save is off. <paramref name="pixels"/> is the capture's bitmap when the caller
    /// still has it, so the clipboard copy does not decode the PNG that was just written.</summary>
    /// <summary>Takes ownership of <paramref name="pixels"/>: it is freed once the clipboard copy
    /// has finished with it, or immediately when there is no copy to make.</summary>
    public void Land(string temporaryPath, DecodedImage? pixels = null)
    {
        var item = Store(temporaryPath);
        Log.Info("capture", $"{item.Width}x{item.Height}");

        if (_settings.CopyToClipboardAutomatically)
        {
            var path = item.FilePath;
            _ = Task.Run(() =>
            {
                try
                {
                    if (pixels is not null) ClipboardImage.Copy(pixels, path);
                    else ClipboardImage.Copy(path);
                }
                catch (Exception exception)
                {
                    // The clipboard is owned by whichever process grabbed it last, so a copy can lose
                    // a race with no fault of ours. The capture is already on disk either way.
                    Log.Error("clipboard.copy", exception, path);
                }
                finally { pixels?.Dispose(); }
            });
        }
        else pixels?.Dispose();

        _main.AddCapture(item);
        ShowPreview(item);
    }

    /// <summary>Moves the temp capture into the screenshot folder and records it.</summary>
    private ScreenshotHistoryItem Store(string temporaryPath)
    {
        // The header, not the pixels: this only needs the dimensions for the history row.
        var (width, height) = ImageSurface.ReadSize(temporaryPath);

        Directory.CreateDirectory(_settings.ScreenshotFolder);
        var baseName = CaptureName.For(DateTime.Now);
        var destination = Path.Combine(_settings.ScreenshotFolder, baseName + ".png");
        if (_settings.SaveAutomatically)
        {
            var counter = 1;
            while (File.Exists(destination))
            {
                destination = Path.Combine(_settings.ScreenshotFolder, $"{baseName}_{counter:D3}.png");
                counter++;
                if (counter > 999) { destination = Path.Combine(_settings.ScreenshotFolder, $"NexusShot {Guid.NewGuid():N}.png"); break; }
            }
            File.Move(temporaryPath, destination, overwrite: false);
        }
        else
        {
            destination = temporaryPath;
        }

        return new ScreenshotHistoryItem
        {
            FilePath = destination,
            CapturedAt = DateTimeOffset.Now,
            Width = width,
            Height = height,
        };
    }

    /// <summary>Updates the card showing a re-saved capture, or brings a new one up if that card was
    /// already dismissed.</summary>
    private void RefreshPreview(ScreenshotHistoryItem item)
    {
        var card = _previews.FirstOrDefault(preview =>
            string.Equals(preview.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase));

        if (card is null)
        {
            ShowPreview(item);
            return;
        }

        card.Refresh(item);
        ReflowPreviews();
    }

    /// <summary>Shows a quick-access card for a fresh capture, and reflows the stack.</summary>
    private void ShowPreview(ScreenshotHistoryItem item)
    {
        var preview = new FloatingPreview(item, _settings.PreviewDismissSeconds);

        preview.EditRequested += Edit;
        preview.PinnedChanged += ReflowPreviews;
        preview.Dismissed += card =>
        {
            _previews.Remove(card);
            ReflowPreviews();
        };

        // Newest at the bottom of the stack, so the most recent capture is nearest the corner and
        // older ones ride up above it.
        _previews.Insert(0, preview);
        ReflowPreviews();
        preview.Show();
    }

    /// <summary>
    /// Lays the cards out from the bottom-left corner upward.
    ///
    /// Anything that runs off the top of the work area is dismissed rather than drawn off-screen -
    /// a card you cannot see is a card you cannot act on, and it would sit there holding a bitmap.
    /// </summary>
    private void ReflowPreviews()
    {
        // The monitor the pointer is on, not the one the shell happens to be on: a capture belongs
        // to the screen the user is looking at.
        var work = Monitors.WorkAreaUnderCursor();
        var scale = Monitors.DpiScaleUnderCursor(_main.Handle);

        var offset = 0.0;
        foreach (var preview in _previews.ToArray())
        {
            var height = preview.StackHeight(scale);

            if (offset + height > work.Height * 0.8)
            {
                preview.Dismiss();
                continue;
            }

            preview.PlaceAt(work, scale, offset);
            offset += height;
        }
    }

    private void Edit(ScreenshotHistoryItem item)
    {
        if (_editors.TryGetValue(item.FilePath, out var existing))
        {
            existing.Show();
            existing.SetForeground();
            return;
        }

        var editor = new EditorWindow(item.FilePath, _settings.Theme);
        _openEditors.Add(editor);
        var editorPath = item.FilePath;
        _editors[editorPath] = editor;

        editor.Closed += () =>
        {
            _openEditors.Remove(editor);
            // The editor releases its own device resources on destroy; this just drops our handle.
            if (_editors.TryGetValue(editorPath, out var owner) && ReferenceEquals(owner, editor))
                _editors.Remove(editorPath);

            // The capture may have just been re-saved, so its cached bitmap is the old pixels.
            _main.DropCache(editorPath);
            _main.Invalidate();
        };

        // Save overwrites the capture, so its history row and its card are both showing stale pixels
        // at a size a crop may have changed.
        editor.Saved += path =>
        {
            var (width, height) = ImageSurface.ReadSize(path);

            var entry = _history.FirstOrDefault(candidate => string.Equals(candidate.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                entry.Width = width;
                entry.Height = height;
                _storage.SaveHistory(_history);
            }

            _main.DropCache(path);
            _main.Invalidate();

            RefreshPreview(entry ?? new ScreenshotHistoryItem
            {
                FilePath = path,
                CapturedAt = DateTimeOffset.Now,
                Width = width,
                Height = height,
            });
        };

        // Save As writes a new file; it belongs in the history, and gets a card of its own.
        editor.SavedAs += path =>
        {
            if (_editors.TryGetValue(editorPath, out var owner) && ReferenceEquals(owner, editor))
                _editors.Remove(editorPath);
            editorPath = path;
            _editors[path] = editor;
            _main.DropCache(path);
            var (width, height) = ImageSurface.ReadSize(path);
            var existing = _history.FirstOrDefault(entry => string.Equals(entry.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Width = width;
                existing.Height = height;
                _storage.SaveHistory(_history);
                _main.Invalidate();
                RefreshPreview(existing);
                return;
            }

            var item = new ScreenshotHistoryItem
            {
                FilePath = path,
                CapturedAt = DateTimeOffset.Now,
                Width = width,
                Height = height,
            };

            _main.AddCapture(item);
            ShowPreview(item);
        };

        var scale = Functions.GetDpiForWindow(editor.Handle) / 96.0;
        editor.ResizeClient((int)(1180 * scale), (int)(820 * scale));
        editor.Center();
        editor.Show();
        editor.SetForeground();
    }

    /// <summary>Open editors follow the shell's theme rather than the one they were opened with.</summary>
    public void RethemeEditors()
    {
        foreach (var editor in _openEditors) editor.SetTheme(_settings.Theme);
    }

    public void Dispose()
    {
        _main.EditRequested -= Edit;
        foreach (var editor in _openEditors.ToArray()) editor.Dispose();
        _openEditors.Clear();
        _editors.Clear();

        foreach (var preview in _previews.ToArray()) preview.Dispose();
        _previews.Clear();
    }
}

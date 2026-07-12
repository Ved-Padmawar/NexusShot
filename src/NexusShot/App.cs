using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;
using NexusShot.Views;

namespace NexusShot;

/// <summary>
/// The application.
///
/// A capture tool's real front end is the tray icon and the global hotkeys, not a window - so the
/// app owns those, and the main window is something it shows and hides. Closing the window does not
/// exit, or the shortcuts would die with it.
/// </summary>
public sealed class App : IDisposable
{
    private readonly Storage _storage = new();
    private readonly AppSettings _settings;
    private readonly List<ScreenshotHistoryItem> _history;

    private readonly MainWindow _main;
    private readonly TrayIcon _tray;
    private readonly Hotkeys _hotkeys;
    private FolderWatcher? _watcher;

    /// <summary>Editors, keyed by the file they are editing, so a second Edit on the same capture
    /// raises the window that is already open rather than opening another.</summary>
    private readonly Dictionary<string, EditorWindow> _editors = [];

    /// <summary>The quick-access cards, newest first. They stack upward from the bottom-left.</summary>
    private readonly List<FloatingPreview> _previews = [];

    public App()
    {
        _settings = _storage.LoadSettings();
        _history = _storage.LoadHistory();

        // Captures whose files have been deleted behind our back are dropped on load, so the grid
        // never shows a row that cannot be opened.
        _history.RemoveAll(item => !File.Exists(item.FilePath));

        _main = new MainWindow(_storage, _settings, _history);
        _main.CaptureRequested += Capture;
        _main.EditRequested += Edit;
        _main.HotkeysChanged += ApplyHotkeys;

        var scale = Functions.GetDpiForWindow(_main.Handle) / 96.0;
        _main.ResizeClient((int)(1100 * scale), (int)(720 * scale));
        _main.Center();

        _tray = new TrayIcon(_main.Handle, "NexusShot", AppIcon.Small);
        _hotkeys = new Hotkeys(_main.Handle);
        ApplyHotkeys();

        _main.SettingsChanged += OnSettingsChanged;
        WatchSaveFolder();

        Log.Info("app.started", $"{_history.Count} captures");

        // The main window's WndProc is the app's message pump: the tray and the hotkeys both post
        // here, which is why they are registered against its handle.
        _main.MessageIntercept = OnMessage;
    }

    public void Run()
    {
        _main.Show();
        _main.SetForeground();

        using var application = new Application();
        application.Run();
    }

    /// <summary>Returns true when the message was ours.</summary>
    private bool OnMessage(uint message, long wParam, long lParam)
    {
        if (message == TrayIcon.WM_TRAY)
        {
            switch (_tray.OnMessage(lParam))
            {
                case TrayIcon.Command.CaptureRegion: Capture(CaptureMode.Region); return true;
                case TrayIcon.Command.CaptureFullScreen: Capture(CaptureMode.FullScreen); return true;
                case TrayIcon.Command.CaptureWindow: Capture(CaptureMode.ActiveWindow); return true;
                case TrayIcon.Command.OpenMain: ShowMain(); return true;
                case TrayIcon.Command.Exit: Exit(); return true;
                default: return true;
            }
        }

        if (message == Hotkeys.WM_HOTKEY)
        {
            switch (_hotkeys.Resolve(wParam))
            {
                case HotkeyId.CaptureRegion: Capture(CaptureMode.Region); return true;
                case HotkeyId.CaptureFullScreen: Capture(CaptureMode.FullScreen); return true;
                case HotkeyId.CaptureActiveWindow: Capture(CaptureMode.ActiveWindow); return true;
                case HotkeyId.OpenMainWindow: ShowMain(); return true;
            }
        }
        return false;
    }

    private void ShowMain()
    {
        _main.Show();
        _main.SetForeground();
    }

    /// <summary>
    /// Takes a capture, files it, and does whatever the settings say to do with it.
    ///
    /// The main window hides first for a full-screen or window capture - otherwise NexusShot itself
    /// would be in the screenshot, which is the sort of thing that is obvious only after you ship it.
    /// </summary>
    private void Capture(CaptureMode mode)
    {
        var wasVisible = _main.IsVisible;
        if (wasVisible) _main.Hide();

        try
        {
            var path = mode switch
            {
                CaptureMode.Region => CaptureRegion(),
                CaptureMode.FullScreen => ScreenCapture.CaptureFullScreen(),
                CaptureMode.ActiveWindow => ScreenCapture.CaptureActiveWindow(),
                _ => null,
            };
            if (path is null)
            {
                if (wasVisible) ShowMain();
                return;
            }

            var item = Store(path);
            Log.Info("capture", $"{mode} {item.Width}x{item.Height}");

            if (_settings.CopyToClipboardAutomatically) ClipboardImage.Copy(item.FilePath);

            // The capture is filed and on the clipboard, and a quick-access card appears with it.
            // Opening a full editor for every capture would force a heavyweight window (its own D2D
            // device, a full-resolution bitmap) on someone who only wanted to paste - and three
            // captures in a row would leave three of them open. The card is where you act on it.
            _main.AddCapture(item);
            ShowPreview(item);

            // The shell is *not* raised: a capture should not steal focus from what you were doing.
            // The card is non-activating for the same reason.
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or ArgumentOutOfRangeException)
        {
            // A failed capture must not take the app with it: the tray and hotkeys stay alive.
            Log.Error("capture.failed", exception, mode.ToString());
            if (wasVisible) ShowMain();
        }
    }

    private static string? CaptureRegion()
    {
        var region = RegionOverlay.Pick();
        return region is { } bounds ? ScreenCapture.Capture(bounds) : null;
    }

    /// <summary>Moves the temp capture into the screenshot folder and records it.</summary>
    private ScreenshotHistoryItem Store(string temporaryPath)
    {
        // The header, not the pixels: this only needs the dimensions for the history row.
        var (width, height) = ImageSurface.ReadSize(temporaryPath);

        Directory.CreateDirectory(_settings.ScreenshotFolder);
        var name = $"NexusShot {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png";
        var destination = Path.Combine(_settings.ScreenshotFolder, name);

        if (_settings.SaveAutomatically)
        {
            File.Move(temporaryPath, destination, overwrite: true);
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
        var scale = Functions.GetDpiForWindow(_main.Handle) / 96.0;

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
        _editors[item.FilePath] = editor;

        editor.Closed += () =>
        {
            // The editor releases its own device resources on destroy; this just drops our handle.
            _editors.Remove(item.FilePath);

            // The capture may have just been re-saved, so its cached bitmap is the old pixels.
            _main.DropCache(item.FilePath);
            _main.Invalidate();
        };

        // Save As writes a new file; it belongs in the history like any other capture.
        editor.SavedAs += path =>
        {
            if (_history.Any(entry => entry.FilePath == path)) return;

            var (width, height) = ImageSurface.ReadSize(path);
            _main.AddCapture(new ScreenshotHistoryItem
            {
                FilePath = path,
                CapturedAt = DateTimeOffset.Now,
                Width = width,
                Height = height,
            });
        };

        var scale = Functions.GetDpiForWindow(editor.Handle) / 96.0;
        editor.ResizeClient((int)(1180 * scale), (int)(820 * scale));
        editor.Center();
        editor.Show();
        editor.SetForeground();
    }

    private void Exit()
    {
        _storage.SaveHistory(_history);
        _storage.SaveSettings(_settings);
        Log.Info("app.exit");
        Functions.PostQuitMessage(0);
    }

    /// <summary>Re-registers the global shortcuts, and tells the shell which ones another app owns
    /// so the settings pane can say so rather than leaving the user wondering.</summary>
    private void ApplyHotkeys() => _main.ReportHotkeyConflicts(_hotkeys.Apply(_settings));

    /// <summary>The save folder may have moved, so the watcher follows it.</summary>
    private void OnSettingsChanged() => WatchSaveFolder();

    private void WatchSaveFolder()
    {
        _watcher?.Dispose();

        try
        {
            _watcher = new FolderWatcher(_settings.ScreenshotFolder, SyncHistory);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            Log.Error("watcher.failed", exception, _settings.ScreenshotFolder);
            _watcher = null;
        }
    }

    /// <summary>
    /// Reconciles the history with what is actually on disk.
    ///
    /// Deletes and renames made in Explorer drop out; PNGs that appeared there are adopted. The
    /// watcher fires on a background thread, so the work is posted to the UI thread rather than
    /// mutating the list underneath a frame that is drawing it.
    /// </summary>
    private void SyncHistory()
    {
        _main.Post(() =>
        {
            var removed = _history.RemoveAll(item => !File.Exists(item.FilePath));

            var known = _history.Select(item => item.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = 0;

            try
            {
                foreach (var file in Directory.EnumerateFiles(_settings.ScreenshotFolder, "*.png"))
                {
                    if (known.Contains(file)) continue;

                    var (width, height) = ImageSurface.ReadSize(file);
                    _history.Add(new ScreenshotHistoryItem
                    {
                        FilePath = file,
                        CapturedAt = File.GetCreationTime(file),
                        Width = width,
                        Height = height,
                    });
                    added++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Error("history.sync_failed", exception);
            }

            if (removed == 0 && added == 0) return;

            _history.Sort((a, b) => b.CapturedAt.CompareTo(a.CapturedAt));
            _storage.SaveHistory(_history);
            _main.Invalidate();
        });
    }

    public void Dispose()
    {
        foreach (var editor in _editors.Values) editor.Dispose();
        _editors.Clear();

        foreach (var preview in _previews) preview.Dispose();
        _previews.Clear();

        _watcher?.Dispose();
        _hotkeys.Dispose();
        _tray.Dispose();
        _main.Dispose();
    }
}

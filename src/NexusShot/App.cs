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
    private readonly CapturePipeline _pipeline;
    private FolderWatcher? _watcher;

    public App()
    {
        _settings = _storage.LoadSettings();
        _history = _storage.LoadHistory();

        // Captures whose files have been deleted behind our back are dropped on load, so the grid
        // never shows a row that cannot be opened.
        _history.RemoveAll(item => !File.Exists(item.FilePath));

        _main = new MainWindow(_storage, _settings, _history);
        _pipeline = new CapturePipeline(_storage, _settings, _history, _main);
        _main.CaptureRequested += Capture;
        _main.HotkeysChanged += ApplyHotkeys;
        _main.RecordingChanged += SuspendHotkeys;

        var scale = Functions.GetDpiForWindow(_main.Handle) / 96.0;
        _main.ResizeClient((int)(1100 * scale), (int)(720 * scale));
        _main.Center();

        _tray = new TrayIcon(_main.Handle, "NexusShot", AppIcon.Small);
        _hotkeys = new Hotkeys(_main.Handle);
        ApplyHotkeys();

        _main.SettingsChanged += OnSettingsChanged;
        _main.ThemeChanged += RethemeEditors;
        WatchSaveFolder();

        // Rewrites a Run entry from an older build, which had no --startup flag.
        if (_settings.StartWithWindows) Startup.Set(true);

        Log.Info("app.started", $"{_history.Count} captures");

        // The main window's WndProc is the app's message pump: the tray and the hotkeys both post
        // here, which is why they are registered against its handle.
        _main.MessageIntercept = OnMessage;
    }

    /// <summary>A login launch starts in the tray with no window; the hotkeys are live either way.</summary>
    public void Run(bool showWindow = true)
    {
        if (showWindow)
        {
            _main.Show();
            _main.SetForeground();
        }

        using var application = new Application();
        application.Run();
    }

    /// <summary>Returns true when the message was ours.</summary>
    private bool OnMessage(uint message, long wParam, long lParam)
    {
        // A second launch asking us to come to the front.
        if (Platform.SingleInstance.WM_SHOW_EXISTING != 0 && message == Platform.SingleInstance.WM_SHOW_EXISTING)
        {
            ShowMain();
            return true;
        }

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
    /// Takes a capture and hands it to the pipeline, which files it and shows a quick-access card.
    ///
    /// The shell is deliberately *not* hidden first: NexusShot's own window is a legitimate thing to
    /// capture, and a tool that ducks out of the way cannot screenshot itself.
    /// </summary>
    private void Capture(CaptureMode mode)
    {
        try
        {
            // Full-screen and active-window blit straight to pixels, so the clipboard copy uses
            // those instead of decoding the PNG back. Region already returns a path - its pixels are
            // a crop of a snapshot the overlay owns, and that snapshot dies with the overlay.
            DecodedImage? pixels = mode switch
            {
                CaptureMode.FullScreen => ScreenCapture.CaptureFullScreen(),
                CaptureMode.ActiveWindow => ScreenCapture.CaptureActiveWindow(),
                _ => null,
            };

            var path = pixels is not null ? WriteCapture(pixels)
                : mode == CaptureMode.Region ? CaptureRegion()
                : null;
            if (path is null) return;

            _pipeline.Land(path, pixels);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or ArgumentOutOfRangeException)
        {
            // A failed capture must not take the tray and hotkeys with it.
            Log.Error("capture.failed", exception, mode.ToString());
        }
    }

    private static string? CaptureRegion() => RegionOverlay.Pick();

    /// <summary>Writes a freshly blitted capture to the temp PNG the pipeline expects to move.</summary>
    private static string WriteCapture(DecodedImage image)
    {
        var path = Path.Combine(Path.GetTempPath(), $"NexusShot_{Guid.NewGuid():N}.png");
        PngWriter.Write(path, image.Pixels, image.Width, image.Height);
        return path;
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

    /// <summary>Drops the global shortcuts while a binding is being recorded. A registered key is
    /// delivered as WM_HOTKEY, never as a keystroke, so pressing the key you are rebinding would fire
    /// its action and never reach the recorder.</summary>
    private void SuspendHotkeys(bool recording)
    {
        if (recording) _hotkeys.UnregisterAll();
        else ApplyHotkeys();
    }

    /// <summary>The save folder may have moved, so the watcher follows it.</summary>
    private void OnSettingsChanged() => WatchSaveFolder();

    /// <summary>Open editors follow the shell's theme rather than the one they were opened with.</summary>
    private void RethemeEditors() => _pipeline.RethemeEditors();

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
            if (removed != 0) _main.SweepDecoded();

            var known = _history.Select(item => item.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var folder = _settings.ScreenshotFolder;
            _ = Task.Run(() =>
            {
                var candidates = new List<ScreenshotHistoryItem>();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*.png"))
                    {
                        if (known.Contains(file)) continue;
                        try
                        {
                            var (width, height) = ImageSurface.ReadSize(file);
                            candidates.Add(new ScreenshotHistoryItem
                            {
                                FilePath = file,
                                CapturedAt = File.GetCreationTime(file),
                                Width = width,
                                Height = height,
                            });
                        }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Log.Error("history.sync_failed", exception);
                }
                if (candidates.Count == 0 && removed == 0) return;
                _main.Post(() =>
                {
                    var added = 0;
                    foreach (var c in candidates) if (!known.Contains(c.FilePath)) { _history.Add(c); added++; known.Add(c.FilePath); }
                    if (removed == 0 && added == 0) return;
                    _history.Sort((a, b) => b.CapturedAt.CompareTo(a.CapturedAt));
                    _storage.SaveHistory(_history);
                    _main.Invalidate();
                });
            });
        });
    }

    public void Dispose()
    {
        _pipeline.Dispose();

        _watcher?.Dispose();
        _hotkeys.Dispose();
        _tray.Dispose();
        _main.Dispose();
    }
}

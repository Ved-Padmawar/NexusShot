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

        // The watcher only reports changes from here on, so whatever is already in the folder is
        // invisible to it. A reinstall keeps the captures but loses the history file, and pointing
        // the setting at an existing folder is the same situation: without this scan those captures
        // appear only once some later change happens to fire the watcher.
        SyncHistory();

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
                case TrayIcon.Command.CaptureRegion:
                    Capture(CaptureMode.Region);
                    return true;
                case TrayIcon.Command.CaptureFullScreen:
                    Capture(CaptureMode.FullScreen);
                    return true;
                case TrayIcon.Command.CaptureWindow:
                    Capture(CaptureMode.ActiveWindow);
                    return true;
                case TrayIcon.Command.OpenMain:
                    ShowMain();
                    return true;
                case TrayIcon.Command.Exit:
                    Exit();
                    return true;
                default:
                    return true;
            }
        }

        if (message == Hotkeys.WM_HOTKEY)
        {
            switch (_hotkeys.Resolve(wParam))
            {
                case HotkeyId.CaptureRegion:
                    Capture(CaptureMode.Region);
                    return true;
                case HotkeyId.CaptureFullScreen:
                    Capture(CaptureMode.FullScreen);
                    return true;
                case HotkeyId.CaptureActiveWindow:
                    Capture(CaptureMode.ActiveWindow);
                    return true;
                case HotkeyId.OpenMainWindow:
                    ShowMain();
                    return true;
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
    private bool _captureRunning;
    private bool _disposed;

    private void Capture(CaptureMode mode)
    {
        if (_captureRunning) return;
        _captureRunning = true;
        try
        {
            // The desktop must be sampled before focus changes; only encoding/filing is deferred.
            var pixels = mode switch
            {
                CaptureMode.FullScreen => ScreenCapture.CaptureFullScreen(),
                CaptureMode.ActiveWindow => ScreenCapture.CaptureActiveWindow(),
                _ => RegionOverlay.Pick(),
            };
            if (pixels is null) { _captureRunning = false; return; }
            _ = FinishCapture(pixels, _settings.ScreenshotFolder, _settings.SaveAutomatically,
                _settings.CopyToClipboardAutomatically);
        }
        catch (Exception exception)
        {
            _captureRunning = false;
            Log.Error("capture.failed", exception, mode.ToString());
            UserFeedback.Error(_main.Handle, "Could not capture the screen. Please retry.");
        }
    }

    private async Task FinishCapture(DecodedImage pixels, string folder, bool autoSave, bool autoCopy)
    {
        ScreenshotHistoryItem? item = null;
        Exception? failure = null;
        Exception? copyFailure = null;
        try
        {
            item = await MediaWorker.Run(() =>
            {
                var saved = CaptureStore.Save(pixels, folder, autoSave);
                if (autoCopy)
                {
                    try { ClipboardImage.Copy(pixels, saved.FilePath); }
                    catch (Exception exception) { copyFailure = exception; }
                }
                return saved;
            });
        }
        catch (Exception exception) { failure = exception; }
        finally { pixels.Dispose(); }
        _main.Post(() =>
        {
            _captureRunning = false;
            if (_disposed) return;
            if (failure is not null)
            {
                Log.Error("capture.failed", failure);
                UserFeedback.Error(_main.Handle, "Could not save the capture. Check the save folder and available disk space.");
                return;
            }
            try { _pipeline.Land(item!); }
            catch (Exception exception)
            {
                Log.Error("capture.preview_failed", exception);
                UserFeedback.Error(_main.Handle, "The screenshot was saved, but its preview could not be opened.");
            }
            if (copyFailure is not null)
            {
                Log.Error("clipboard.copy", copyFailure);
                UserFeedback.Error(_main.Handle, "The screenshot was saved, but copying failed. Please use Copy to retry.");
            }
        });
    }

    private void Exit()
    {
        if (_captureRunning)
        {
            UserFeedback.Error(_main.Handle, "Please wait for the capture to finish before exiting.");
            return;
        }
        _pipeline.CloseEditors(FinishExit);
    }

    private void FinishExit()
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
    private string? _watchedFolder;

    private void OnSettingsChanged()
    {
        if (string.Equals(_watchedFolder, _settings.ScreenshotFolder, StringComparison.OrdinalIgnoreCase)) return;
        WatchSaveFolder();
        SyncHistory();
    }

    /// <summary>Open editors follow the shell's theme rather than the one they were opened with.</summary>
    private void RethemeEditors() => _pipeline.RethemeEditors();

    private void WatchSaveFolder()
    {
        _watcher?.Dispose();
        _watchedFolder = _settings.ScreenshotFolder;

        try
        {
            _watcher = new FolderWatcher(_settings.ScreenshotFolder, SyncHistory);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
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
    private readonly Dictionary<string, FileVersion> _historyVersions = new(StringComparer.OrdinalIgnoreCase);

    private void SyncHistory()
    {
        _main.Post(() =>
        {
            if (_disposed) return;
            if (_syncRunning) { _syncQueued = true; return; }
            _syncRunning = true;
            _ = ScanHistory(_settings.ScreenshotFolder,
                _history.Select(item => item.FilePath).ToArray(),
                new Dictionary<string, FileVersion>(_historyVersions, StringComparer.OrdinalIgnoreCase));
        });
    }

    private async Task ScanHistory(string folder, string[] known, Dictionary<string, FileVersion> versions)
    {
        HistoryScan? result = null;
        try { result = await Task.Run(() => HistoryScanner.Scan(folder, known, versions)); }
        catch (Exception exception) { Log.Error("history.scan_failed", exception); }
        _main.Post(() =>
        {
            _syncRunning = false;
            if (_disposed) return;
            if (!string.Equals(folder, _settings.ScreenshotFolder, StringComparison.OrdinalIgnoreCase))
                _syncQueued = true;
            else if (result is not null)
            {
                var changed = false;
                foreach (var path in result.Missing)
                {
                    // A capture/save may have recreated this path since the worker observed it.
                    if (File.Exists(path)) continue;
                    changed |= _history.RemoveAll(item => string.Equals(item.FilePath, path, StringComparison.OrdinalIgnoreCase)) != 0;
                    _historyVersions.Remove(path);
                    _main.ForgetMissingCapture(path);
                }
                var live = _history.ToDictionary(item => item.FilePath, StringComparer.OrdinalIgnoreCase);
                foreach (var (candidate, version) in result.Changed)
                {
                    // Reject a file deleted or replaced after the scan. A queued watcher event
                    // will rescan replacements; no old dimensions overwrite a newer save.
                    try
                    {
                        if (FileVersion.Read(candidate.FilePath) != version) { _syncQueued = true; continue; }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    { continue; }
                    _historyVersions[candidate.FilePath] = version;
                    if (live.TryGetValue(candidate.FilePath, out var existing))
                    {
                        existing.Width = candidate.Width;
                        existing.Height = candidate.Height;
                        _main.DropCache(existing.FilePath);
                        _pipeline.RefreshExistingPreview(existing);
                    }
                    else _history.Add(candidate);
                    changed = true;
                }
                if (changed)
                {
                    _main.SweepDecoded();
                    _history.Sort((a, b) => b.CapturedAt.CompareTo(a.CapturedAt));
                    _storage.SaveHistory(_history);
                    _main.Invalidate();
                }
            }
            if (!_syncQueued) return;
            _syncQueued = false;
            SyncHistory();
        });
    }

    /// <summary>Single-flight state for <see cref="SyncHistory"/>. UI-thread only.</summary>
    private bool _syncRunning;
    private bool _syncQueued;

    public void Dispose()
    {
        _disposed = true;
        // Detached before the window goes: a handler that outlives its subscriber can still be
        // reached from a late message, and would run against disposed hotkeys or a dead tray icon.
        _main.CaptureRequested -= Capture;
        _main.HotkeysChanged -= ApplyHotkeys;
        _main.RecordingChanged -= SuspendHotkeys;
        _main.SettingsChanged -= OnSettingsChanged;
        _main.ThemeChanged -= RethemeEditors;

        _pipeline.Dispose();

        _watcher?.Dispose();
        _hotkeys.Dispose();
        _tray.Dispose();
        _main.Dispose();
    }
}

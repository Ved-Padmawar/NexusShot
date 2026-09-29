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
    private readonly TextIndex _texts;
    private readonly TextIndexer _indexer;

    public App()
    {
        _settings = _storage.LoadSettings();
        _history = _storage.LoadHistory();

        _history.RemoveAll(item => !File.Exists(item.FilePath));
        _texts = _storage.LoadTextIndex();

        _main = new MainWindow(_storage, _settings, _history) { Texts = _texts };
        _indexer = new TextIndexer(() => _settings.OcrLanguage, (path, version, language, text) => _main.Post(() => Indexed(path, version, language, text)));
        _pipeline = new CapturePipeline(_storage, _settings, _history, _main);
        _main.CaptureRequested += Capture;
        _main.CaptureTextRequested += CaptureText;
        _main.PasteRequested += Paste;
        _main.CaptureDeleted += _pipeline.ForgetCapture;
        _main.TimedCaptureRequested += TimedCapture;
        _main.HotkeysChanged += ApplyHotkeys;
        _main.InstallRequested += InstallUpdate;
        _main.UnsavedEditors = () => _pipeline.UnsavedEditors;
        _main.RecordingChanged += SuspendHotkeys;

        var scale = Functions.GetDpiForWindow(_main.Handle) / 96.0;
        _main.ResizeClient((int)(1100 * scale), (int)(720 * scale));
        _main.Center();

        _tray = new TrayIcon(_main.Handle, "NexusShot", AppIcon.Small);
        UserFeedback.Tray = _tray;
        _hotkeys = new Hotkeys(_main.Handle);
        ApplyHotkeys();

        _main.SettingsChanged += OnSettingsChanged;
        _main.ThemeChanged += RethemeEditors;
        WatchSaveFolder();

        // The watcher sees only later changes; this picks up captures already in the folder.
        SyncHistory();

        // Rewrites a Run entry from an older build, which had no --startup flag.
        if (_settings.StartWithWindows) Startup.Set(true);
        SessionLifetime.RegisterCrashRestart();

        Log.Info("app.started", $"{_history.Count} captures");

        // The tray and hotkeys post to this window, so its WndProc is the app's message pump.
        _main.MessageIntercept = OnMessage;
    }

    /// <summary>A login launch starts in the tray with no window; the hotkeys are live either way.</summary>
    /// <summary>Starts with what the launch asked for: the Library, a file in the editor, a capture,
    /// or - at sign-in - nothing but the tray.</summary>
    public void Run(LaunchRequest request)
    {
        if (request.ShowsLibrary) _main.Reveal();
        else _main.Post(() => Handle(request));

        _main.ScheduleUpdateChecks();

        using var application = new Application();
        application.Run();
    }

    /// <summary>Returns true when the message was ours; a handled message answers 0.</summary>
    private bool OnMessage(uint message, long wParam, long lParam)
    {
        // Unsaved edits hold a shutdown: saving would overwrite files the user never chose to save.
        if (message == SessionLifetime.WM_QUERYENDSESSION)
        {
            var unsaved = _pipeline.UnsavedEditors;
            if (unsaved == 0) return false;
            SessionLifetime.BlockEnd(_main.Handle, unsaved == 1 ? "An open editor has unsaved changes." : $"{unsaved} open editors have unsaved changes.");
            return true;
        }

        // No Exit runs when the session ends, so write out what it would.
        if (message == SessionLifetime.WM_ENDSESSION)
        {
            SessionLifetime.AllowEnd(_main.Handle);
            if (wParam != 0)
            {
                _storage.SaveHistory(_history);
                _storage.SaveSettings(_settings);
                _storage.SaveTextIndex(_texts);
                Log.Info("app.session_end");
            }
            return false;
        }

        if (message == Platform.SingleInstance.WM_COPYDATA)
        {
            // Read now, while the sender's buffer lives; handled after returning, which frees the sender.
            if (Platform.SingleInstance.ReadForwarded(lParam) is { } request) _main.Post(() => Handle(request));
            return true;
        }

        // A second launch asking us to come to the front.
        if (Platform.SingleInstance.WM_SHOW_EXISTING != 0 && message == Platform.SingleInstance.WM_SHOW_EXISTING)
        {
            ShowMain();
            return true;
        }

        if (TrayIcon.WM_TASKBARCREATED != 0 && message == TrayIcon.WM_TASKBARCREATED)
        {
            _tray.Add();
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
                case TrayIcon.Command.TimedCapture:
                    TimedCapture();
                    return true;
                case TrayIcon.Command.CaptureText:
                    CaptureText();
                    return true;
                case TrayIcon.Command.RestoreClosed:
                    _pipeline.ToggleRestoreStrip();
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
                case HotkeyId.RestoreClosed:
                    _pipeline.ToggleRestoreStrip();
                    return true;
                case HotkeyId.CaptureText:
                    CaptureText();
                    return true;
                case HotkeyId.TimedCapture:
                    TimedCapture();
                    return true;
            }
        }
        return false;
    }

    /// <summary>Opens images in the editor: from Explorer, or handed over by a second launch.</summary>
    public void Open(IReadOnlyList<string> paths) => _pipeline.Open(paths);

    /// <summary>Copied image files open as they are; a copied image is filed in the save folder like
    /// a capture, so it has a name and a place in the Library, and opens in the editor.</summary>
    private void Paste()
    {
        ClipboardReader.Pasted? pasted;
        try { pasted = ClipboardReader.Read(); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException
            or System.Runtime.InteropServices.ExternalException)
        {
            Log.Error("paste.read", exception);
            UserFeedback.Error(_main.Handle, "Could not read the clipboard. Please retry.");
            return;
        }

        if (pasted is null)
        {
            UserFeedback.Info(_main.Handle, "There is no image on the clipboard to open.");
            return;
        }
        if (pasted.Image is not { } pixels)
        {
            Open(pasted.Files);
            return;
        }
        _ = FilePasted(pixels, _settings.ScreenshotFolder, _settings.CaptureFormat);
    }

    private async Task FilePasted(DecodedImage pixels, string folder, ImageFormat format)
    {
        ScreenshotHistoryItem? item = null;
        try { item = await MediaWorker.Run(() => CaptureStore.Save(pixels, folder, format)); }
        catch (Exception exception) { Log.Error("paste.save", exception); }
        finally { pixels.Dispose(); }
        _main.Post(() =>
        {
            if (_disposed) return;
            if (item is null) UserFeedback.Error(_main.Handle, "Could not save the pasted image. Check the save folder and available disk space.");
            else _pipeline.LandInEditor(item);
        });
    }

    private void Handle(LaunchRequest request)
    {
        if (request.File is { } file) Open([file]);
        switch (request.Capture)
        {
            case CaptureCommand.Region: Capture(CaptureMode.Region); break;
            case CaptureCommand.Window: Capture(CaptureMode.ActiveWindow); break;
            case CaptureCommand.Screen: Capture(CaptureMode.FullScreen); break;
            case CaptureCommand.Text: CaptureText(); break;
        }
    }

    private void ShowMain()
    {
        _main.Reveal();
    }

    /// <summary>
    /// Takes a capture and hands it to the pipeline, which files it and shows a quick-access card.
    ///
    /// The shell is deliberately *not* hidden first: NexusShot's own window is a legitimate thing to
    /// capture, and a tool that ducks out of the way cannot screenshot itself.
    /// </summary>
    private bool _captureRunning;
    private bool _disposed;

    /// <summary>What Enter repeats in the next pick. This session only: a region from another day,
    /// or another monitor layout, is rarely the one wanted.</summary>
    private RectInt? _lastRegion;

    private void Capture(CaptureMode mode) => Capture(mode, PickerMode.Region);

    /// <summary>Picks something to read. The picker can still be switched to take an image instead.</summary>
    private void CaptureText() => Capture(CaptureMode.Region, PickerMode.Text);

    /// <summary>The picker, for a region capture, opens in <paramref name="picker"/>'s mode; its result
    /// is filed, or read when the picker ended in Text.</summary>
    private void Capture(CaptureMode mode, PickerMode picker)
    {
        if (_captureRunning) return;
        _captureRunning = true;
        try
        {
            // Sampled before focus changes; a pointer drawn into a text capture would only hide letters.
            var cursor = _settings.IncludeCursor && picker != PickerMode.Text;
            var app = _settings.NameAfterApp ? ForegroundApp.Name() : null;
            DecodedImage pixels;
            if (mode == CaptureMode.FullScreen) pixels = ScreenCapture.CaptureFullScreen(cursor);
            else if (mode == CaptureMode.ActiveWindow) pixels = ScreenCapture.CaptureActiveWindow(cursor);
            else
            {
                if (RegionOverlay.Pick(cursor, Theme, _lastRegion, picker, _settings.ShowMagnifier) is not { } picked)
                {
                    _captureRunning = false;
                    return;
                }
                _lastRegion = picked.Region;
                if (picked.Text)
                {
                    _ = FinishCaptureText(picked.Pixels, _settings.OcrLanguage);
                    return;
                }
                pixels = picked.Pixels;
            }
            if (_settings.ShutterSound) Shutter.Play();

            // "Copy only" means the clipboard is the whole result, whatever the auto-copy setting says.
            _ = FinishCapture(pixels, _settings.ScreenshotFolder,
                _settings.CopyToClipboardAutomatically || _settings.AfterCapture == AfterCapture.CopyOnly,
                _settings.CaptureFormat, app);
        }
        catch (Exception exception)
        {
            _captureRunning = false;
            Log.Error("capture.failed", exception, mode.ToString());
            UserFeedback.Error(_main.Handle, "Could not capture the screen. Please retry.");
        }
    }

    /// <summary>The theme the pickers draw their accents in.</summary>
    private Theme Theme => SystemTheme.Resolve(_settings.Theme, _settings.Accent);

    private CountdownBadge? _countdown;

    /// <summary>Counts down, then captures in the default mode. Asking again while it counts
    /// cancels it.</summary>
    private void TimedCapture()
    {
        if (_countdown is not null)
        {
            _countdown.Cancel();
            return;
        }
        if (_captureRunning) return;

        _countdown = new CountdownBadge(_settings.TimedCaptureSeconds);
        _countdown.Dismissed += () => _countdown = null;
        // Posted: Elapsed fires inside WM_DESTROY, too early for the region picker's own message loop.
        _countdown.Elapsed += () => _main.Post(() => Capture(_settings.DefaultCaptureMode));
        _countdown.Start();
    }

    /// <summary>Picks a region and copies the text in it. Nothing is saved and no card appears: the
    /// text is the result, and a notification says how much there was.</summary>
    private async Task FinishCaptureText(DecodedImage pixels, string? language)
    {
        var lines = 0;
        Exception? failure = null;
        try { lines = await MediaWorker.Run(() => TextRecognition.CopyText(pixels, language)); }
        catch (Exception exception) { failure = exception; }
        finally { pixels.Dispose(); }
        _main.Post(() =>
        {
            _captureRunning = false;
            if (_disposed) return;
            if (failure is not null)
            {
                Log.Error("capture_text.failed", failure);
                UserFeedback.Error(_main.Handle, failure is InvalidOperationException
                    ? failure.Message
                    : "Could not read the text in that area. Please retry.");
            }
            else UserFeedback.Info(_main.Handle, lines == 0
                ? "No text found in that area."
                : $"Copied {lines} line{(lines == 1 ? "" : "s")} of text.");
        });
    }

    private async Task FinishCapture(DecodedImage pixels, string folder, bool autoCopy, ImageFormat format, string? app)
    {
        ScreenshotHistoryItem? item = null;
        Exception? failure = null;
        Exception? copyFailure = null;
        try
        {
            item = await MediaWorker.Run(() =>
            {
                var saved = CaptureStore.Save(pixels, folder, format, app);
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

    /// <summary>Closes the editors, saving or dropping their changes as the restart prompt chose, then
    /// hands over to the installer and exits so it can replace the running exe. The installer starts
    /// NexusShot again when it is done.</summary>
    private void InstallUpdate(string installer, bool saveEditors) => _pipeline.CloseEditors(saveEditors, () =>
    {
        try
        {
            Updater.Install(installer);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error("update.install", exception, installer);
            _main.UpdateFailed(exception is InvalidDataException ? exception.Message : "the installer could not be started");
            return;
        }
        FinishExit();
    });

    private void FinishExit()
    {
        _storage.SaveHistory(_history);
        _storage.SaveSettings(_settings);
        _storage.SaveTextIndex(_texts);
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
        _main.SweepExpired();
        ReadNewText();
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

    private readonly HistorySync _sync = new();

    /// <summary>Reconciles the history with the save folder. The watcher fires on a background thread,
    /// so the work is posted to the UI thread rather than mutating the list under a frame drawing it.</summary>
    private void SyncHistory()
    {
        _main.Post(() =>
        {
            if (_disposed || !_sync.TryBegin()) return;
            _ = ScanHistory(_settings.ScreenshotFolder, _history.Select(item => item.FilePath).ToArray(), _sync.Versions());
        });
    }

    private async Task ScanHistory(string folder, string[] known, Dictionary<string, FileVersion> versions)
    {
        HistoryScan? scan = null;
        try { scan = await Task.Run(() => HistoryScanner.Scan(folder, known, versions)); }
        catch (Exception exception) { Log.Error("history.scan_failed", exception); }
        _main.Post(() =>
        {
            if (_disposed) return;
            var stale = !string.Equals(folder, _settings.ScreenshotFolder, StringComparison.OrdinalIgnoreCase);
            var result = _sync.Complete(_history, scan, stale, File.Exists, FileVersion.Read);
            foreach (var path in result.Removed)
            {
                _main.ForgetMissingCapture(path);
                _pipeline.ForgetCapture(path);
            }
            foreach (var item in result.Refreshed)
            {
                _main.DropCache(item.FilePath);
                _pipeline.RefreshExistingPreview(item);
            }
            if (result.Changed)
            {
                _main.SweepDecoded();
                _storage.SaveHistory(_history);
                _main.Invalidate();
            }
            _main.SweepExpired();
            ReadNewText();
            if (result.ScanAgain) SyncHistory();
        });
    }

    /// <summary>Queues every capture whose text is unread or out of date, newest first.</summary>
    private void ReadNewText()
    {
        if (!_settings.FindTextInCaptures) return;
        _texts.Retain(_history.Select(item => item.FilePath));
        foreach (var item in _history)
        {
            try
            {
                if (!_texts.IsCurrent(item.FilePath, FileVersion.Read(item.FilePath), _settings.OcrLanguage)) _indexer.Enqueue(item.FilePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Saved at most every few seconds while a backlog is read, and at exit.</summary>
    private DateTime _textsSaved;

    private void Indexed(string path, FileVersion version, string? language, string text)
    {
        // A read begun before the language changed is dropped; the change already queued it again.
        if (_disposed || language != _settings.OcrLanguage) return;
        _texts.Set(path, version, language, text);
        if (text.Length > 0) _main.Invalidate();
        if (DateTime.UtcNow - _textsSaved < TimeSpan.FromSeconds(10)) return;
        _textsSaved = DateTime.UtcNow;
        _storage.SaveTextIndex(_texts);
    }

    public void Dispose()
    {
        _disposed = true;
        // Detached first, so a late message cannot reach disposed hotkeys or a dead tray icon.
        _main.CaptureRequested -= Capture;
        _main.CaptureTextRequested -= CaptureText;
        _main.PasteRequested -= Paste;
        _main.CaptureDeleted -= _pipeline.ForgetCapture;
        _main.TimedCaptureRequested -= TimedCapture;
        _main.HotkeysChanged -= ApplyHotkeys;
        _main.RecordingChanged -= SuspendHotkeys;
        _main.SettingsChanged -= OnSettingsChanged;
        _main.ThemeChanged -= RethemeEditors;

        _countdown?.Cancel();
        _pipeline.Dispose();

        _watcher?.Dispose();
        _indexer.Dispose();
        _hotkeys.Dispose();
        UserFeedback.Tray = null;
        _tray.Dispose();
        _main.Dispose();
    }
}

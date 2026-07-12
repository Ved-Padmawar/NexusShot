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

    /// <summary>Editors, keyed by the file they are editing, so a second Edit on the same capture
    /// raises the window that is already open rather than opening another.</summary>
    private readonly Dictionary<string, EditorWindow> _editors = [];

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

        var scale = Functions.GetDpiForWindow(_main.Handle) / 96.0;
        _main.ResizeClient((int)(1100 * scale), (int)(720 * scale));
        _main.Center();

        _tray = new TrayIcon(_main.Handle, "NexusShot", LoadIcon());
        _hotkeys = new Hotkeys(_main.Handle);
        _hotkeys.Apply(_settings);

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
            if (_settings.CopyToClipboardAutomatically) ClipboardImage.Copy(item.FilePath);

            // The capture is filed and on the clipboard; that is usually the whole job. Opening an
            // editor for every capture would force a heavyweight window (its own D2D device and a
            // full-resolution bitmap) on someone who only wanted to paste - and three captures in a
            // row would leave three of them open. Editing is a click away in the shell.
            _main.AddCapture(item);
            ShowMain();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or ArgumentOutOfRangeException)
        {
            // A failed capture must not take the app with it: the tray and hotkeys stay alive.
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

    private void Edit(ScreenshotHistoryItem item)
    {
        if (_editors.TryGetValue(item.FilePath, out var existing))
        {
            existing.Show();
            existing.SetForeground();
            return;
        }

        var editor = new EditorWindow(item.FilePath);
        _editors[item.FilePath] = editor;
        editor.Closed += () =>
        {
            // The editor releases its own device resources on destroy; this just drops our handle.
            _editors.Remove(item.FilePath);

            // The capture may have just been re-saved, so its cached bitmap is the old pixels.
            _main.DropCache(item.FilePath);
            _main.Invalidate();
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
        Functions.PostQuitMessage(0);
    }

    /// <summary>The application icon, so the tray matches the taskbar and Alt+Tab. Resource id 32512
    /// (IDI_APPLICATION) resolves to the exe's own icon once one is embedded.</summary>
    private static IntPtr LoadIcon() =>
        LoadIconW(IntPtr.Zero, 32512);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr instance, nint name);

    public void Dispose()
    {
        foreach (var editor in _editors.Values) editor.Dispose();
        _editors.Clear();

        _hotkeys.Dispose();
        _tray.Dispose();
        _main.Dispose();
    }
}

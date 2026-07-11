using Microsoft.UI.Xaml;
using NexusShot.App.Enums;
using NexusShot.App.Hotkeys;
using NexusShot.App.Native;
using NexusShot.App.Services;
using NexusShot.App.Views;
using WinRT.Interop;

namespace NexusShot.App.Tray;

/// <summary>Native notification-area icon, command menu, and global-hotkey host.</summary>
public sealed class TrayIconService : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = NativeMethods.WmApp + 1;
    private const uint CaptureRegionCommand = 1001;
    private const uint CaptureFullScreenCommand = 1002;
    private const uint CaptureActiveWindowCommand = 1003;
    private const uint OpenWindowCommand = 1004;
    private const uint OpenSettingsCommand = 1005;
    private const uint QuitCommand = 1006;

    private readonly MainWindow _mainWindow;
    private readonly AppServices _services;
    private readonly IntPtr _windowHandle;
    private readonly WindowMessageSubclass _subclass;
    private readonly NativeMethods.NotifyIconData _iconData;
    private bool _visible;
    private bool _disposed;
    private bool _useGuidIdentity = true;

    public TrayIconService(MainWindow mainWindow, AppServices services)
    {
        _mainWindow = mainWindow;
        _services = services;
        _windowHandle = WindowNative.GetWindowHandle(mainWindow);
        _subclass = new WindowMessageSubclass(_windowHandle);
        _subclass.MessageReceived += OnWindowMessage;
        Hotkeys = new GlobalHotkeyService(_windowHandle, _subclass);
        Hotkeys.HotkeyPressed += OnHotkeyPressed;
        _iconData = CreateIconData();
    }

    public GlobalHotkeyService Hotkeys { get; }
    /// <summary>Raised when a caller must show the interactive region-selection overlay.</summary>
    public event EventHandler? RegionCaptureRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? QuitRequested;

    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_visible) return;
        if (!TryAddIcon())
            throw new InvalidOperationException("NexusShot could not add its notification-area icon.");

        var versionData = IconData();
        versionData.uTimeoutOrVersion = NativeMethods.NotifyIconVersion4;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NimSetVersion, ref versionData);
        _visible = true;
        ApplyHotkeys();
    }

    /// <summary>
    /// Adds the icon, working around GUID identity's path binding: Windows ties a NIF_GUID
    /// registration to the exe path that created it, so running from a new location (an upgrade
    /// to a different folder, or a dev build beside an installed copy) fails until the stale
    /// entry is deleted — and if that is not enough, the icon falls back to plain uID identity.
    /// </summary>
    private bool TryAddIcon()
    {
        var addData = IconData();
        if (NativeMethods.Shell_NotifyIcon(NativeMethods.NimAdd, ref addData)) return true;

        var stale = IconData();
        NativeMethods.Shell_NotifyIcon(NativeMethods.NimDelete, ref stale);
        addData = IconData();
        if (NativeMethods.Shell_NotifyIcon(NativeMethods.NimAdd, ref addData)) return true;

        _useGuidIdentity = false;
        addData = IconData();
        return NativeMethods.Shell_NotifyIcon(NativeMethods.NimAdd, ref addData);
    }

    /// <summary>The registration data under the currently effective identity.</summary>
    private NativeMethods.NotifyIconData IconData()
    {
        var data = _iconData;
        if (!_useGuidIdentity)
        {
            data.uFlags &= ~NativeMethods.NifGuid;
            data.guidItem = default;
        }
        return data;
    }

    /// <summary>
    /// (Re-)registers the global shortcuts from current settings and returns the ones another
    /// application already owns. Called at startup and whenever the user edits a shortcut.
    /// </summary>
    public IReadOnlyList<NexusHotkey> ApplyHotkeys()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var failed = Hotkeys.Register(GlobalHotkeyService.GesturesFrom(_services.Settings));
        if (failed.Count > 0)
            _services.Logger.Info("hotkeys.registration_failed", new { hotkeys = string.Join(",", failed) });
        UpdateToolTip(failed.Count == 0
            ? "NexusShot"
            : "NexusShot — one or more shortcuts are unavailable");
        return failed;
    }

    public void UpdateToolTip(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_visible) return;
        var data = IconData();
        data.uFlags = NativeMethods.NifTip | NativeMethods.NifShowTip
            | (_useGuidIdentity ? NativeMethods.NifGuid : 0);
        data.szTip = text.Length <= 127 ? text : text[..127];
        NativeMethods.Shell_NotifyIcon(NativeMethods.NimModify, ref data);
    }

    private NativeMethods.NotifyIconData CreateIconData() => new()
    {
        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        hWnd = _windowHandle,
        uID = IconId,
        uFlags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip
            | NativeMethods.NifShowTip | NativeMethods.NifGuid,
        uCallbackMessage = CallbackMessage,
        hIcon = LoadTrayIcon(),
        szTip = "NexusShot",
        guidItem = new Guid("3FD09C4F-0C79-4BA1-8D24-62CE1E539441"),
    };

    /// <summary>
    /// The app icon at the shell's small-icon size, falling back to the generic system icon rather
    /// than leaving the notification area with no icon at all.
    /// </summary>
    private static IntPtr LoadTrayIcon()
    {
        var icon = Helpers.AppIcon.LoadSmall();
        return icon != IntPtr.Zero
            ? icon
            : NativeMethods.LoadIcon(IntPtr.Zero, (IntPtr)NativeMethods.IdiApplication);
    }

    private void OnWindowMessage(object? sender, NativeWindowMessageEventArgs args)
    {
        if (args.Message == NativeMethods.WmDestroy)
        {
            Dispose();
            return;
        }

        // The only signal an unpackaged WinUI 3 app gets when the OS light/dark setting flips.
        // lParam points at the name of the section that changed; "ImmersiveColorSet" is ours.
        if (args.Message == NativeMethods.WmSettingChange)
        {
            var section = args.LParam == IntPtr.Zero
                ? null
                : System.Runtime.InteropServices.Marshal.PtrToStringUni(args.LParam);
            if (string.Equals(section, "ImmersiveColorSet", StringComparison.Ordinal))
                _services.Theme.OnSystemThemeChanged();
            return;
        }

        if (args.Message != CallbackMessage) return;

        var notification = unchecked((uint)args.LParam.ToInt64() & 0xFFFF);
        if (notification is NativeMethods.WmRButtonUp or NativeMethods.WmContextMenu)
        {
            ShowContextMenu();
            args.Handled = true;
        }
        else if (notification == NativeMethods.WmLButtonUp)
        {
            BringMainWindowToFront();
            args.Handled = true;
        }
    }

    private void ShowContextMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, CaptureRegionCommand, "Capture region");
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, CaptureFullScreenCommand, "Capture full screen");
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, CaptureActiveWindowCommand, "Capture active window");
            NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, 0, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, OpenWindowCommand, "Open NexusShot");
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, OpenSettingsCommand, "Settings");
            NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, 0, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, QuitCommand, "Quit");
            NativeMethods.SetForegroundWindow(_windowHandle);
            if (!NativeMethods.GetCursorPos(out var point)) return;
            var command = NativeMethods.TrackPopupMenuEx(menu, NativeMethods.TpmRightButton | NativeMethods.TpmReturnCmd, point.X, point.Y, _windowHandle, IntPtr.Zero);
            DispatchCommand(command);
        }
        finally { NativeMethods.DestroyMenu(menu); }
    }

    private void DispatchCommand(uint command)
    {
        switch (command)
        {
            case CaptureRegionCommand: RegionCaptureRequested?.Invoke(this, EventArgs.Empty); break;
            case CaptureFullScreenCommand: Capture(CaptureMode.FullScreen); break;
            case CaptureActiveWindowCommand: Capture(CaptureMode.ActiveWindow); break;
            case OpenWindowCommand: BringMainWindowToFront(); break;
            case OpenSettingsCommand: SettingsRequested?.Invoke(this, EventArgs.Empty); break;
            case QuitCommand: QuitRequested?.Invoke(this, EventArgs.Empty); break;
        }
    }

    private void OnHotkeyPressed(object? sender, NexusHotkey hotkey) => DispatchCommand(hotkey switch
    {
        NexusHotkey.CaptureRegion => CaptureRegionCommand,
        NexusHotkey.CaptureFullScreen => CaptureFullScreenCommand,
        NexusHotkey.CaptureActiveWindow => CaptureActiveWindowCommand,
        NexusHotkey.OpenMainWindow => OpenWindowCommand,
        _ => 0,
    });

    private async void Capture(CaptureMode mode)
    {
        try { await _mainWindow.BeginCaptureAsync(mode); }
        catch { UpdateToolTip("NexusShot — capture failed"); }
    }

    private void BringMainWindowToFront()
    {
        _mainWindow.ShowDashboard();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hotkeys.HotkeyPressed -= OnHotkeyPressed;
        Hotkeys.Dispose();
        _subclass.MessageReceived -= OnWindowMessage;
        if (_visible)
        {
            var data = IconData();
            NativeMethods.Shell_NotifyIcon(NativeMethods.NimDelete, ref data);
            _visible = false;
        }
        _subclass.Dispose();
        GC.SuppressFinalize(this);
    }
}

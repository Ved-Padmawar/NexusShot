using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>
/// The notification-area icon and its menu.
///
/// The tray is how a capture tool is actually used: the main window is somewhere to review history,
/// but the hotkeys and this menu are the app most of the time. It stays alive independently of any
/// window, so closing the main window hides it rather than exiting.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;

    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;

    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;

    /// <summary>The private message the icon posts to our window for every mouse event on it.</summary>
    public const uint WM_TRAY = 0x0400 + 1;   // WM_APP + 1

    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_LBUTTONDBLCLK = 0x0203;

    private readonly IntPtr _window;
    private readonly uint _id;
    private bool _added;

    public TrayIcon(IntPtr window, string tooltip, IntPtr icon)
    {
        _window = window;
        _id = 1;

        var data = Build(tooltip, icon);
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    /// <summary>The menu items, in order. The index the user picked comes back from Show.</summary>
    public enum Command
    {
        None = 0,
        CaptureRegion = 1,
        CaptureFullScreen = 2,
        CaptureWindow = 3,
        OpenMain = 4,
        Exit = 5,
    }

    /// <summary>
    /// Handles a tray message. Returns the command the user chose, if any.
    ///
    /// A double-click opens the main window - the convention every tray app follows - and a right
    /// click opens the menu.
    /// </summary>
    public Command OnMessage(long lParam)
    {
        var message = (uint)(lParam & 0xFFFF);
        return message switch
        {
            WM_LBUTTONDBLCLK => Command.OpenMain,
            WM_RBUTTONUP => ShowMenu(),
            _ => Command.None,
        };
    }

    private Command ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return Command.None;

        try
        {
            AppendMenuW(menu, MF_STRING, (nuint)Command.CaptureRegion, "Capture region");
            AppendMenuW(menu, MF_STRING, (nuint)Command.CaptureFullScreen, "Capture full screen");
            AppendMenuW(menu, MF_STRING, (nuint)Command.CaptureWindow, "Capture active window");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, (nuint)Command.OpenMain, "Open NexusShot");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, (nuint)Command.Exit, "Exit");

            // The window must be foreground or the menu will not dismiss when clicked away - a
            // documented quirk of tray menus that every tray app has to work around.
            SetForegroundWindow(_window);
            GetCursorPos(out var cursor);

            var chosen = TrackPopupMenu(
                menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, cursor.X, cursor.Y, 0, _window, IntPtr.Zero);

            return (Command)chosen;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private NOTIFYICONDATAW Build(string tooltip, IntPtr icon) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window,
        uID = _id,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_TRAY,
        hIcon = icon,
        szTip = tooltip,
    };

    public void Dispose()
    {
        if (!_added) return;
        _added = false;

        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _window,
            uID = _id,
        };
        Shell_NotifyIconW(NIM_DELETE, ref data);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, nuint id, string? item);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(
        IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rect);

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
}

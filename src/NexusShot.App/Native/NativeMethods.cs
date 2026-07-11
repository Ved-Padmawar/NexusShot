using System.Runtime.InteropServices;

namespace NexusShot.App.Native;

internal static class NativeMethods
{
    internal const int GwlWndProc = -4;
    internal const uint WmHotkey = 0x0312;
    internal const uint WmApp = 0x8000;
    internal const uint WmCommand = 0x0111;
    internal const uint WmContextMenu = 0x007B;
    internal const uint WmRButtonUp = 0x0205;
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmDestroy = 0x0002;
    internal const uint WmNcDestroy = 0x0082;
    internal const uint WmSettingChange = 0x001A;
    internal const uint NifMessage = 0x00000001;
    internal const uint NifIcon = 0x00000002;
    internal const uint NifTip = 0x00000004;
    internal const uint NifGuid = 0x00000020;
    /// <summary>Required for the standard tooltip once NOTIFYICON_VERSION_4 is selected;
    /// without it the shell shows no hover text at all.</summary>
    internal const uint NifShowTip = 0x00000080;
    internal const uint NimAdd = 0x00000000;
    internal const uint NimModify = 0x00000001;
    internal const uint NimDelete = 0x00000002;
    internal const uint NimSetVersion = 0x00000004;
    internal const uint NotifyIconVersion4 = 4;
    internal const uint MfString = 0x00000000;
    internal const uint MfSeparator = 0x00000800;
    internal const uint TpmRightButton = 0x0002;
    internal const uint TpmReturnCmd = 0x0100;
    internal const int IdiApplication = 32512;
    internal const int SwHide = 0;
    internal const int SwRestore = 9;
    internal const int SwShowNoActivate = 4;

    internal const int GwlStyle = -16;
    internal const int GwlExStyle = -20;
    internal const int WsExNoActivate = 0x08000000;
    internal const int WsExToolWindow = 0x00000080;
    /// <summary>WS_CAPTION (WS_BORDER | WS_DLGFRAME) and WS_THICKFRAME: every frame-drawing style bit.</summary>
    internal const long WsCaption = 0x00C00000;
    internal const long WsThickFrame = 0x00040000;

    internal const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    /// <summary>DPI of the monitor hosting the window. 96 == 100% scaling.</summary>
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    internal static readonly IntPtr HwndTopmost = new(-1);
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpFrameChanged = 0x0020;
    internal const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    internal const int DwmwaWindowCornerPreference = 33;
    internal const int DwmwcpRound = 2;
    internal const int DwmwaBorderColor = 34;
    /// <summary>DWMWA_COLOR_NONE: suppresses the thin border DWM paints around rounded windows.</summary>
    internal const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);

    /// <summary>Paints the non-client area (titlebar, borders) dark. Windows 10 2004+.</summary>
    internal const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newLong);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CallWindowProc(IntPtr previousWindowProc, IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    internal const uint WmSetIcon = 0x0080;
    internal static readonly IntPtr IconSmall = IntPtr.Zero;
    internal static readonly IntPtr IconBig = new(1);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    internal const uint ImageIcon = 1;
    internal const uint LrDefaultColor = 0x0000;
    internal const uint LrShared = 0x8000;
    internal const int SmCxIcon = 11;
    internal const int SmCyIcon = 12;
    internal const int SmCxSmIcon = 49;
    internal const int SmCySmIcon = 50;

    /// <summary>
    /// Loads an icon at an exact size. <see cref="LoadIcon"/> always returns SM_CXICON (32px) and
    /// the shell then downscales it for the 16px notification area, which looks soft.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr reserved);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyMenu(IntPtr menu);

    internal const int WsExLayered = 0x00080000;
    internal const uint LwaAlpha = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out Point point);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X; public int Y; }
}

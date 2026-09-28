using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>The raw HWND calls shared by more than one window. One declaration each, here, so two
/// callers cannot drift to different signatures for the same function.</summary>
public static partial class WindowInterop
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(
        IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(IntPtr window, ref POINT point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetDoubleClickTime();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nuint SetTimer(IntPtr window, nuint id, uint elapse, IntPtr callback);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(IntPtr window, nuint id);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(IntPtr window, out RECT client);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr window, out RECT bounds);

    [LibraryImport("user32.dll", EntryPoint = "IsZoomed", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomedWindow(IntPtr window);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial nint GetWindowLongPtrW(IntPtr window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtrW(IntPtr window, int index, nint value);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr SetCapture(IntPtr window);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr GetCapture();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW", SetLastError = true)]
    public static partial IntPtr DefWindowProcW(IntPtr window, uint msg, nuint wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowText(IntPtr window, string text);

    /// <summary>An int-valued DWM attribute; true when DWM accepted it. Older builds refuse newer
    /// attributes, which is the caller's to handle.</summary>
    public static bool SetDwmAttribute(IntPtr window, int attribute, int value) =>
        DwmSetWindowAttribute(window, attribute, ref value, sizeof(int)) == 0;

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_CLOAK = 13;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMWCP_ROUND = 2;

    /// <summary>Waits for DWM's next composition, so a frame just drawn is on screen.</summary>
    [LibraryImport("dwmapi.dll")]
    public static partial int DwmFlush();

    /// <summary>The whole window as DWM glass, for a system backdrop to show through.</summary>
    public static bool ExtendFrameIntoClientArea(IntPtr window)
    {
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        return DwmExtendFrameIntoClientArea(window, ref margins) == 0;
    }

    /// <summary>Asks for WM_MOUSELEAVE, which Windows does not send unless a window opts in.</summary>
    public static bool TrackMouseLeave(IntPtr window)
    {
        var track = new TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = 0x00000002,   // TME_LEAVE
            hwndTrack = window,
        };
        return TrackMouseEvent(ref track);
    }

    /// <summary>Fades a WS_EX_LAYERED window as a whole.</summary>
    public static bool SetLayeredAlpha(IntPtr window, byte alpha) =>
        SetLayeredWindowAttributes(window, 0, alpha, 0x00000002);   // LWA_ALPHA

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(IntPtr window, ref MARGINS margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TrackMouseEvent(ref TRACKMOUSEEVENT track);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(IntPtr window, uint key, byte alpha, uint flags);
}

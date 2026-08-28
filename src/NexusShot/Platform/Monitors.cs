using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>
/// Monitor geometry.
///
/// The *work* area, not the monitor bounds: it excludes the taskbar, which is where the quick-access
/// cards would otherwise sit underneath. Coordinates are physical pixels, because the manifest
/// declares PerMonitorV2 - so a multi-monitor desktop with mixed scale factors needs no correction.
/// </summary>
public static partial class Monitors
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>The work area of the monitor a window is on.</summary>
    public static RectInt WorkArea(IntPtr window)
    {
        var monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
        return WorkAreaOf(monitor);
    }

    /// <summary>The work area of the monitor the pointer is on - which is where a capture the user
    /// just took belongs, rather than wherever the shell happens to be.</summary>
    public static RectInt WorkAreaUnderCursor()
    {
        GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        return WorkAreaOf(monitor);
    }

    private static RectInt WorkAreaOf(IntPtr monitor)
    {
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };

        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info))
            return ScreenCapture.VirtualDesktop;

        var work = info.rcWork;
        return new RectInt(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromPoint(POINT point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    public static double DpiScaleUnderCursor(IntPtr fallbackWindow)
    {
        GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        if (TryGetDpiForMonitor(monitor, out var dpi)) return dpi / 96.0;
        return Functions.GetDpiForWindow(fallbackWindow) / 96.0;
    }

    private static bool TryGetDpiForMonitor(IntPtr monitor, out uint dpi)
    {
        dpi = 96;
        try
        { var hr = GetDpiForMonitor(monitor, 0, out var dx, out _); if (hr == 0) { dpi = dx; return true; } }
        catch { }
        return false;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}

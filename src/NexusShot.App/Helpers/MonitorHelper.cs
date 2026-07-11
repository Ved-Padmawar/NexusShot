using NexusShot.App.Native;
using Windows.Graphics;

namespace NexusShot.App.Helpers;

public static class MonitorHelper
{
    /// <summary>
    /// Work area of the monitor under the cursor, in physical pixels. Excludes the taskbar,
    /// so overlays anchored to the bottom edge are not hidden behind it.
    /// </summary>
    public static RectInt32 GetWorkArea()
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) cursor = new NativeMethods.Point { X = 0, Y = 0 };
        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);

        var info = new NativeMethods.MonitorInfo { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
            return new RectInt32(0, 0, 1920, 1080);

        var work = info.rcWork;
        return new RectInt32(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
    }
}

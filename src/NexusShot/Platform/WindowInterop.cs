using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>
/// The handful of raw HWND calls needed by more than one window (<c>CaptionWindow</c>,
/// <c>FloatingPreview</c>, <c>RegionOverlay</c>). One declaration each, here, rather than every
/// window re-declaring the same P/Invoke signature.
/// </summary>
public static class WindowInterop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr window, ref POINT point);

    [DllImport("user32.dll")]
    public static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern nuint SetTimer(IntPtr window, nuint id, uint elapse, IntPtr callback);

    [DllImport("user32.dll")]
    public static extern bool KillTimer(IntPtr window, nuint id);
}

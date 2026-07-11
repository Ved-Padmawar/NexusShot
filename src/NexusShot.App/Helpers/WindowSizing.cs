using Microsoft.UI.Windowing;
using NexusShot.App.Native;

namespace NexusShot.App.Helpers;

internal static class WindowSizing
{
    /// <summary>
    /// Sizes a window in device-independent pixels. <see cref="AppWindow.Resize"/> takes physical
    /// pixels, so passing DIP constants straight through opens the window undersized on any
    /// display scaled above 100%.
    /// </summary>
    public static void ResizeDips(AppWindow window, IntPtr handle, int widthDips, int heightDips)
    {
        var scale = NativeMethods.GetDpiForWindow(handle) / 96.0;
        window.Resize(new Windows.Graphics.SizeInt32(
            (int)Math.Round(widthDips * scale),
            (int)Math.Round(heightDips * scale)));
    }
}

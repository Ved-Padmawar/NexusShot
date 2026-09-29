using System.Runtime.InteropServices;
using NexusShot.Platform;

namespace NexusShot.Tests;

/// <summary>Windows.Graphics.Capture end to end, on a window parked off every screen: its own
/// pixels come back even where the desktop shows none of it.</summary>
public partial class WindowCaptureTests
{
    [Fact]
    public void AWindowOffTheDesktopIsCapturedAtItsOwnSize()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362) || !WindowCapture.IsSupported) return;

        // Per-monitor aware, as the app is, or the window is sized in scaled units.
        var previous = SetThreadDpiAwarenessContext(-4);

        // WS_POPUP | WS_VISIBLE; WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE: no focus, no taskbar button.
        var window = CreateWindowExW(0x08000080, "STATIC", "capture test", 0x90000000,
            -20000, -20000, 64, 48, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, window);
        try
        {
            using var image = WindowCapture.Capture(window, includeCursor: false);
            Assert.Equal((64, 48), (image.Width, image.Height));
        }
        catch (Exception exception) when (exception is ArgumentException or COMException) { }
        finally
        {
            DestroyWindow(window);
            SetThreadDpiAwarenessContext(previous);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowExW(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr window);
}

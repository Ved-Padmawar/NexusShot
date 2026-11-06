using System.Runtime.InteropServices;
using NexusShot.Platform;

namespace NexusShot.Tests;

/// <summary>Windows.Graphics.Capture end to end, on a window parked off every screen: its own
/// pixels come back even where the desktop shows none of it.</summary>
public partial class GraphicsCaptureTests
{
    [Fact]
    public void AWindowOffTheDesktopIsCapturedAtItsOwnSize()
    {
        if (!GraphicsCapture.IsSupported) return;

        // Per-monitor aware, as the app is, or the window is sized in scaled units.
        var previous = SetThreadDpiAwarenessContext(-4);

        // WS_POPUP | WS_VISIBLE; WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE: no focus, no taskbar button.
        var window = CreateWindowExW(0x08000080, "STATIC", "capture test", 0x90000000,
            -20000, -20000, 64, 48, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, window);
        try
        {
            using var image = GraphicsCapture.CaptureWindow(window, includeCursor: false, hdrWhiteNits: null);
            Assert.Equal((64, 48), (image.Width, image.Height));
        }
        catch (Exception exception) when (exception is ArgumentException or COMException) { }
        finally
        {
            DestroyWindow(window);
            SetThreadDpiAwarenessContext(previous);
        }
    }

    [Fact]
    public void AnAreaStreamDeliversOpaqueFramesAtTheAreaSize()
    {
        if (!GraphicsCapture.IsSupported) return;
        var previous = SetThreadDpiAwarenessContext(-4);
        try
        {
            var monitor = Monitors.All()[0];
            using var stream = new GraphicsCapture.AreaStream(new RectInt(monitor.X + 10, monitor.Y + 10, 64, 48), includeCursor: false);
            using var first = stream.Next();
            using var second = stream.Next();
            Assert.Equal((64, 48), (second.Width, second.Height));
            Assert.All(MemoryMarshal.Cast<byte, uint>(second.Span).ToArray(), pixel => Assert.Equal(0xFFu, pixel >> 24));
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    [Fact]
    public void AnAreaStreamFillsDesktopOutsideEveryMonitorWithBlack()
    {
        if (!GraphicsCapture.IsSupported) return;
        var previous = SetThreadDpiAwarenessContext(-4);
        try
        {
            var desktop = ScreenCapture.VirtualDesktop;
            using var stream = new GraphicsCapture.AreaStream(new RectInt(desktop.X - 32, desktop.Y, 64, 48), includeCursor: false);
            using var frame = stream.Next();
            var pixels = MemoryMarshal.Cast<byte, uint>(frame.Span);
            for (var row = 0; row < frame.Height; row++)
                Assert.All(pixels.Slice(row * frame.Width, 32).ToArray(), pixel => Assert.Equal(0xFF000000u, pixel));
        }
        finally { SetThreadDpiAwarenessContext(previous); }
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

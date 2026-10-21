using System.Runtime.InteropServices;
using DirectN;
using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot.Tests;

public partial class CompositionLayersTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposedDrawingContextReleasesNativeReferences(bool scroll)
    {
        var window = CreateWindowExW(0, "STATIC", "composition test", 0x00CF0000,
            0, 0, 64, 48, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, window);
        try
        {
            using var layers = new CompositionLayers(window);
            layers.Resize(64, 48, new Rgba(255, 255, 255));
            if (scroll) layers.PlaceScroll(new Rect(0, 0, 64, 48), 64, 48, 0);

            ID2D1DeviceContext native;
            using (var context = scroll
                ? layers.BeginScroll(new RECT { right = 64, bottom = 48 }, new Point(0, 0))
                : layers.BeginChrome())
            {
                native = context.Object;
            }
            if (scroll) layers.EndScroll(); else layers.EndChrome();

            // Keeping the managed wrapper alive must not keep its disposed native context alive.
            Assert.Throws<ObjectDisposedException>(() => native.GetDpi(out _, out _));
        }
        finally
        {
            DestroyWindow(window);
        }
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowExW(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr window);
}

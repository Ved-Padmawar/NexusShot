using System.Runtime.InteropServices;
using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot.Platform;

/// <summary>
/// Screen capture, via GDI BitBlt.
///
/// Coordinates are physical pixels: the manifest opts into PerMonitorV2, so the virtual desktop
/// metrics and window rects are already unscaled and no DPI correction is needed anywhere here.
///
/// The XAML build used System.Drawing for the blit and the PNG encode. This does the blit with
/// GDI directly and the encode with WIC, which is one fewer dependency in a single-file AOT exe.
/// </summary>
public static class ScreenCapture
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const int BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    /// <summary>1 GiB at 32bpp: a sanity ceiling, not a real limit anyone reaches.</summary>
    private const long MaximumPixels = 268_435_456;

    public static RectInt VirtualDesktop => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN));

    public static string CaptureFullScreen() => Capture(VirtualDesktop);

    public static string CaptureRegion(RectInt region) => Capture(Intersect(region, VirtualDesktop));

    public static string CaptureActiveWindow()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rect))
            throw new InvalidOperationException("Could not determine the active window.");

        return Capture(Intersect(
            new RectInt(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top),
            VirtualDesktop));
    }

    /// <summary>Blits the region and writes it to a temp PNG. Returns the path.</summary>
    public static unsafe string Capture(RectInt bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds), "The capture area must have positive dimensions.");
        if ((long)bounds.Width * bounds.Height > MaximumPixels)
            throw new ArgumentOutOfRangeException(nameof(bounds), "The requested capture area is too large.");

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new InvalidOperationException("Could not open a screen device context.");

        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero) throw new InvalidOperationException("Could not create a capture device context.");

            // A top-down 32bpp DIB, so the bits come back in the layout WIC wants without a flip.
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = bounds.Width,
                biHeight = -bounds.Height,      // negative: top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            };

            bitmap = CreateDIBSection(memory, ref header, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                throw new InvalidOperationException("Could not allocate the capture bitmap.");

            var previous = SelectObject(memory, bitmap);
            try
            {
                // CAPTUREBLT includes layered windows, which is what makes a capture match what the
                // user can actually see.
                if (!BitBlt(memory, 0, 0, bounds.Width, bounds.Height,
                        screen, bounds.X, bounds.Y, SRCCOPY | CAPTUREBLT))
                    throw new InvalidOperationException("The screen copy failed.");
            }
            finally
            {
                SelectObject(memory, previous);
            }

            var stride = bounds.Width * 4;
            var pixels = new byte[stride * bounds.Height];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            // BitBlt leaves the alpha byte as garbage; the desktop is opaque, so force it.
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

            var path = Path.Combine(Path.GetTempPath(), $"NexusShot_{Guid.NewGuid():N}.png");
            PngWriter.Write(path, pixels, bounds.Width, bounds.Height);
            return path;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static RectInt Intersect(RectInt requested, RectInt available)
    {
        var left = Math.Max(requested.X, available.X);
        var top = Math.Max(requested.Y, available.Y);
        var right = Math.Min((long)requested.X + requested.Width, (long)available.X + available.Width);
        var bottom = Math.Min((long)requested.Y + requested.Height, (long)available.Y + available.Height);

        if (right <= left || bottom <= top)
            throw new ArgumentOutOfRangeException(nameof(requested), "The capture area is outside the virtual desktop.");

        return new RectInt(left, top, checked((int)(right - left)), checked((int)(bottom - top)));
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, int rop);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr dc, ref BITMAPINFOHEADER header, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }
}

/// <summary>An integer rectangle, for screen coordinates.</summary>
public readonly record struct RectInt(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public Rect ToRect() => new(X, Y, Width, Height);
}

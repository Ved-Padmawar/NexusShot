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
/// The blit uses GDI directly and the encode uses WIC, which is one fewer dependency in a
/// single-file AOT exe.
/// </summary>
public static partial class ScreenCapture
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
        WindowInterop.GetSystemMetrics(SM_XVIRTUALSCREEN),
        WindowInterop.GetSystemMetrics(SM_YVIRTUALSCREEN),
        WindowInterop.GetSystemMetrics(SM_CXVIRTUALSCREEN),
        WindowInterop.GetSystemMetrics(SM_CYVIRTUALSCREEN));

    public static DecodedImage CaptureFullScreen(bool includeCursor) => Capture(VirtualDesktop, includeCursor);

    /// <summary>
    /// Blits the foreground window.
    ///
    /// The DWM extended frame is preferred over <c>GetWindowRect</c>: the latter includes the drop
    /// shadow, which lands as a band of desktop around the window.
    /// </summary>
    public static DecodedImage CaptureActiveWindow(bool includeCursor)
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            throw new InvalidOperationException("Could not determine the active window.");
        RectInt winRect;
        if (DwmGetWindowAttribute(window, 9, out var dwmRect, Marshal.SizeOf<RECT>()) == 0)
            winRect = new RectInt(dwmRect.Left, dwmRect.Top, dwmRect.Right - dwmRect.Left, dwmRect.Bottom - dwmRect.Top);
        else if (WindowInterop.GetWindowRect(window, out var rect))
            winRect = new RectInt(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        else throw new InvalidOperationException("Could not determine the active window.");
        return Capture(Intersect(winRect, VirtualDesktop), includeCursor);
    }

    /// <summary>
    /// Blits the region and hands back the pixels, premultiplied BGRA and top-down - the format
    /// both <see cref="ImageWriter"/> and <see cref="ImageSurface.Upload"/> already take.
    ///
    /// Returning pixels rather than a path is deliberate: the region picker needs the same bitmap
    /// three times over (to display, to crop, to encode), and routing it through a temp PNG meant
    /// re-decoding a full virtual desktop for each one. Callers that want a file encode it
    /// themselves, once.
    /// </summary>
    public static unsafe DecodedImage Capture(RectInt bounds, bool includeCursor = false)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds), "The capture area must have positive dimensions.");
        if ((long)bounds.Width * bounds.Height > MaximumPixels)
            throw new ArgumentOutOfRangeException(nameof(bounds), "The requested capture area is too large.");

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
            throw new InvalidOperationException("Could not open a screen device context.");

        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero)
                throw new InvalidOperationException("Could not create a capture device context.");

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
                // CAPTUREBLT includes layered windows, so the capture matches what is on screen.
                if (!BitBlt(memory, 0, 0, bounds.Width, bounds.Height,
                        screen, bounds.X, bounds.Y, SRCCOPY | CAPTUREBLT))
                    throw new InvalidOperationException("The screen copy failed.");
            }
            finally
            {
                // GDI copies never contain the cursor (a hardware overlay), so it is drawn in after the blit.
                if (includeCursor) DrawCursor(memory, bounds);
                SelectObject(memory, previous);
            }

            var image = DecodedImage.Allocate(bounds.Width, bounds.Height);
            var pixels = image.Span;
            unsafe { new ReadOnlySpan<byte>((void*)bits, image.ByteLength).CopyTo(pixels); }

            // BitBlt leaves the alpha byte as garbage; the desktop is opaque, so force it.
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

            return image;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Draws the visible cursor at its hotspot-corrected position. A hidden cursor (a game,
    /// a video in full screen) is left out, as the user sees it.</summary>
    private static unsafe void DrawCursor(IntPtr dc, RectInt bounds)
    {
        var info = new CURSORINFO { cbSize = (uint)sizeof(CURSORINFO) };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero) return;
        if (!GetIconInfo(info.hCursor, out var icon)) return;

        // GetIconInfo hands back bitmaps the caller owns.
        if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
        if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);

        DrawIconEx(dc,
            info.ptScreenPos.X - (int)icon.xHotspot - bounds.X,
            info.ptScreenPos.Y - (int)icon.yHotspot - bounds.Y,
            info.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
    }

    private const uint CURSOR_SHOWING = 0x00000001;
    private const uint DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hCursor;
        public WindowInterop.POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(ref CURSORINFO info);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetIconInfo(IntPtr icon, out ICONINFO info);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height,
        uint step, IntPtr flickerFreeBrush, uint flags);

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

    [LibraryImport("user32.dll", SetLastError = true)] private static partial IntPtr GetForegroundWindow();
    [LibraryImport("dwmapi.dll", SetLastError = true)] private static partial int DwmGetWindowAttribute(IntPtr window, int attr, out WindowInterop.RECT value, int size);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial IntPtr GetDC(IntPtr window);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial int ReleaseDC(IntPtr window, IntPtr dc);

    [LibraryImport("gdi32.dll", SetLastError = true)] private static partial IntPtr CreateCompatibleDC(IntPtr dc);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr dc);
    [LibraryImport("gdi32.dll", SetLastError = true)] private static partial IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr obj);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(
        IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, int rop);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial IntPtr CreateDIBSection(
        IntPtr dc, ref BITMAPINFOHEADER header, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

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

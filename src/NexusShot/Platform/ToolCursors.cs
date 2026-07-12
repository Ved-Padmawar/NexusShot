using System.Runtime.InteropServices;
using NexusShot.Core;

namespace NexusShot.Platform;

/// <summary>
/// The editor's cursors. The brush and eraser get a real HCURSOR sized to the stroke rather than a
/// ring drawn into the scene: Windows composites the cursor, so it tracks the pointer exactly, where
/// anything the app paints arrives a frame late and trails. Cached - building one allocates a DIB.
/// </summary>
public static class ToolCursors
{
    private const int IDC_ARROW = 32512;
    private const int IDC_CROSS = 32515;
    private const int IDC_SIZEALL = 32646;
    private const int IDC_SIZENWSE = 32642;
    private const int IDC_SIZENESW = 32643;
    private const int IDC_SIZEWE = 32644;
    private const int IDC_SIZENS = 32645;

    private static readonly Dictionary<int, IntPtr> System = [];
    private static readonly Dictionary<(int Size, uint Colour), IntPtr> Circles = [];
    private static IntPtr _pencil;

    public static IntPtr Arrow => Standard(IDC_ARROW);
    public static IntPtr Cross => Standard(IDC_CROSS);

    public static IntPtr Resize(ResizeHandle handle) => Standard(handle switch
    {
        ResizeHandle.TopLeft or ResizeHandle.BottomRight => IDC_SIZENWSE,
        ResizeHandle.TopRight or ResizeHandle.BottomLeft => IDC_SIZENESW,
        ResizeHandle.Top or ResizeHandle.Bottom => IDC_SIZENS,
        ResizeHandle.Left or ResizeHandle.Right => IDC_SIZEWE,
        _ => IDC_SIZEALL,
    });

    private static IntPtr Standard(int id)
    {
        if (System.TryGetValue(id, out var cached)) return cached;
        var cursor = LoadCursorW(IntPtr.Zero, id);
        System[id] = cursor;
        return cursor;
    }

    /// <summary>
    /// The brush/eraser cursor: a ring of the stroke's true on-screen diameter, so what you see is
    /// exactly what you will paint.
    ///
    /// Windows caps a cursor at the system metric (typically 32px), so a large brush would silently
    /// be drawn small and lie about its size. Past that, it falls back to a crosshair - honest,
    /// where a wrong-sized ring is not.
    /// </summary>
    public static IntPtr Circle(double diameter, Rgba fill)
    {
        var size = (int)Math.Round(diameter);
        var maximum = GetSystemMetrics(13);   // SM_CXCURSOR

        if (size < 6 || size > maximum) return Cross;

        var key = (size, Pack(fill));
        if (Circles.TryGetValue(key, out var cached)) return cached;

        var cursor = BuildCircle(size, fill);
        if (cursor == IntPtr.Zero) return Cross;

        Circles[key] = cursor;
        return cursor;
    }

    /// <summary>A pencil, for the pen tool.</summary>
    public static IntPtr Pencil()
    {
        if (_pencil != IntPtr.Zero) return _pencil;
        _pencil = BuildPencil();
        return _pencil == IntPtr.Zero ? Cross : _pencil;
    }

    /// <summary>A ring: dark outer stroke, white inner, so it reads on any content. Hotspot centred.</summary>
    private static unsafe IntPtr BuildCircle(int size, Rgba fill)
    {
        var pixels = new uint[size * size];
        var centre = (size - 1) / 2.0;
        var outer = size / 2.0;

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var distance = Math.Sqrt(Square(x - centre) + Square(y - centre));

            // Two concentric strokes plus a translucent fill, matching the on-canvas footprint.
            uint colour;
            if (distance > outer) continue;
            else if (distance > outer - 1.2) colour = Pack(Rgba.Black.WithAlpha(190));
            else if (distance > outer - 2.4) colour = Pack(Rgba.White.WithAlpha(230));
            else colour = Pack(fill);

            if (colour == 0) continue;
            pixels[y * size + x] = colour;
        }

        return FromPixels(pixels, size, size, size / 2, size / 2);
    }

    /// <summary>A pencil glyph, drawn as a diagonal nib with its point at the hotspot.</summary>
    private static IntPtr BuildPencil()
    {
        const int size = 32;
        var pixels = new uint[size * size];

        var ink = Pack(Rgba.White.WithAlpha(255));
        var edge = Pack(Rgba.Black.WithAlpha(220));

        // The body: a thick diagonal from the tip (bottom-left) up to the top-right.
        for (var i = 0; i < 22; i++)
        {
            var x = 3 + i;
            var y = 28 - i;

            for (var w = -3; w <= 3; w++)
            {
                var px = x + w;
                var py = y + w;
                if (px < 0 || px >= size || py < 0 || py >= size) continue;

                // Outline the body so it reads on white content too.
                pixels[py * size + px] = Math.Abs(w) >= 3 ? edge : ink;
            }
        }

        // The tip: a small solid wedge at the hotspot.
        for (var y = 26; y < 31; y++)
        for (var x = 1; x < 6 - (y - 26); x++)
            pixels[y * size + x] = x <= 1 || y >= 30 ? edge : ink;

        return FromPixels(pixels, size, size, 2, 29);
    }

    /// <summary>Builds an HCURSOR from premultiplied BGRA pixels.</summary>
    private static unsafe IntPtr FromPixels(uint[] pixels, int width, int height, int hotX, int hotY)
    {
        var header = new BITMAPV5HEADER
        {
            bV5Size = (uint)sizeof(BITMAPV5HEADER),
            bV5Width = width,
            bV5Height = -height,      // top-down
            bV5Planes = 1,
            bV5BitCount = 32,
            bV5Compression = 3,       // BI_BITFIELDS
            bV5RedMask = 0x00FF0000,
            bV5GreenMask = 0x0000FF00,
            bV5BlueMask = 0x000000FF,
            bV5AlphaMask = 0xFF000000,
        };

        var screen = GetDC(IntPtr.Zero);
        var colour = CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, screen);

        if (colour == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;

        // The mask is unused for a 32-bit cursor, but CreateIconIndirect still requires one.
        var mask = CreateBitmap(width, height, 1, 1, IntPtr.Zero);

        try
        {
            fixed (uint* source = pixels)
                Buffer.MemoryCopy(source, (void*)bits, pixels.Length * 4, pixels.Length * 4);

            var info = new ICONINFO
            {
                fIcon = false,        // a cursor, not an icon
                xHotspot = hotX,
                yHotspot = hotY,
                hbmMask = mask,
                hbmColor = colour,
            };

            return CreateIconIndirect(ref info);
        }
        finally
        {
            DeleteObject(colour);
            DeleteObject(mask);
        }
    }

    /// <summary>Premultiplied BGRA, which is what a 32-bit cursor DIB expects.</summary>
    private static uint Pack(Rgba c)
    {
        var a = c.A / 255.0;
        return ((uint)c.A << 24)
            | ((uint)(c.R * a) << 16)
            | ((uint)(c.G * a) << 8)
            | (uint)(c.B * a);
    }

    private static double Square(double v) => v * v;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPV5HEADER
    {
        public uint bV5Size;
        public int bV5Width;
        public int bV5Height;
        public ushort bV5Planes;
        public ushort bV5BitCount;
        public uint bV5Compression;
        public uint bV5SizeImage;
        public int bV5XPelsPerMeter;
        public int bV5YPelsPerMeter;
        public uint bV5ClrUsed;
        public uint bV5ClrImportant;
        public uint bV5RedMask;
        public uint bV5GreenMask;
        public uint bV5BlueMask;
        public uint bV5AlphaMask;
        public uint bV5CSType;
        public CIEXYZTRIPLE bV5Endpoints;
        public uint bV5GammaRed;
        public uint bV5GammaGreen;
        public uint bV5GammaBlue;
        public uint bV5Intent;
        public uint bV5ProfileData;
        public uint bV5ProfileSize;
        public uint bV5Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CIEXYZTRIPLE { public CIEXYZ red, green, blue; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CIEXYZ { public int x, y, z; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr instance, nint name);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr dc, ref BITMAPV5HEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bits, IntPtr data);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}

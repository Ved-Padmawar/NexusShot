using System.Runtime.InteropServices;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// Puts an image on the clipboard as a DIB.
///
/// CF_DIB is what every Windows app actually reads for a pasted image, so the bytes are a
/// BITMAPINFOHEADER followed by bottom-up BGRA rows. Alpha is flattened onto white first: a DIB's
/// alpha channel is widely ignored, and a paste that came out with black fringes would be worse
/// than one that came out opaque.
/// </summary>
internal static class ClipboardImage
{
    private const uint CF_DIB = 8;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static void Copy(string pngPath)
    {
        var (pixels, width, height) = ImageSurface.Decode(pngPath);
        var dib = BuildDib(pixels, width, height);

        if (!OpenClipboard(IntPtr.Zero)) return;
        try
        {
            EmptyClipboard();

            var memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)dib.Length);
            if (memory == IntPtr.Zero) return;

            var target = GlobalLock(memory);
            if (target == IntPtr.Zero)
            {
                GlobalFree(memory);
                return;
            }

            try { Marshal.Copy(dib, 0, target, dib.Length); }
            finally { GlobalUnlock(memory); }

            // The clipboard takes ownership on success; freeing it here would be a double free.
            if (SetClipboardData(CF_DIB, memory) == IntPtr.Zero) GlobalFree(memory);
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>A 32-bit BITMAPINFOHEADER plus bottom-up rows, alpha composited onto white.</summary>
    private static byte[] BuildDib(byte[] premultipliedBgra, int width, int height)
    {
        const int headerSize = 40;
        var stride = width * 4;
        var dib = new byte[headerSize + stride * height];

        void WriteInt(int offset, int value) =>
            BitConverter.GetBytes(value).CopyTo(dib, offset);
        void WriteShort(int offset, short value) =>
            BitConverter.GetBytes(value).CopyTo(dib, offset);

        WriteInt(0, headerSize);
        WriteInt(4, width);
        WriteInt(8, height);          // positive: bottom-up
        WriteShort(12, 1);            // planes
        WriteShort(14, 32);           // bits per pixel
        WriteInt(16, 0);              // BI_RGB
        WriteInt(20, stride * height);

        for (var y = 0; y < height; y++)
        {
            // Bottom-up: the last source row is the first destination row.
            var source = (height - 1 - y) * stride;
            var destination = headerSize + y * stride;

            for (var x = 0; x < stride; x += 4)
            {
                var alpha = premultipliedBgra[source + x + 3];

                // Premultiplied over white: c + (255 - a). Opaque pixels (the common case for a
                // screenshot) pass through untouched.
                var inverse = 255 - alpha;
                dib[destination + x] = (byte)Math.Min(255, premultipliedBgra[source + x] + inverse);
                dib[destination + x + 1] = (byte)Math.Min(255, premultipliedBgra[source + x + 1] + inverse);
                dib[destination + x + 2] = (byte)Math.Min(255, premultipliedBgra[source + x + 2] + inverse);
                dib[destination + x + 3] = 255;
            }
        }
        return dib;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}

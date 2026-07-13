using System.Runtime.InteropServices;
using NexusShot.Render;

namespace NexusShot.Platform;

/// <summary>
/// Puts an image on the clipboard.
///
/// Three formats, because no one of them is read by everything: "PNG" carries real alpha and is what
/// Clipboard History prefers, CF_DIBV5 is the Win32 format that also carries alpha, and CF_DIB is
/// what every app can still paste. The DIBs are flattened onto white, whose alpha is widely ignored.
///
/// Every format is placed by value. Delay-rendered data would stay owned by this process, and the
/// shell's Clipboard History (Win+V) would never record the entry at all.
/// </summary>
internal static class ClipboardImage
{
    private const uint CF_DIB = 8;
    private const uint CF_DIBV5 = 17;
    private const uint GMEM_MOVEABLE = 0x0002;

    private const int BITMAPINFOHEADER_SIZE = 40;
    private const int BITMAPV5HEADER_SIZE = 124;

    private const uint BI_RGB = 0;
    private const uint BI_BITFIELDS = 3;
    private const uint LCS_sRGB = 0x73524742;   // 'sRGB'

    /// <summary>The shell registers "PNG" by name; the atom is stable for the session.</summary>
    private static readonly uint CF_PNG = RegisterClipboardFormatW("PNG");

    public static void Copy(string pngPath)
    {
        byte[] pixels;
        int width, height;
        try
        {
            (pixels, width, height) = ImageSurface.Decode(pngPath);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return;
        }

        if (width <= 0 || height <= 0) return;

        if (!OpenClipboard(IntPtr.Zero)) return;
        try
        {
            EmptyClipboard();

            if (CF_PNG != 0)
            {
                try { Place(CF_PNG, File.ReadAllBytes(pngPath)); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Losing the lossless format still leaves the DIBs below.
                }
            }

            Place(CF_DIBV5, BuildDib(pixels, width, height, v5: true));
            Place(CF_DIB, BuildDib(pixels, width, height, v5: false));
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Copies a block onto the clipboard, which takes ownership on success.</summary>
    private static void Place(uint format, byte[] bytes)
    {
        var memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
        if (memory == IntPtr.Zero) return;

        var target = GlobalLock(memory);
        if (target == IntPtr.Zero)
        {
            GlobalFree(memory);
            return;
        }

        try { Marshal.Copy(bytes, 0, target, bytes.Length); }
        finally { GlobalUnlock(memory); }

        // The clipboard owns it on success; freeing it here would be a double free.
        if (SetClipboardData(format, memory) == IntPtr.Zero) GlobalFree(memory);
    }

    /// <summary>A packed DIB: header, then bottom-up 32-bit rows, alpha composited onto white.
    /// <paramref name="v5"/> emits a BITMAPV5HEADER, which states the channel masks and colour space
    /// rather than leaving the reader to assume them.</summary>
    private static byte[] BuildDib(byte[] premultipliedBgra, int width, int height, bool v5)
    {
        var headerSize = v5 ? BITMAPV5HEADER_SIZE : BITMAPINFOHEADER_SIZE;
        var stride = width * 4;
        var dib = new byte[headerSize + stride * height];

        void WriteInt(int offset, int value) => BitConverter.GetBytes(value).CopyTo(dib, offset);
        void WriteUInt(int offset, uint value) => BitConverter.GetBytes(value).CopyTo(dib, offset);
        void WriteShort(int offset, short value) => BitConverter.GetBytes(value).CopyTo(dib, offset);

        WriteInt(0, headerSize);
        WriteInt(4, width);
        WriteInt(8, height);          // positive: bottom-up
        WriteShort(12, 1);            // planes
        WriteShort(14, 32);           // bits per pixel
        WriteUInt(16, v5 ? BI_BITFIELDS : BI_RGB);
        WriteInt(20, stride * height);

        if (v5)
        {
            // The channel masks, in the BGRA order the rows below are written in.
            WriteUInt(40, 0x00FF0000);   // red
            WriteUInt(44, 0x0000FF00);   // green
            WriteUInt(48, 0x000000FF);   // blue
            WriteUInt(52, 0xFF000000);   // alpha
            WriteUInt(56, LCS_sRGB);     // colour space
        }

        for (var y = 0; y < height; y++)
        {
            // Bottom-up: the last source row is the first destination row.
            var source = (height - 1 - y) * stride;
            var destination = headerSize + y * stride;

            for (var x = 0; x < stride; x += 4)
            {
                var alpha = premultipliedBgra[source + x + 3];

                // Premultiplied over white: c + (255 - a). Opaque pixels - the common case for a
                // screenshot - pass through untouched.
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

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string format);

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

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
internal static partial class ClipboardImage
{
    private const uint CF_DIB = 8;
    private const uint CF_DIBV5 = 17;
    private const uint GMEM_MOVEABLE = 0x0002;

    private const int BITMAPINFOHEADER_SIZE = 40;
    private const int BITMAPV5HEADER_SIZE = 124;

    private const uint BI_RGB = 0;
    private const uint BI_BITFIELDS = 3;
    private const uint LCS_sRGB = 0x73524742;   // 'sRGB'

    private const int OpenAttempts = 5;
    private const int OpenRetryDelayMs = 15;

    /// <summary>The shell registers "PNG" by name; the atom is stable for the session.</summary>
    private static readonly uint CF_PNG = RegisterClipboardFormatW("PNG");

    /// <summary>Decodes the file, then copies it. For callers that only have a path.</summary>
    public static void Copy(string pngPath)
    {
        DecodedImage image;
        try
        {
            image = ImageSurface.Decode(pngPath);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return;
        }

        using (image) Copy(image, pngPath);
    }

    /// <summary>
    /// Copies pixels the caller already holds.
    ///
    /// The capture path has the bitmap in memory before it ever reaches a file, so decoding the PNG
    /// back again just to build the DIBs was a full re-decode of every capture.
    /// <paramref name="pngPath"/> is read only for the lossless "PNG" format; pass null to place
    /// the DIBs alone.
    /// </summary>
    public static void Copy(DecodedImage image, string? pngPath)
    {
        var width = image.Width;
        var height = image.Height;

        if (width <= 0 || height <= 0) return;

        if (!TryOpenClipboard()) return;
        try
        {
            EmptyClipboard();

            if (CF_PNG != 0 && pngPath is not null)
            {
                try
                { Place(CF_PNG, File.ReadAllBytes(pngPath)); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Losing the lossless format still leaves the DIBs below.
                }
            }

            PlaceDib(CF_DIBV5, image.Span, width, height, v5: true);
            PlaceDib(CF_DIB, image.Span, width, height, v5: false);
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// The clipboard is a single system-wide resource, and any process holding it makes
    /// OpenClipboard fail outright. Clipboard managers and Office hold it for a few milliseconds at
    /// a time, so one attempt loses that race often enough to drop captures.
    /// </summary>
    private static bool TryOpenClipboard()
    {
        for (var attempt = 0; ; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            if (attempt == OpenAttempts - 1) return false;

            Thread.Sleep(OpenRetryDelayMs << attempt);
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

        try
        { Marshal.Copy(bytes, 0, target, bytes.Length); }
        finally { GlobalUnlock(memory); }

        // The clipboard owns it on success; freeing it here would be a double free.
        if (SetClipboardData(format, memory) == IntPtr.Zero) GlobalFree(memory);
    }

    /// <summary>
    /// Builds a packed DIB directly inside the clipboard's own moveable block, which the clipboard
    /// then takes ownership of.
    ///
    /// A full-screen DIB is several megabytes, so staging it in a byte[] first put one array per
    /// format straight onto the large object heap and left it there until the next gen-2 collection.
    /// </summary>
    private static void PlaceDib(uint format, ReadOnlySpan<byte> premultipliedBgra, int width, int height, bool v5)
    {
        var headerSize = v5 ? BITMAPV5HEADER_SIZE : BITMAPINFOHEADER_SIZE;
        var stride = width * 4;

        var memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)(headerSize + stride * height));
        if (memory == IntPtr.Zero) return;

        var target = GlobalLock(memory);
        if (target == IntPtr.Zero)
        {
            GlobalFree(memory);
            return;
        }

        try
        {
            unsafe
            {
                WriteDib(new Span<byte>((void*)target, headerSize + stride * height),
                    premultipliedBgra, width, height, headerSize, stride, v5);
            }
        }
        finally { GlobalUnlock(memory); }

        if (SetClipboardData(format, memory) == IntPtr.Zero) GlobalFree(memory);
    }

    /// <summary><paramref name="v5"/> emits a BITMAPV5HEADER, which states the channel masks and
    /// colour space rather than leaving the reader to assume them. Rows are bottom-up 32-bit, with
    /// alpha composited onto white.</summary>
    private static void WriteDib(Span<byte> dib, ReadOnlySpan<byte> premultipliedBgra,
        int width, int height, int headerSize, int stride, bool v5)
    {
        var header = dib;
        BitConverter.TryWriteBytes(header[0..], headerSize);
        BitConverter.TryWriteBytes(header[4..], width);
        BitConverter.TryWriteBytes(header[8..], height);            // positive: bottom-up
        BitConverter.TryWriteBytes(header[12..], (short)1);         // planes
        BitConverter.TryWriteBytes(header[14..], (short)32);        // bits per pixel
        BitConverter.TryWriteBytes(header[16..], v5 ? BI_BITFIELDS : BI_RGB);
        BitConverter.TryWriteBytes(header[20..], stride * height);

        if (v5)
        {
            // The channel masks, in the BGRA order the rows below are written in.
            BitConverter.TryWriteBytes(header[40..], 0x00FF0000u);   // red
            BitConverter.TryWriteBytes(header[44..], 0x0000FF00u);   // green
            BitConverter.TryWriteBytes(header[48..], 0x000000FFu);   // blue
            BitConverter.TryWriteBytes(header[52..], 0xFF000000u);   // alpha
            BitConverter.TryWriteBytes(header[56..], LCS_sRGB);      // colour space
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
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr owner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetClipboardData(uint format, IntPtr data);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string format);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalLock(IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalFree(IntPtr memory);
}

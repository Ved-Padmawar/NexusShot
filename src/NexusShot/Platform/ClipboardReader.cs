using System.Runtime.InteropServices;
using NexusShot.Render;

namespace NexusShot.Platform;

/// <summary>What a paste can open: an image placed on the clipboard, or image files copied in
/// Explorer.</summary>
internal static partial class ClipboardReader
{
    private const uint CF_DIB = 8;
    private const uint CF_HDROP = 15;
    private const uint CF_DIBV5 = 17;
    private const uint BI_BITFIELDS = 3;

    private static readonly uint CF_PNG = RegisterClipboardFormatW("PNG");

    /// <summary>Exactly one of <paramref name="Image"/> and <paramref name="Files"/> is set; the caller
    /// owns the image.</summary>
    public sealed record Pasted(DecodedImage? Image, IReadOnlyList<string> Files);

    /// <summary>Null when the clipboard holds neither. "PNG" is preferred, then the DIB with alpha, then
    /// the plain DIB every app can place. The bytes are copied out and the clipboard released before
    /// any decode, so another app is never kept waiting on it.</summary>
    public static Pasted? Read()
    {
        if (!ClipboardWriter.TryOpenClipboard(IntPtr.Zero)) throw new InvalidOperationException("The clipboard is busy. Please retry.");
        byte[]? png = null;
        byte[]? dib = null;
        try
        {
            if (GetClipboardData(CF_HDROP) is var drop && drop != IntPtr.Zero)
                return new Pasted(null, FileDrop.Read(drop));
            png = CF_PNG == 0 ? null : Bytes(CF_PNG);
            if (png is null) dib = Bytes(CF_DIBV5) ?? Bytes(CF_DIB);
        }
        finally { CloseClipboard(); }

        if (png is not null) return new Pasted(ImageSurface.Decode(new MemoryStream(png)), []);
        if (dib is not null) return new Pasted(ImageSurface.Decode(new MemoryStream(AsBitmapFile(dib))), []);
        return null;
    }

    private static unsafe byte[]? Bytes(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        var size = (int)GlobalSize(handle);
        var data = GlobalLock(handle);
        if (data == IntPtr.Zero) return null;
        try { return new ReadOnlySpan<byte>((void*)data, size).ToArray(); }
        finally { GlobalUnlock(handle); }
    }

    /// <summary>A packed DIB is a .bmp file without its 14-byte file header, whose one real field is
    /// where the pixels start: past the info header, the three masks a BI_BITFIELDS DIB with the
    /// small header appends, and any palette.</summary>
    internal static byte[] AsBitmapFile(ReadOnlySpan<byte> dib)
    {
        var headerSize = BitConverter.ToUInt32(dib);
        var bitCount = BitConverter.ToUInt16(dib[14..]);
        var compression = BitConverter.ToUInt32(dib[16..]);
        var colorsUsed = BitConverter.ToUInt32(dib[32..]);
        var masks = compression == BI_BITFIELDS && headerSize == 40 ? 12u : 0u;
        var colors = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1u << bitCount : 0u;
        var pixels = 14 + headerSize + masks + colors * 4;

        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.TryWriteBytes(file.AsSpan(2), (uint)file.Length);
        BitConverter.TryWriteBytes(file.AsSpan(10), pixels);
        dib.CopyTo(file.AsSpan(14));
        return file;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetClipboardData(uint format);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("kernel32.dll")]
    private static partial nuint GlobalSize(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalLock(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr memory);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string format);
}

using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>Unicode text on the clipboard: the inline text editor's cut/copy/paste, and recognised
/// text.</summary>
internal static partial class ClipboardText
{
    private const uint CF_UNICODETEXT = 13;

    /// <summary>Throws when the clipboard could not be written.</summary>
    public static void Copy(string text)
    {
        if (text.Length == 0) return;
        ClipboardWriter.Write(() => ClipboardWriter.Place(CF_UNICODETEXT, (text.Length + 1) * 2, block =>
        {
            MemoryMarshal.AsBytes(text.AsSpan()).CopyTo(block);
            block[^2..].Clear();
        }));
    }

    public static string? Paste()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
        if (!OpenClipboard(IntPtr.Zero)) return null;

        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return null;

            var source = GlobalLock(handle);
            if (source == IntPtr.Zero) return null;

            try
            {
                return Marshal.PtrToStringUni(source);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetClipboardData(uint format);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalLock(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr memory);
}

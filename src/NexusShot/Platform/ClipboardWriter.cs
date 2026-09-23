using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>
/// Every clipboard write: a real owner, retries while another process holds it, and an exception on
/// failure, so a copy that did not happen is never reported as done.
/// </summary>
internal static partial class ClipboardWriter
{
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int OpenAttempts = 5;
    private const int OpenRetryDelayMs = 15;

    /// <summary>Opens and empties the clipboard, runs <paramref name="place"/> to put formats on
    /// it, and closes it again.</summary>
    public static void Write(Action place)
    {
        // EmptyClipboard needs a real owner before SetClipboardData. This hidden message-only
        // window is created on the copying thread and owns no delayed-rendered data.
        var owner = CreateWindowExW(0, "STATIC", "NexusShot clipboard", 0,
            0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (owner == IntPtr.Zero) throw new InvalidOperationException("Could not create the clipboard owner.");
        try
        {
            if (!TryOpenClipboard(owner)) throw new InvalidOperationException("The clipboard is busy. Please retry.");
            try
            {
                if (!EmptyClipboard()) throw new InvalidOperationException("Could not clear the clipboard.");
                place();
            }
            finally { CloseClipboard(); }
        }
        finally { DestroyWindow(owner); }
    }

    public delegate void Fill(Span<byte> block);

    /// <summary>Puts a block on the open clipboard, written in place by <paramref name="fill"/>: a
    /// byte[] staging copy would land on the large object heap, which an idle app never collects.</summary>
    public static unsafe void Place(uint format, int length, Fill fill)
    {
        var memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)length);
        if (memory == IntPtr.Zero) throw new InvalidOperationException("Could not allocate clipboard data.");

        var target = GlobalLock(memory);
        if (target == IntPtr.Zero)
        {
            GlobalFree(memory);
            throw new InvalidOperationException("Could not lock clipboard data.");
        }

        try { fill(new Span<byte>((void*)target, length)); }
        catch
        {
            GlobalUnlock(memory);
            GlobalFree(memory);
            throw;
        }
        GlobalUnlock(memory);

        // The clipboard owns it on success; freeing it here would be a double free.
        if (SetClipboardData(format, memory) == IntPtr.Zero)
        {
            GlobalFree(memory);
            throw new InvalidOperationException("Could not publish clipboard data.");
        }
    }

    /// <summary>A file's bytes, read straight into the clipboard block.</summary>
    public static void PlaceFile(uint format, string path)
    {
        using var file = File.OpenRead(path);
        Place(format, checked((int)file.Length), block => file.ReadExactly(block));
    }

    /// <summary>
    /// The clipboard is a single system-wide resource, and any process holding it makes
    /// OpenClipboard fail outright. Clipboard managers and Office hold it for a few milliseconds at
    /// a time, so one attempt loses that race often enough to drop captures.
    /// </summary>
    private static bool TryOpenClipboard(IntPtr owner)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (OpenClipboard(owner)) return true;
            if (attempt == OpenAttempts - 1) return false;

            Thread.Sleep(OpenRetryDelayMs << attempt);
        }
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowExW(uint extendedStyle, string className, string title,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr window);

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

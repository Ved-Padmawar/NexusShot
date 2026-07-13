using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>Unicode text on the clipboard, for the inline text editor's cut/copy/paste.</summary>
internal static class ClipboardText
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static void Copy(string text)
    {
        if (text.Length == 0) return;
        if (!OpenClipboard(IntPtr.Zero)) return;

        try
        {
            EmptyClipboard();

            var bytes = (text.Length + 1) * 2;
            var memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (memory == IntPtr.Zero) return;

            var target = GlobalLock(memory);
            if (target == IntPtr.Zero)
            {
                GlobalFree(memory);
                return;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * 2, 0);
            }
            finally
            {
                GlobalUnlock(memory);
            }

            // The clipboard owns the block once this succeeds; freeing it here would double-free.
            if (SetClipboardData(CF_UNICODETEXT, memory) == IntPtr.Zero) GlobalFree(memory);
        }
        finally
        {
            CloseClipboard();
        }
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

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}

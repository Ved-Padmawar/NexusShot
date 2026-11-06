using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>Files dropped from Explorer, via WM_DROPFILES: enough for a window that only takes files.</summary>
internal static partial class FileDrop
{
    public const uint WM_DROPFILES = 0x0233;

    public static void Accept(IntPtr window) => DragAcceptFiles(window, true);

    /// <summary>The dropped paths. Frees the drop handle, so call it once per WM_DROPFILES.</summary>
    public static string[] Paths(IntPtr drop)
    {
        try { return Read(drop); }
        finally { DragFinish(drop); }
    }

    /// <summary>The paths in a drop handle someone else owns - the clipboard's, for a paste - which is
    /// left for its owner to free.</summary>
    public static unsafe string[] Read(IntPtr drop)
    {
        var count = DragQueryFileW(drop, uint.MaxValue, null, 0);
        var paths = new string[count];
        for (uint i = 0; i < count; i++)
        {
            var length = DragQueryFileW(drop, i, null, 0);
            var buffer = new char[length + 1];
            fixed (char* text = buffer) DragQueryFileW(drop, i, text, length + 1);
            paths[i] = new string(buffer, 0, (int)length);
        }
        return paths;
    }

    [LibraryImport("shell32.dll")]
    private static partial void DragAcceptFiles(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool accept);

    [LibraryImport("shell32.dll")]
    private static unsafe partial uint DragQueryFileW(IntPtr drop, uint index, char* file, uint size);

    [LibraryImport("shell32.dll")]
    private static partial void DragFinish(IntPtr drop);
}

using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>Deletes through the shell, so a capture removed by mistake can be restored from the
/// Recycle Bin rather than being gone.</summary>
internal static partial class RecycleBin
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    /// <summary>Throws <see cref="IOException"/> when the file could not go. On a drive without a
    /// Recycle Bin the shell deletes outright, as Explorer does after its own prompt - ours already
    /// asked.</summary>
    public static unsafe void Delete(string path)
    {
        // pFrom is double-null-terminated; the string's own terminator is the second.
        fixed (char* from = path + '\0')
        {
            var operation = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                pFrom = (nint)from,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            var result = SHFileOperationW(ref operation);
            if (result != 0 || operation.fAnyOperationsAborted != 0)
                throw new IOException($"The shell could not delete the file (0x{result:X}).");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHFILEOPSTRUCTW
    {
        public nint hwnd;
        public uint wFunc;
        public nint pFrom;
        public nint pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public nint hNameMappings;
        public nint lpszProgressTitle;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHFileOperationW(ref SHFILEOPSTRUCTW operation);
}

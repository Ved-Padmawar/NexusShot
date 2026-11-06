using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>The app in front, by the name its maker gave it - "Google Chrome", not "chrome" - for a
/// capture's file name.</summary>
internal static partial class ForegroundApp
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Null for NexusShot itself - its own Library or tray is in front when a capture starts
    /// from them - and for a process that cannot be read.</summary>
    public static unsafe string? Name()
    {
        var window = WindowInterop.GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var id) == 0 || id == Environment.ProcessId)
            return null;

        // Limited information is granted even for an elevated process, where opening it fully is not.
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, id);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = stackalloc char[1024];
            var length = 1024u;
            if (!QueryFullProcessImageNameW(process, 0, buffer, ref length)) return null;
            var path = new string(buffer, 0, (int)length);
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            return string.IsNullOrWhiteSpace(description) ? Path.GetFileNameWithoutExtension(path) : description;
        }
        catch (FileNotFoundException) { return null; }
        finally { CloseHandle(process); }
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(IntPtr process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}

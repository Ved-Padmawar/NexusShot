using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>
/// Keeps one NexusShot to a session.
///
/// The app lives in the tray, so launching it again is how a user "opens" it. A second process would
/// take no global hotkey - the first already owns them all - and would then report every shortcut as
/// belonging to another app. It hands its request to the first instead, and exits.
/// </summary>
public static partial class SingleInstance
{
    private const string MutexName = @"Local\NexusShot.SingleInstance";

    /// <summary>The main window's title, which is also how a second launch finds it.</summary>
    public const string MainWindowTitle = "NexusShot";

    public const uint WM_COPYDATA = 0x004A;

    /// <summary>Marks our WM_COPYDATA, so another sender's data is never read as a path.</summary>
    private const nint OpenFileTag = 0x4E53_4F46;

    /// <summary>Broadcast by a second instance; the running one shows its window.</summary>
    public static readonly uint WM_SHOW_EXISTING = RegisterWindowMessageW("NexusShot.ShowExisting");

    private static Mutex? _mutex;

    /// <summary>
    /// True when this process is the one that gets to run. False means another instance already has
    /// it, and has been handed <paramref name="file"/> to open, or asked to come to the front - the
    /// caller should exit.
    /// </summary>
    public static bool Claim(string? file)
    {
        // Ownership comes from the wait, not the constructor: an instance that was killed leaves the
        // mutex abandoned but still named, so construction reports created: false and only the wait
        // reports the abandonment. That wait is a success - the owner is gone, and this process
        // inherits the mutex it was handed rather than constructing a second one.
        _mutex = new Mutex(initiallyOwned: false, MutexName);

        bool owned;
        try { owned = _mutex.WaitOne(TimeSpan.Zero, exitContext: false); }
        catch (AbandonedMutexException) { owned = true; }

        if (owned) return true;

        _mutex.Dispose();
        _mutex = null;

        if ((file is null || !SendFile(file)) && WM_SHOW_EXISTING != 0)
            PostMessageW(HWND_BROADCAST, WM_SHOW_EXISTING, IntPtr.Zero, IntPtr.Zero);

        return false;
    }

    /// <summary>Hands a path to the running instance's main window.</summary>
    private static unsafe bool SendFile(string file)
    {
        var window = FindWindowW(null, MainWindowTitle);
        if (window == IntPtr.Zero) return false;

        // Pass on our foreground right, or the editor opens behind Explorer.
        GetWindowThreadProcessId(window, out var process);
        AllowSetForegroundWindow(process);

        fixed (char* text = file)
        {
            var data = new COPYDATASTRUCT { dwData = OpenFileTag, cbData = (uint)(file.Length * 2), lpData = (IntPtr)text };
            SendMessageW(window, WM_COPYDATA, IntPtr.Zero, (IntPtr)(&data));
            return true;
        }
    }

    /// <summary>The path from a WM_COPYDATA another launch sent, or null if it is not one.</summary>
    public static unsafe string? ReadForwardedFile(long lParam)
    {
        var data = (COPYDATASTRUCT*)lParam;
        if (data == null || data->dwData != OpenFileTag || data->lpData == IntPtr.Zero) return null;
        return new string((char*)data->lpData, 0, (int)(data->cbData / 2));
    }

    /// <summary>Releases ownership before disposing. Disposing alone leaves the mutex abandoned, and
    /// the next instance would take it through the exception path rather than cleanly.</summary>
    public static void Release()
    {
        if (_mutex is null) return;

        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { } // Not the owner - already released, or claimed on another thread.

        _mutex.Dispose();
        _mutex = null;
    }

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public nint dwData;
        public uint cbData;
        public IntPtr lpData;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string message);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowW(string? className, string title);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint process);
}

using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>
/// Keeps one NexusShot to a session.
///
/// The app lives in the tray, so launching it again is how a user "opens" it. A second process would
/// take no global hotkey - the first already owns them all - and would then report every shortcut as
/// belonging to another app. It asks the first to show itself instead, and exits.
/// </summary>
public static partial class SingleInstance
{
    private const string MutexName = @"Local\NexusShot.SingleInstance";

    /// <summary>Broadcast by a second instance; the running one shows its window.</summary>
    public static readonly uint WM_SHOW_EXISTING = RegisterWindowMessageW("NexusShot.ShowExisting");

    private static Mutex? _mutex;

    /// <summary>
    /// True when this process is the one that gets to run. False means another instance already has
    /// it, and has been asked to come to the front - the caller should exit.
    /// </summary>
    public static bool Claim()
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

        // Not a message to a known window: the running instance's handle is not knowable from here,
        // and a broadcast reaches it wherever it is.
        if (WM_SHOW_EXISTING != 0)
            PostMessageW(HWND_BROADCAST, WM_SHOW_EXISTING, IntPtr.Zero, IntPtr.Zero);

        return false;
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

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string message);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}

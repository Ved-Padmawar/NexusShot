using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>
/// Keeps one NexusShot to a session.
///
/// The app lives in the tray, so launching it again is how a user "opens" it. A second process would
/// take no global hotkey - the first already owns them all - and would then report every shortcut as
/// belonging to another app. It asks the first to show itself instead, and exits.
/// </summary>
public static class SingleInstance
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
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var created);

        if (created) return true;

        _mutex.Dispose();
        _mutex = null;

        // Not a message to a known window: the running instance's handle is not knowable from here,
        // and a broadcast reaches it wherever it is.
        if (WM_SHOW_EXISTING != 0)
            PostMessageW(HWND_BROADCAST, WM_SHOW_EXISTING, IntPtr.Zero, IntPtr.Zero);

        return false;
    }

    public static void Release()
    {
        _mutex?.Dispose();
        _mutex = null;
    }

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}

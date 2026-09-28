using System.Runtime.InteropServices;
using NexusShot.Core;

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

    /// <summary>The main window's title and class, which together are how a second launch finds it.
    /// The title alone also matches Explorer open on the save folder, which is named NexusShot too.</summary>
    public const string MainWindowTitle = "NexusShot";
    public const string MainWindowClass = "NexusShot.Library";

    public const uint WM_COPYDATA = 0x004A;

    /// <summary>Mark our WM_COPYDATA, so another sender's data is never read as a request.</summary>
    private const nint OpenFileTag = 0x4E53_4F46;
    private const nint CaptureTag = 0x4E53_4341;

    /// <summary>Broadcast by a second instance; the running one shows its window.</summary>
    public static readonly uint WM_SHOW_EXISTING = RegisterWindowMessageW("NexusShot.ShowExisting");

    private static Mutex? _mutex;

    /// <summary>
    /// True when this process is the one that gets to run. False means another instance already has
    /// it, and has been handed <paramref name="request"/>'s file or capture, or asked to come to the
    /// front - the caller should exit.
    /// </summary>
    public static bool Claim(LaunchRequest request)
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

        var handed = request switch
        {
            { File: { } file } => Send(OpenFileTag, file),
            { Capture: { } capture } => Send(CaptureTag, capture.ToString()),
            _ => false,
        };
        if (!handed && WM_SHOW_EXISTING != 0)
            PostMessageW(HWND_BROADCAST, WM_SHOW_EXISTING, IntPtr.Zero, IntPtr.Zero);

        return false;
    }

    /// <summary>
    /// Hands a request to the running instance's main window. A launch that races the first one's
    /// startup finds no window yet, so it waits briefly for one rather than dropping the file. The
    /// send times out, so a hung first instance cannot hang Explorer's "Open with" along with it.
    /// </summary>
    private static unsafe bool Send(nint tag, string text)
    {
        var window = IntPtr.Zero;
        for (var attempt = 0; attempt < 30 && window == IntPtr.Zero; attempt++)
        {
            if (attempt > 0) Thread.Sleep(100);
            window = FindWindowW(MainWindowClass, MainWindowTitle);
        }
        if (window == IntPtr.Zero) return false;

        // Pass on our foreground right, or the editor opens behind Explorer.
        GetWindowThreadProcessId(window, out var process);
        AllowSetForegroundWindow(process);

        fixed (char* characters = text)
        {
            var data = new COPYDATASTRUCT { dwData = tag, cbData = (uint)(text.Length * 2), lpData = (IntPtr)characters };
            return SendMessageTimeoutW(window, WM_COPYDATA, IntPtr.Zero, (IntPtr)(&data),
                SMTO_ABORTIFHUNG, 5000, out _) != IntPtr.Zero;
        }
    }

    private const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>The request in a WM_COPYDATA another launch sent, or null if it is not one.</summary>
    public static unsafe LaunchRequest? ReadForwarded(long lParam)
    {
        var data = (COPYDATASTRUCT*)lParam;
        if (data == null || data->lpData == IntPtr.Zero) return null;
        var text = new string((char*)data->lpData, 0, (int)(data->cbData / 2));
        return data->dwData switch
        {
            OpenFileTag => new LaunchRequest(File: text),
            CaptureTag when Enum.TryParse<CaptureCommand>(text, out var capture) => new LaunchRequest(Capture: capture),
            _ => null,
        };
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

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static partial IntPtr SendMessageTimeoutW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowW(string? className, string title);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint process);
}

using System.Runtime.InteropServices;
using NexusShot.Core;

namespace NexusShot.Platform;

/// <summary>How the app meets the end of a Windows session, and the restart after a crash.</summary>
internal static partial class SessionLifetime
{
    public const uint WM_QUERYENDSESSION = 0x0011;
    public const uint WM_ENDSESSION = 0x0016;

    private const uint RESTART_NO_PATCH = 4;
    private const uint RESTART_NO_REBOOT = 8;

    /// <summary>Has Windows relaunch the app - to the tray, as at sign-in - if it crashes or hangs.
    /// Not after an update or reboot: sign-in already starts it when the user asked for that.</summary>
    public static void RegisterCrashRestart() =>
        RegisterApplicationRestart(LaunchRequest.Startup, RESTART_NO_PATCH | RESTART_NO_REBOOT);

    /// <summary>Holds a shutdown or sign-out, with <paramref name="reason"/> shown beside the app on
    /// the screen Windows puts up, where the user can still end the session anyway.</summary>
    public static void BlockEnd(IntPtr window, string reason) => ShutdownBlockReasonCreate(window, reason);

    public static void AllowEnd(IntPtr window) => ShutdownBlockReasonDestroy(window);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterApplicationRestart(string commandLine, uint flags);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShutdownBlockReasonCreate(IntPtr window, string reason);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShutdownBlockReasonDestroy(IntPtr window);
}

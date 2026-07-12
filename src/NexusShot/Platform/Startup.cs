using Microsoft.Win32;

namespace NexusShot.Platform;

/// <summary>
/// The "Start with Windows" toggle: an HKCU Run entry.
///
/// Per-user, so it needs no elevation, and the installer removes the key on uninstall - a stale Run
/// entry pointing at a deleted exe is the sort of thing that outlives the app that wrote it.
/// </summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NexusShot";

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe is null) return;
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A registry write we cannot make is not worth crashing over; the setting simply will
            // not take effect, and the toggle will read back as false next launch.
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

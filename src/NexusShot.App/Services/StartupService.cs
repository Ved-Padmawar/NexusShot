using Microsoft.Win32;

namespace NexusShot.App.Services;
public interface IStartupService { void SetEnabled(bool enabled); }
public sealed class StartupService : IStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NexusShot";
    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } executable) key?.SetValue(ValueName, $"\"{executable}\"");
        else key?.DeleteValue(ValueName, false);
    }
}

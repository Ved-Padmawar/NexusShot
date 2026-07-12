using System.Runtime.InteropServices;
using Microsoft.Win32;
using NexusShot.Core;

namespace NexusShot.Platform;

/// <summary>
/// The OS theme. An unpackaged app's only signal that the user flipped it is WM_SETTINGCHANGE with
/// lParam "ImmersiveColorSet"; the registry is the only place the answer lives. The titlebar is
/// DWM's, not ours, so it has to be told separately.
/// </summary>
public static class SystemTheme
{
    public const uint WM_SETTINGCHANGE = 0x001A;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>True when the OS is using a dark app theme.</summary>
    public static bool IsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Resolves the setting to the theme actually in force.</summary>
    public static Theme Resolve(AppTheme setting) => setting switch
    {
        AppTheme.Light => Theme.Light,
        AppTheme.Dark => Theme.Dark,
        _ => IsDark() ? Theme.Dark : Theme.Light,
    };

    /// <summary>True when the message means the user changed the system theme.</summary>
    public static bool IsColorSetChange(uint message, IntPtr lParam)
    {
        if (message != WM_SETTINGCHANGE || lParam == IntPtr.Zero) return false;
        return Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet";
    }

    /// <summary>Matches the window's titlebar to the theme. XAML did not own it either.</summary>
    public static void ApplyTitleBar(IntPtr window, bool dark)
    {
        if (window == IntPtr.Zero) return;

        var value = dark ? 1 : 0;
        DwmSetWindowAttribute(window, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int size);
}

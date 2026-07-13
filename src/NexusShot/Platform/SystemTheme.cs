using System.Runtime.InteropServices;
using Microsoft.Win32;
using NexusShot.Core;

namespace NexusShot.Platform;

/// <summary>
/// The OS theme. An unpackaged app's only signal that the user flipped it is WM_SETTINGCHANGE with
/// lParam "ImmersiveColorSet"; the registry is the only place the answer lives. The windows paint
/// their own captions, so DWM only needs telling about the frame it still draws around them.
/// </summary>
public static class SystemTheme
{
    public const uint WM_SETTINGCHANGE = 0x001A;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// Whether the OS is in dark mode, cached.
    ///
    /// Resolve() runs at the top of every frame, and a registry read per frame is what made the
    /// theme feel sluggish. The value only changes on WM_SETTINGCHANGE, which invalidates this.
    /// </summary>
    private static bool? _isDark;

    public static bool IsDark() => _isDark ??= ReadIsDark();

    /// <summary>Re-reads the OS theme. Called when WM_SETTINGCHANGE says it moved.</summary>
    public static void Invalidate() => _isDark = null;

    private static bool ReadIsDark()
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
        if (Marshal.PtrToStringUni(lParam) != "ImmersiveColorSet") return false;

        Invalidate();
        return true;
    }

    /// <summary>
    /// Tells DWM the frame is dark, without touching the caption.
    ///
    /// For a window that paints its own caption there is no caption colour to set - only the border
    /// and the drop shadow DWM still draws around us, which follow this flag.
    /// </summary>
    public static void ApplyFrame(IntPtr window, Theme theme)
    {
        if (window == IntPtr.Zero) return;

        var value = theme.IsDark ? 1 : 0;
        DwmSetWindowAttribute(window, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int size);
}

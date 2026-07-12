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
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

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

    /// <summary>Matches the DWM-owned titlebar to the app surface directly beneath it.</summary>
    public static void ApplyTitleBar(IntPtr window, Theme theme, Rgba? surface = null)
    {
        if (window == IntPtr.Zero) return;

        var value = theme.IsDark ? 1 : 0;
        DwmSetWindowAttribute(window, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));

        // Immersive dark mode only asks Windows for its standard dark caption, whose colour does
        // not match our #141417 base. Windows 11 lets an app provide the exact caption and text
        // colours; older Windows versions simply ignore these attributes and keep the fallback.
        value = ColorRef(surface ?? theme.SurfaceBase);
        DwmSetWindowAttribute(window, DWMWA_CAPTION_COLOR, ref value, sizeof(int));

        value = ColorRef(theme.TextPrimary);
        DwmSetWindowAttribute(window, DWMWA_TEXT_COLOR, ref value, sizeof(int));
    }

    /// <summary>DWM expects COLORREF (00BBGGRR), rather than the RGB order used by the renderer.</summary>
    private static int ColorRef(Rgba color) => color.R | color.G << 8 | color.B << 16;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int size);
}

using Microsoft.UI.Xaml;
using Microsoft.Win32;
using NexusShot.App.Enums;
using NexusShot.App.Native;
using WinRT.Interop;

namespace NexusShot.App.Services;

/// <summary>
/// Applies the active theme to every open window and keeps them in sync.
///
/// WinUI 3 has no application-wide theme switch that reaches already-open windows: setting
/// <see cref="Application.RequestedTheme"/> after launch throws, and each window owns an
/// independent XamlRoot. So windows register here and the service walks them, setting
/// <see cref="FrameworkElement.RequestedTheme"/> on each content root. Because every brush in
/// Themes/Tokens.xaml lives in a ThemeDictionary and is consumed via {ThemeResource}, that one
/// assignment re-resolves the whole visual tree.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private readonly List<WeakReference<Window>> _windows = [];
    private AppTheme _preference = AppTheme.System;
    private bool _disposed;

    /// <summary>Raised after the effective theme changes, so views can restyle anything hand-drawn.</summary>
    public event EventHandler<ElementTheme>? ThemeChanged;

    /// <summary>The theme actually in effect, with <see cref="AppTheme.System"/> already resolved.</summary>
    public ElementTheme EffectiveTheme => _preference switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ReadSystemTheme(),
    };

    /// <summary>Sets the user's preference and repaints every registered window.</summary>
    public void SetPreference(AppTheme preference)
    {
        _preference = preference;
        Apply();
    }

    /// <summary>
    /// Registers a window and themes it immediately. Held weakly: a closed window must not be
    /// kept alive by this list, and the editor and preview cards are created and closed freely.
    /// </summary>
    public void Register(Window window)
    {
        Prune();
        _windows.Add(new WeakReference<Window>(window));
        ApplyTo(window, EffectiveTheme);
    }

    /// <summary>
    /// Call when WM_SETTINGCHANGE reports "ImmersiveColorSet". Only matters in System mode; an
    /// explicit Light or Dark preference must not be overridden by the OS flipping.
    /// </summary>
    public void OnSystemThemeChanged()
    {
        if (_preference != AppTheme.System) return;
        Apply();
    }

    private void Apply()
    {
        Prune();
        var theme = EffectiveTheme;
        foreach (var reference in _windows)
            if (reference.TryGetTarget(out var window))
                ApplyTo(window, theme);

        ThemeChanged?.Invoke(this, theme);
    }

    private static void ApplyTo(Window window, ElementTheme theme)
    {
        if (window.Content is FrameworkElement root) root.RequestedTheme = theme;

        // XAML does not own the non-client area, so a dark window would keep a light titlebar.
        var handle = WindowNative.GetWindowHandle(window);
        var useDark = theme == ElementTheme.Dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(
            handle, NativeMethods.DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));

        ApplyCaptionButtonColors(window, theme);
    }

    /// <summary>
    /// Colours the system caption buttons to match the app surface. With content extended into
    /// the titlebar they default to colours meant for the system backdrop, which leaves the
    /// glyphs and hover fills nearly invisible over our own surfaces.
    /// </summary>
    private static void ApplyCaptionButtonColors(Window window, ElementTheme theme)
    {
        var titleBar = window.AppWindow?.TitleBar;
        if (titleBar is null) return;

        var isDark = theme == ElementTheme.Dark;
        var foreground = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF4)
            : Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1E);
        var hoverFill = isDark
            ? Windows.UI.Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x1A, 0x00, 0x00, 0x00);
        var pressedFill = isDark
            ? Windows.UI.Color.FromArgb(0x42, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x29, 0x00, 0x00, 0x00);
        var inactiveForeground = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x6A, 0x6A, 0x72)
            : Windows.UI.Color.FromArgb(0xFF, 0x9A, 0x9A, 0xA2);

        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hoverFill;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressedFill;
    }

    /// <summary>Reads what Explorer itself reads. Absent or unreadable means light, as Windows defaults.</summary>
    private static ElementTheme ReadSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            // The value is inverted: 1 means apps use the *light* theme.
            return key?.GetValue(AppsUseLightThemeValue) is int value && value == 0
                ? ElementTheme.Dark
                : ElementTheme.Light;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return ElementTheme.Light;
        }
    }

    private void Prune() => _windows.RemoveAll(reference => !reference.TryGetTarget(out _));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _windows.Clear();
    }
}

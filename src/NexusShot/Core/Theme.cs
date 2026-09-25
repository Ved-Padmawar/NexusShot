namespace NexusShot.Core;

/// <summary>
/// The design tokens. A theme is a value the renderer reads at the top of the frame, so switching
/// themes or accents is a repaint and cannot half-apply.
///
/// Elevation is lightness, not shadow: in dark the pane, floating and hover surfaces step lighter;
/// in light they step toward white, and the stage is the one surface darker than the window so a
/// capture always reads as sitting on it. Shadows are reserved for things that float over the
/// stage - the image itself, pills and popovers.
/// </summary>
public sealed record Theme
{
    public required bool IsDark { get; init; }

    /// <summary>The window's own surface.</summary>
    public required Rgba SurfaceWindow { get; init; }
    /// <summary>Panes and inset fields: the library header, settings groups, text boxes.</summary>
    public required Rgba SurfacePane { get; init; }
    /// <summary>Anything that floats: pills, popovers, menus, the settings sheet's controls.</summary>
    public required Rgba SurfaceRaised { get; init; }
    /// <summary>Opaque hover and pressed fills, shared by every control on the chrome. Opaque, so a
    /// hovered control never lets the canvas show through it; a clear step from every surface they sit
    /// on, so a hover is seen without looking for it.</summary>
    public required Rgba SurfaceHover { get; init; }
    public required Rgba SurfacePressed { get; init; }
    /// <summary>The canvas the capture sits on, and the dot grid printed across it.</summary>
    public required Rgba SurfaceStage { get; init; }
    public required Rgba StageDot { get; init; }

    // Hairlines: low-alpha white on dark, low-alpha black on light - mirrored, not lightened.
    public required Rgba StrokeSubtle { get; init; }
    public required Rgba StrokeDefault { get; init; }
    public required Rgba StrokeStrong { get; init; }

    public required Rgba TextPrimary { get; init; }
    public required Rgba TextSecondary { get; init; }
    public required Rgba TextTertiary { get; init; }
    public required Rgba TextQuaternary { get; init; }

    public required Rgba Accent { get; init; }
    public required Rgba AccentHover { get; init; }
    public required Rgba AccentPressed { get; init; }
    /// <summary>Text and glyphs on an accent fill. Every preset accent is light, so this is a
    /// near-black tinted toward the accent rather than white.</summary>
    public required Rgba TextOnAccent { get; init; }
    /// <summary>A wash of the accent: selected tabs, the recording hotkey field.</summary>
    public required Rgba AccentSoft { get; init; }
    /// <summary>The accent as text or a stroke on the window. Darkened in light, where the raw
    /// preset accents are too pale to read.</summary>
    public required Rgba AccentText { get; init; }
    public required Rgba AccentLine { get; init; }

    public required Rgba Danger { get; init; }

    /// <summary>What dims the window behind a modal sheet.</summary>
    public required Rgba Scrim { get; init; }
    /// <summary>The colour of drop shadows. Stronger in dark: a shadow has to read against a surface
    /// that is already nearly black.</summary>
    public required Rgba Shadow { get; init; }

    /// <summary>Scrim behind a card's hover actions. Dark in both themes: it sits over captured
    /// pixels, which have no theme.</summary>
    public static readonly Rgba ImageScrim = new(0, 0, 0, 0xB3);

    /// <summary>The system's close-button red, which users rely on across every app.</summary>
    public static readonly Rgba CaptionClose = new(0xC4, 0x2B, 0x1C);

    /// <summary>Builds the theme for a mode and an accent. The accent's derived tones depend on the
    /// mode - a soft wash is fainter on dark, accent text is darkened on light - so they are
    /// computed here rather than stored per preset.</summary>
    public static Theme Resolve(bool dark, Core.Accent accent)
    {
        var baseTheme = dark ? DarkBase : LightBase;
        var color = accent.Color;
        return baseTheme with
        {
            Accent = color,
            AccentHover = color.Mix(Rgba.White, 0.12),
            AccentPressed = color.Mix(Rgba.Black, 0.15),
            TextOnAccent = accent.Ink,
            AccentSoft = color.WithAlpha(dark ? (byte)36 : (byte)56),
            AccentText = dark ? color : color.Mix(Rgba.Black, 0.45),
            AccentLine = dark ? color.WithAlpha(115) : color.Mix(Rgba.Black, 0.3),
        };
    }

    public static Theme Dark => Resolve(dark: true, Core.Accent.Default);

    private static readonly Theme DarkBase = new()
    {
        IsDark = true,

        SurfaceWindow = Hex("#101113"),
        SurfacePane = Hex("#141518"),
        SurfaceRaised = Hex("#1C1D21"),
        SurfaceHover = Hex("#303137"),
        SurfacePressed = Hex("#3A3B42"),
        SurfaceStage = Hex("#0A0A0C"),
        StageDot = Hex("#FFFFFF", 14),

        StrokeSubtle = Hex("#FFFFFF", 17),
        StrokeDefault = Hex("#FFFFFF", 28),
        StrokeStrong = Hex("#FFFFFF", 46),

        TextPrimary = Hex("#EDEDEF"),
        TextSecondary = Hex("#A3A3AB"),
        TextTertiary = Hex("#6C6C75"),
        TextQuaternary = Hex("#4A4A52"),

        Accent = default, AccentHover = default, AccentPressed = default, TextOnAccent = default,
        AccentSoft = default, AccentText = default, AccentLine = default,

        Danger = Hex("#FF5A4E"),
        Scrim = Hex("#040406", 158),
        Shadow = Hex("#000000", 140),
    };

    private static readonly Theme LightBase = new()
    {
        IsDark = false,

        SurfaceWindow = Hex("#FBFBFA"),
        SurfacePane = Hex("#F3F3F1"),
        SurfaceRaised = Hex("#FFFFFF"),
        SurfaceHover = Hex("#E0E0DC"),
        SurfacePressed = Hex("#D2D2CD"),
        SurfaceStage = Hex("#E9E9E5"),
        StageDot = Hex("#000000", 19),

        StrokeSubtle = Hex("#000000", 18),
        StrokeDefault = Hex("#000000", 28),
        StrokeStrong = Hex("#000000", 51),

        TextPrimary = Hex("#141416"),
        TextSecondary = Hex("#5E5E66"),
        TextTertiary = Hex("#8D8D95"),
        TextQuaternary = Hex("#B5B5BB"),

        Accent = default, AccentHover = default, AccentPressed = default, TextOnAccent = default,
        AccentSoft = default, AccentText = default, AccentLine = default,

        Danger = Hex("#E0362B"),
        Scrim = Hex("#E6E6E4", 153),
        Shadow = Hex("#141428", 46),
    };

    private static Rgba Hex(string hex, byte alpha = 255) => Palette.Parse(hex).WithAlpha(alpha);
}

/// <summary>
/// An accent preset: the colour, and the ink drawn on top of it. Every preset is light enough that
/// white text on it fails contrast, so each carries its own near-black ink.
/// </summary>
public sealed record Accent(string Name, Rgba Color, Rgba Ink)
{
    public static readonly Accent[] Presets =
    [
        new("Lime", Palette.Parse("#C8F135"), Palette.Parse("#11140A")),
        new("Iris", Palette.Parse("#8B7CFF"), Palette.Parse("#0D0A1F")),
        new("Coral", Palette.Parse("#FF6A3D"), Palette.Parse("#1F0A03")),
        new("Cyan", Palette.Parse("#3DD6F5"), Palette.Parse("#03161A")),
        new("Mono", Palette.Parse("#F2F2F2"), Palette.Parse("#111111")),
    ];

    public static Accent Default => Presets[0];

    /// <summary>The preset a persisted name refers to, or the default for one that no longer
    /// exists - a settings file from a future build must not leave the app without an accent.</summary>
    public static Accent Named(string? name) =>
        Array.Find(Presets, preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Default;
}

/// <summary>Theme-invariant tokens. Geometry, type and motion do not change with the palette.</summary>
public static class Metrics
{
    public const float RadiusXs = 4;
    public const float RadiusSm = 6;
    public const float RadiusMd = 8;
    public const float RadiusLg = 12;
    public const float RadiusXl = 16;

    public const float FontXs = 11;
    public const float FontSm = 12;
    public const float FontMd = 13;
    public const float FontLg = 15;
    public const float FontXl = 20;
    public const float Font2Xl = 28;

    public const double ControlHeight = 32;
    public const double ControlHeightSmall = 28;

    /// <summary>State changes (hover, press), movement (a thumb sliding), and layout (a bar
    /// resizing, a sheet opening). One ease-out curve for all three.</summary>
    public const double MotionFast = 110;
    public const double Motion = 180;
    public const double MotionSlow = 280;

    /// <summary>Segoe UI Variable's two optical sizes. Windows 10 lacks both; the renderer falls
    /// back to <see cref="FontFallback"/> when a family is missing.</summary>
    public const string FontFamily = "Segoe UI Variable Text";
    public const string DisplayFamily = "Segoe UI Variable Display";
    public const string FontFallback = "Segoe UI";

    /// <summary>For readouts that are really numbers and for keycaps - a hex colour jitters as it
    /// changes if its digits are proportional.</summary>
    public const string MonoFamily = "Cascadia Mono";
    public const string MonoFallback = "Consolas";
}

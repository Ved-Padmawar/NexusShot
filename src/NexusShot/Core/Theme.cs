namespace NexusShot.Core;

/// <summary>
/// The design tokens, ported from the XAML build's ThemeDictionaries.
///
/// In XAML these had to be brushes resolved through ThemeResource, because StaticResource snapshots
/// a brush at load time and a control resolved under one theme keeps it forever. Here a theme is
/// just a value: the renderer reads whichever one is current at the top of the frame, so switching
/// themes is a repaint and cannot half-apply.
///
/// The two themes are not inverses. Dark uses lightness for elevation (a raised surface is lighter
/// than its base). Light inverts that only for the sunken canvas well, because a screenshot must
/// read as inset from the chrome in both.
/// </summary>
public sealed record Theme
{
    // Surfaces. Elevation is lightness, not shadow.
    public required Rgba SurfaceSunken { get; init; }
    public required Rgba SurfaceBase { get; init; }
    public required Rgba SurfaceRaised { get; init; }
    public required Rgba SurfaceOverlay { get; init; }

    // Hairlines: low-alpha white on dark, low-alpha black on light - mirrored, not lightened.
    public required Rgba StrokeSubtle { get; init; }
    public required Rgba StrokeDefault { get; init; }
    public required Rgba StrokeStrong { get; init; }

    // Interaction fills.
    public required Rgba FillHover { get; init; }
    public required Rgba FillPressed { get; init; }
    public required Rgba FillSelected { get; init; }

    public required Rgba TextPrimary { get; init; }
    public required Rgba TextSecondary { get; init; }
    public required Rgba TextTertiary { get; init; }
    public required Rgba TextOnAccent { get; init; }

    public required Rgba Accent { get; init; }
    public required Rgba AccentHover { get; init; }
    public required Rgba AccentPressed { get; init; }
    public required Rgba Danger { get; init; }

    /// <summary>Scrim behind a floating card's hover actions. Dark in both themes: it sits over
    /// captured pixels, which have no theme.</summary>
    public required Rgba HoverScrim { get; init; }

    // Row scrims. Deliberately not the chrome fills: those are ~7% white, which shifts a bright
    // screenshot by 1/255 - invisible over captured pixels.
    public required Rgba RowHoverFill { get; init; }
    public required Rgba RowPressedFill { get; init; }
    public required Rgba RowSelectFill { get; init; }
    public required Rgba RowSelectStroke { get; init; }

    public required bool IsDark { get; init; }

    public static readonly Theme Dark = new()
    {
        IsDark = true,

        SurfaceSunken = Hex("#0E0E10"),
        SurfaceBase = Hex("#141417"),
        SurfaceRaised = Hex("#17171A"),
        SurfaceOverlay = Hex("#1E1E22"),

        StrokeSubtle = Hex("#FFFFFF", 0x14),
        StrokeDefault = Hex("#FFFFFF", 0x1F),
        StrokeStrong = Hex("#FFFFFF", 0x33),

        FillHover = Hex("#FFFFFF", 0x12),
        FillPressed = Hex("#FFFFFF", 0x0A),
        FillSelected = Hex("#FFFFFF", 0x1F),

        TextPrimary = Hex("#F2F2F4"),
        TextSecondary = Hex("#9A9AA2"),
        TextTertiary = Hex("#6A6A72"),
        TextOnAccent = Hex("#FFFFFF"),

        Accent = Hex("#0A84FF"),
        AccentHover = Hex("#3D9BFF"),
        AccentPressed = Hex("#0069D9"),
        Danger = Hex("#FF453A"),

        HoverScrim = Hex("#000000", 0xB3),

        // Selection is an elevated neutral pill, not a blue tint: an opaque raised surface a step
        // lighter than the sidebar, reading as lifted rather than coloured.
        RowHoverFill = Hex("#FFFFFF", 0x0F),
        RowPressedFill = Hex("#FFFFFF", 0x08),
        RowSelectFill = Hex("#1E1E22"),
        RowSelectStroke = Hex("#FFFFFF", 0x14),
    };

    public static readonly Theme Light = new()
    {
        IsDark = false,

        // The canvas well stays the darkest surface so the capture reads as inset.
        SurfaceSunken = Hex("#E4E4E7"),
        SurfaceBase = Hex("#F7F7F9"),
        SurfaceRaised = Hex("#FFFFFF"),
        SurfaceOverlay = Hex("#FFFFFF"),

        StrokeSubtle = Hex("#000000", 0x0F),
        StrokeDefault = Hex("#000000", 0x1A),
        StrokeStrong = Hex("#000000", 0x33),

        FillHover = Hex("#000000", 0x0D),
        FillPressed = Hex("#000000", 0x1A),
        FillSelected = Hex("#000000", 0x14),

        TextPrimary = Hex("#1C1C1E"),
        TextSecondary = Hex("#4A4A52"),
        // #8E8E96 measured only 2.56:1 on SurfaceSunken, under the 3:1 floor for UI text.
        TextTertiary = Hex("#5E5E66"),
        TextOnAccent = Hex("#FFFFFF"),

        // Darker accent: #0A84FF on white fails contrast for small text.
        Accent = Hex("#0069D9"),
        AccentHover = Hex("#0A84FF"),
        AccentPressed = Hex("#0055B0"),
        Danger = Hex("#D70015"),

        HoverScrim = Hex("#000000", 0xB3),

        // Row scrims *lighten* here, where dark's darken. A scrim tints the row's text as well as
        // its thumbnail; light-theme text is dark, so a darkening scrim moves the surface toward the
        // text and collapses contrast (the subtitle fell to 1.5:1, and no text colour fixes it).
        // Lightening pushes the surface away from the text, so contrast rises on hover.
        RowHoverFill = Hex("#000000", 0x0E),
        RowPressedFill = Hex("#000000", 0x17),
        RowSelectFill = Hex("#FFFFFF"),
        RowSelectStroke = Hex("#000000", 0x24),
    };

    private static Rgba Hex(string hex, byte alpha = 255) => Palette.Parse(hex).WithAlpha(alpha);
}

/// <summary>Theme-invariant tokens. Geometry and type do not change with the palette.</summary>
public static class Metrics
{
    public const float RadiusControl = 6;
    public const float RadiusContainer = 10;

    public const float FontCaption = 11;
    public const float FontBody = 13;
    public const float FontSubtitle = 15;
    public const float FontTitle = 20;

    public const string FontFamily = "Segoe UI";
}

using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>
/// One icon: SVG path data on a square design grid, stroked with round caps and joins, and an
/// optional filled part. Authored in SVG so the prototype and the app draw the same marks; parsed
/// once and turned into geometry by <see cref="D2DResources.IconGeometry"/>.
/// </summary>
public sealed class Icon(string stroke, string? fill = null, double grid = 24, double strokeWidth = 1.7,
    double fillOpacity = 1)
{
    public double Grid => grid;
    public double StrokeWidth => strokeWidth;
    public double FillOpacity => fillOpacity;

    public IReadOnlyList<IconFigure> Stroke => _stroke ??= IconPath.Parse(stroke);
    public IReadOnlyList<IconFigure>? Fill => fill is null ? null : _fill ??= IconPath.Parse(fill);

    private IReadOnlyList<IconFigure>? _stroke;
    private IReadOnlyList<IconFigure>? _fill;
}

/// <summary>The app's icon set: 1.7-unit strokes on a 24-unit grid, one visual weight everywhere.</summary>
public static class Icons
{
    // Tools, in toolbar order.
    public static readonly Icon Select = new("M6 3.5l12.5 7-5.6 1.6-2.6 5.4z");
    public static readonly Icon Rectangle = new(Rect(4, 5.5, 16, 13, 2));
    public static readonly Icon Ellipse = new("M3.5 12a8.5 6.5 0 1 0 17 0a8.5 6.5 0 1 0-17 0z");
    public static readonly Icon Arrow = new("M5 19L18 6M9.5 5.5H18.5V14.5");
    public static readonly Icon Line = new("M5 19L19 5");
    public static readonly Icon Pen = new(
        "M4.5 19.5l1-4.2L16 4.8a2 2 0 0 1 2.9 0l.3.3a2 2 0 0 1 0 2.9L8.7 18.5zM14 7l3 3");
    public static readonly Icon Brush = new(
        "M20 4c-3.5 1.5-7.5 5-9.2 7.6l1.6 1.6C15 11.5 18.5 7.5 20 4zM10 12.8c-2.3.2-3.5 1.7-3.8 3.6-.2 1.4-1 2.4-2.2 2.9 3 .9 6.9.1 7.5-3.4z");
    public static readonly Icon Eraser = new(
        "M9 20h11M4.7 14.3l9.6-9.6a2 2 0 0 1 2.8 0l2.2 2.2a2 2 0 0 1 0 2.8L11 18l-2 2H8.4a2 2 0 0 1-1.4-.6l-2.3-2.3a2 2 0 0 1 0-2.8zM9 10l5 5");
    public static readonly Icon Text = new("M5 7V5h14v2M12 5v14M9 19h6");
    public static readonly Icon Counter = new(Circle(12, 12, 8.5) + "M10.5 9.2L12.5 8v8");
    public static readonly Icon Highlight = new("M5 20h6M13.5 4.5l5 5-7 7H6.5v-5zM9 14l-2 2");
    public static readonly Icon Blur = new(
        "M12 3.5c3.2 3.9 6 6.9 6 10.6a6 6 0 0 1-12 0c0-3.7 2.8-6.7 6-10.6zM9 14.5a3 3 0 0 0 3 3");
    public static readonly Icon Pixelate = new("M4 4h5v5H4zM15 4h5v5h-5zM9.5 9.5h5v5h-5zM4 15h5v5H4zM15 15h5v5h-5z");
    public static readonly Icon Spotlight = new(Circle(12, 12, 4)
        + "M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6l1.4 1.4M17 17l1.4 1.4M5.6 18.4L7 17M17 7l1.4-1.4");
    public static readonly Icon Crop = new("M7 3v12a2 2 0 0 0 2 2h12M3 7h12a2 2 0 0 1 2 2v12");

    // Actions.
    public static readonly Icon Undo = new("M9 14L4 9l5-5M4 9h10.5a5.5 5.5 0 0 1 0 11H11");
    public static readonly Icon Redo = new("M15 14l5-5-5-5M20 9H9.5a5.5 5.5 0 0 0 0 11H13");
    public static readonly Icon Copy = new(Rect(8.5, 8.5, 11.5, 11.5, 2.2)
        + "M15.5 8.5V6.2A2.2 2.2 0 0 0 13.3 4H6.2A2.2 2.2 0 0 0 4 6.2v7.1a2.2 2.2 0 0 0 2.2 2.2h2.3");
    /// <summary>A disk: what a save has always looked like, and what the card's Save as showed.</summary>
    public static readonly Icon Save = new(
        "M6 4h10.5L20 7.5V18a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zM8 4v4.5h7V4M8 20v-6.5h8V20");
    public static readonly Icon Folder = new(
        "M3.5 7.5a2 2 0 0 1 2-2h3.8l2 2h7.2a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z");
    public static readonly Icon Share = new(
        "M12 15V3.5M7.5 8L12 3.5 16.5 8M5 13v5.5A1.5 1.5 0 0 0 6.5 20h11a1.5 1.5 0 0 0 1.5-1.5V13");
    public static readonly Icon Ocr = new(
        "M4 8V5.5A1.5 1.5 0 0 1 5.5 4H8M16 4h2.5A1.5 1.5 0 0 1 20 5.5V8M20 16v2.5a1.5 1.5 0 0 1-1.5 1.5H16M8 20H5.5A1.5 1.5 0 0 1 4 18.5V16M8 9h8M8 12h8M8 15h5");
    public static readonly Icon Delete = new(
        "M4.5 7h15M10 11v6M14 11v6M6.5 7l.9 11.2A2 2 0 0 0 9.4 20h5.2a2 2 0 0 0 2-1.8L17.5 7M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2");
    public static readonly Icon Edit = new("M4.5 19.5l1-4.2L16 4.8a2 2 0 0 1 2.9 0l.3.3a2 2 0 0 1 0 2.9L8.7 18.5z");
    public static readonly Icon Pin = new("M9 4h6M10 4v5.5L7 13h10l-3-3.5V4M12 13v7");
    public static readonly Icon Drag = new("M9 5h.01M15 5h.01M9 12h.01M15 12h.01M9 19h.01M15 19h.01", strokeWidth: 3);
    public static readonly Icon Dropper = new(
        "M14 6l4 4M16.2 3.8a2.3 2.3 0 0 1 3.3 3.3L17.3 9.3l-3.3-3.3zM15 7L5.5 16.5 5 19.5l3-.5L17.5 9");

    // Capture.
    public static readonly Icon CaptureRegion = new("M4 8.5V4h4.5M15.5 4H20v4.5M20 15.5V20h-4.5M8.5 20H4v-4.5M12 9v6M9 12h6");
    public static readonly Icon CaptureWindow = new(Rect(3, 4.5, 18, 15, 2.2) + "M3 9h18M6 6.8h.01M8.5 6.8h.01");
    public static readonly Icon CaptureScreen = new(Rect(3, 4, 18, 12.5, 2) + "M8.5 20.5h7M12 16.5v4");
    public static readonly Icon Timer = new(Circle(12, 13.5, 7.5) + "M12 9.5v4l2.5 2M9.5 2.5h5");

    // Chrome.
    public static readonly Icon Settings = new("M4 7h9M17 7h3M4 17h3M11 17h9" + Circle(15, 7, 2) + Circle(9, 17, 2));
    public static readonly Icon History = new("M4 12a8 8 0 1 0 2.4-5.7L4 8.6M4 4v4.6h4.6M12 8v4.5l3 1.8");
    public static readonly Icon Search = new(Circle(11, 11, 6.5) + "M20 20l-4.3-4.3");
    public static readonly Icon Plus = new("M12 5v14M5 12h14");
    public static readonly Icon Minus = new("M5 12h14");
    public static readonly Icon Fit = new("M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5");
    public static readonly Icon Download = new("M12 4v11M7 10l5 5 5-5M5 20h14");
    public static readonly Icon Restart = new("M20 12a8 8 0 1 1-2.3-5.7M20 4v5h-5");
    /// <summary>Two chasing arrows: check again, as distinct from Restart's single loop.</summary>
    public static readonly Icon Refresh = new("M4 11a8 8 0 0 1 14.3-4.3M20 4v5h-5M20 13a8 8 0 0 1-14.3 4.3M4 20v-5h5");
    public static readonly Icon Warning = new(
        "M12 9v4M12 16.5v.5M10.3 3.9L2.5 18a2 2 0 0 0 1.7 3h15.6a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z");
    public static readonly Icon Tick = new("M5 12.5l4.5 4.5L19 7.5");
    public static readonly Icon SelectAll = new(Rect(4, 4, 16, 16, 3.5) + "M8.5 12.2l2.4 2.4 4.6-5");
    public static readonly Icon Close = new("M6 6l12 12M18 6L6 18");
    public static readonly Icon ChevronDown = new("M7 10l5 5 5-5");

    // Style bar.
    public static readonly Icon Bold = new("M7 5h6a3.5 3.5 0 0 1 0 7H7zM7 12h7a3.5 3.5 0 0 1 0 7H7z");
    public static readonly Icon Italic = new("M10 5h8M6 19h8M14 5l-4 14");
    public static readonly Icon Underline = new("M7 4v7a5 5 0 0 0 10 0V4M5 20h14");
    public static readonly Icon FillTinted = new(Rect(4, 5.5, 16, 13, 2), Rect(4, 5.5, 16, 13, 2), fillOpacity: 0.35);
    public static readonly Icon FillSolid = new(Rect(4, 5.5, 16, 13, 2), Rect(4, 5.5, 16, 13, 2));

    // Settings pages.
    public static readonly Icon General = new(Circle(12, 12, 3)
        + "M12 3v2.5M12 18.5V21M3 12h2.5M18.5 12H21M5.6 5.6l1.8 1.8M16.6 16.6l1.8 1.8M5.6 18.4l1.8-1.8M16.6 7.4l1.8-1.8");
    public static readonly Icon Camera = new(
        "M4 8.5A2 2 0 0 1 6 6.5h1.6L9 4.5h6l1.4 2H18a2 2 0 0 1 2 2V17a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2z" + Circle(12, 12.5, 3.5));
    public static readonly Icon Cards = new(Rect(3, 7, 13, 10, 1.8) + "M7 4h11.5A2.5 2.5 0 0 1 21 6.5V14");
    public static readonly Icon Keyboard = new(Rect(2.5, 6, 19, 12, 2) + "M6 10h.01M9.5 10h.01M13 10h.01M16.5 10h.01M7.5 14h9");
    public static readonly Icon Output = new("M14 3.5H7a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-10zM14 3.5v5h5");
    public static readonly Icon Palette = new(
        "M12 3.5a8.5 8.5 0 0 0 0 17c1.2 0 1.8-.8 1.8-1.7 0-1.3-1.2-1.5-1.2-2.6 0-1 .8-1.7 1.8-1.7h2.2a3.9 3.9 0 0 0 3.9-3.9c0-3.9-3.8-7.1-8.5-7.1z"
        + "M7.8 11h.01M10.5 7.6h.01M15 8h.01", strokeWidth: 1.7);

    // Caption buttons, on the 10-unit grid Windows draws its own at.
    public static readonly Icon CaptionMinimise = new("M0 5.5h10", grid: 10, strokeWidth: 1);
    public static readonly Icon CaptionMaximise = new(Rect(0.5, 0.5, 9, 9, 1), grid: 10, strokeWidth: 1);
    public static readonly Icon CaptionRestore = new(Rect(0.5, 2.5, 7, 7, 1) + "M2.5 2.5V1.5a1 1 0 0 1 1-1h5a1 1 0 0 1 1 1v5a1 1 0 0 1-1 1h-1",
        grid: 10, strokeWidth: 1);
    public static readonly Icon CaptionClose = new("M.5.5l9 9M9.5.5l-9 9", grid: 10, strokeWidth: 1);

    /// <summary>A rounded rectangle as path data - SVG's &lt;rect&gt;, which the parser does not read.</summary>
    private static string Rect(double x, double y, double width, double height, double r) =>
        FormattableString.Invariant(
            $"M{x + r} {y}h{width - 2 * r}a{r} {r} 0 0 1 {r} {r}v{height - 2 * r}a{r} {r} 0 0 1 {-r} {r}h{-(width - 2 * r)}a{r} {r} 0 0 1 {-r} {-r}v{-(height - 2 * r)}a{r} {r} 0 0 1 {r} {-r}z");

    /// <summary>A circle as two half-arcs - SVG's &lt;circle&gt;.</summary>
    private static string Circle(double cx, double cy, double r) =>
        FormattableString.Invariant($"M{cx - r} {cy}a{r} {r} 0 1 0 {2 * r} 0a{r} {r} 0 1 0 {-2 * r} 0z");
}

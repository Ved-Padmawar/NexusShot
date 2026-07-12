using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>
/// Tool icons, drawn as vectors.
///
/// No icon font, no PNG assets, no .pri to forget to publish. Each icon is a few primitives inside
/// a normalised box, so it is crisp at any DPI and picks up the theme's tint for free.
/// </summary>
public static class ToolIcons
{
    public static Action<Ui, Rect, Rgba> For(EditorTool tool) => tool switch
    {
        EditorTool.Select => Select,
        EditorTool.Rectangle => Rectangle,
        EditorTool.Ellipse => Ellipse,
        EditorTool.Line => Line,
        EditorTool.Arrow => Arrow,
        EditorTool.Pen => Pen,
        EditorTool.Brush => Brush,
        EditorTool.Eraser => Eraser,
        EditorTool.Text => TextTool,
        EditorTool.Highlight => Highlight,
        EditorTool.Blur => Blur,
        EditorTool.Pixelate => Pixelate,
        EditorTool.Counter => Counter,
        EditorTool.Spotlight => Spotlight,
        EditorTool.Crop => Crop,
        _ => (_, _, _) => { },
    };

    /// <summary>The icon's drawing box: a centred square inset from the tile.</summary>
    private static Rect Box(Rect tile, double inset = 7)
    {
        var size = Math.Min(tile.Width, tile.Height) - inset * 2;
        return new Rect(
            tile.Center.X - size / 2,
            tile.Center.Y - size / 2,
            size,
            size);
    }

    private static void Select(Ui ui, Rect tile, Rgba tint)
    {
        // A cursor arrow.
        var b = Box(tile, 6);
        var tip = new Point(b.X + b.Width * 0.22, b.Y + b.Height * 0.08);
        ui.Line(tip, new Point(b.X + b.Width * 0.22, b.Y + b.Height * 0.82), tint, 1.6f);
        ui.Line(tip, new Point(b.X + b.Width * 0.80, b.Y + b.Height * 0.56), tint, 1.6f);
        ui.Line(
            new Point(b.X + b.Width * 0.22, b.Y + b.Height * 0.82),
            new Point(b.X + b.Width * 0.42, b.Y + b.Height * 0.60), tint, 1.6f);
        ui.Line(
            new Point(b.X + b.Width * 0.42, b.Y + b.Height * 0.60),
            new Point(b.X + b.Width * 0.80, b.Y + b.Height * 0.56), tint, 1.6f);
        ui.Line(
            new Point(b.X + b.Width * 0.44, b.Y + b.Height * 0.62),
            new Point(b.X + b.Width * 0.62, b.Y + b.Height * 0.94), tint, 1.6f);
    }

    private static void Rectangle(Ui ui, Rect tile, Rgba tint) =>
        ui.StrokeRounded(Box(tile), 1.5f, tint, 1.6f);

    private static void Ellipse(Ui ui, Rect tile, Rgba tint)
    {
        var b = Box(tile);
        ui.StrokeCircle(b.Center, (float)(b.Width / 2), tint, 1.6f);
    }

    private static void Line(Ui ui, Rect tile, Rgba tint)
    {
        var b = Box(tile);
        ui.Line(new Point(b.Left, b.Bottom), new Point(b.Right, b.Top), tint, 1.8f);
    }

    private static void Arrow(Ui ui, Rect tile, Rgba tint)
    {
        var b = Box(tile);
        var from = new Point(b.Left, b.Bottom);
        var to = new Point(b.Right, b.Top);
        ui.Line(from, to, tint, 1.8f);
        // Head: two short barbs back along the shaft.
        ui.Line(to, new Point(to.X - b.Width * 0.36, to.Y + b.Height * 0.06), tint, 1.8f);
        ui.Line(to, new Point(to.X - b.Width * 0.06, to.Y + b.Height * 0.36), tint, 1.8f);
    }

    private static void Pen(Ui ui, Rect tile, Rgba tint)
    {
        var b = Box(tile);
        // A nib: a diagonal body with a point at the lower left.
        ui.Line(new Point(b.Left + b.Width * 0.12, b.Bottom - b.Height * 0.08),
                new Point(b.Left + b.Width * 0.34, b.Bottom - b.Height * 0.30), tint, 1.6f);
        ui.Line(new Point(b.Left + b.Width * 0.30, b.Bottom - b.Height * 0.26),
                new Point(b.Right - b.Width * 0.10, b.Top + b.Height * 0.10), tint, 2.4f);
        ui.Line(new Point(b.Left + b.Width * 0.12, b.Bottom - b.Height * 0.08),
                new Point(b.Left + b.Width * 0.22, b.Bottom - b.Height * 0.34), tint, 1.6f);
    }

    private static void Brush(Ui ui, Rect tile, Rgba tint)
    {
        var b = Box(tile);
        // A thick round-capped daub: the tool paints width, so the icon shows width.
        ui.Line(new Point(b.Left + b.Width * 0.18, b.Bottom - b.Height * 0.18),
                new Point(b.Right - b.Width * 0.18, b.Top + b.Height * 0.18), tint, 4.5f);
    }

    private static void Eraser(Ui ui, Rect tile, Rgba tint)
    {
        var b = Box(tile);
        // A tilted block with a baseline under it.
        var body = new Rect(b.X + b.Width * 0.10, b.Y + b.Height * 0.20, b.Width * 0.62, b.Height * 0.46);
        ui.StrokeRounded(body, 2f, tint, 1.6f);
        ui.Line(new Point(b.Left, b.Bottom - b.Height * 0.08),
                new Point(b.Right, b.Bottom - b.Height * 0.08), tint, 1.6f);
    }

    private static void TextTool(Ui ui, Rect tile, Rgba tint)
    {
        var b = Box(tile);
        // A serif "T".
        ui.Line(new Point(b.Left + b.Width * 0.08, b.Top + b.Height * 0.12),
                new Point(b.Right - b.Width * 0.08, b.Top + b.Height * 0.12), tint, 1.8f);
        ui.Line(new Point(b.Center.X, b.Top + b.Height * 0.12),
                new Point(b.Center.X, b.Bottom - b.Height * 0.10), tint, 1.8f);
    }

    private static void Highlight(Ui ui, Rect tile, Rgba tint)
    {
        var b = Box(tile);
        // A marker swipe across two text-like rules: the band reads as a highlight only if there is
        // something under it to be highlighted.
        ui.Line(new Point(b.Left, b.Top + b.Height * 0.22),
                new Point(b.Right - b.Width * 0.2, b.Top + b.Height * 0.22), tint.WithAlpha(90), 1.4f);
        ui.Line(new Point(b.Left, b.Bottom - b.Height * 0.16),
                new Point(b.Right - b.Width * 0.35, b.Bottom - b.Height * 0.16), tint.WithAlpha(90), 1.4f);

        var band = new Rect(b.X, b.Center.Y - b.Height * 0.16, b.Width, b.Height * 0.32);
        ui.FillRounded(band, 1.5f, tint.WithAlpha(150));
    }

    private static void Blur(Ui ui, Rect tile, Rgba tint)
    {
        var b = Box(tile);
        // Concentric rings fading out: the visual grammar of a blur.
        ui.StrokeCircle(b.Center, (float)(b.Width * 0.46), tint.WithAlpha(70), 1.4f);
        ui.StrokeCircle(b.Center, (float)(b.Width * 0.30), tint.WithAlpha(140), 1.4f);
        ui.FillCircle(b.Center, (float)(b.Width * 0.14), tint);
    }

    private static void Pixelate(Ui ui, Rect tile, Rgba tint)
    {
        var b = Box(tile);
        // A 3x3 checker: some cells solid, some empty.
        var cell = b.Width / 3;
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
        {
            if ((row + column) % 2 != 0) continue;
            ui.FillRect(new Rect(b.X + column * cell + 1, b.Y + row * cell + 1, cell - 2, cell - 2), tint);
        }
    }

    private static void Counter(Ui ui, Rect tile, Rgba tint)
    {
        var b = Box(tile);
        ui.StrokeCircle(b.Center, (float)(b.Width / 2), tint, 1.6f);
        ui.Text("1", b, tint, Metrics.FontCaption, bold: true, align: TextAlign.Center);
    }

    private static void Spotlight(Ui ui, Rect tile, Rgba tint)
    {
        var b = Box(tile);
        // A lit region inside a dimmed field: the outer frame is the dimming, the inner box is what
        // stays visible. Outline rather than fill, so it does not read as a solid blob.
        ui.StrokeRounded(b, 2f, tint.WithAlpha(70), 1.3f);
        var inner = b.Deflate(b.Width * 0.26);
        ui.FillRounded(inner, 1.5f, tint);
    }

    private static void Crop(Ui ui, Rect tile, Rgba tint)
    {
        var b = Box(tile, 6);
        // The two overlapping L's of a crop mark.
        ui.Line(new Point(b.Left + b.Width * 0.26, b.Top),
                new Point(b.Left + b.Width * 0.26, b.Bottom - b.Height * 0.22), tint, 1.6f);
        ui.Line(new Point(b.Left + b.Width * 0.26, b.Bottom - b.Height * 0.22),
                new Point(b.Right, b.Bottom - b.Height * 0.22), tint, 1.6f);
        ui.Line(new Point(b.Left, b.Top + b.Height * 0.22),
                new Point(b.Right - b.Width * 0.26, b.Top + b.Height * 0.22), tint, 1.6f);
        ui.Line(new Point(b.Right - b.Width * 0.26, b.Top + b.Height * 0.22),
                new Point(b.Right - b.Width * 0.26, b.Bottom), tint, 1.6f);
    }

    // ============================  CHROME ICONS  ============================

    public static void Undo(Ui ui, Rect tile, Rgba tint) => Curve(ui, tile, tint, mirrored: false);
    public static void Redo(Ui ui, Rect tile, Rgba tint) => Curve(ui, tile, tint, mirrored: true);

    /// <summary>
    /// The undo/redo glyph: an arrow that curves up and doubles back, with the head at the left tip.
    ///
    /// Written as explicit fractions of the box rather than derived from trigonometry. The arc-plus-
    /// tangent version was harder to reason about than the shape it drew, and two attempts at it
    /// produced a bare arch and then a squiggle. A glyph this small is a handful of coordinates;
    /// stating them is both shorter and correct.
    /// </summary>
    private static void Curve(Ui ui, Rect tile, Rgba tint, bool mirrored)
    {
        var b = Box(tile, 6);

        // Normalised path, left to right: the head's two barbs meet at the tip, then the shaft
        // sweeps right and curls down. Y grows downward.
        var tip = P(0.06, 0.42);

        // The curve: sampled points along the top, from the tip round to the descending tail.
        Point[] shaft =
        [
            tip,
            P(0.24, 0.30),
            P(0.46, 0.22),
            P(0.68, 0.26),
            P(0.84, 0.42),
            P(0.88, 0.64),
            P(0.84, 0.84),
        ];
        for (var i = 1; i < shaft.Length; i++) ui.Line(shaft[i - 1], shaft[i], tint, 1.8f);

        // The head: two barbs back from the tip, opening rightward along the shaft.
        ui.Line(tip, P(0.30, 0.20), tint, 1.8f);
        ui.Line(tip, P(0.26, 0.62), tint, 1.8f);

        // Redo is undo reflected about the tile's vertical centreline.
        Point P(double u, double v)
        {
            var x = b.X + (mirrored ? 1 - u : u) * b.Width;
            return new Point(x, b.Y + v * b.Height);
        }
    }
}

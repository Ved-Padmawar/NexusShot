namespace NexusShot.Core;

/// <summary>
/// The single size contract shared by paint strokes, eraser masks and their cursor preview.
/// Thickness is a diameter in image pixels; the affected footprint extends half of it from the
/// sampled centreline in every direction.
/// </summary>
public static class PaintStrokeGeometry
{
    public static double Diameter(double thickness) => Math.Max(1, thickness);
    public static double Radius(double thickness) => Diameter(thickness) / 2;
}

public sealed class EraserMask
{
    public double Radius { get; init; }
    public List<Point> Points { get; init; } = [];
}

/// <summary>
/// A single annotation in image-pixel coordinates. Annotations are never baked into the
/// bitmap until export; the editor renders the base image plus this list on top.
/// </summary>
public sealed class Annotation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required EditorTool Tool { get; init; }

    /// <summary>Drag origin, in image pixels.</summary>
    public Point Start { get; set; }

    /// <summary>Current drag endpoint, in image pixels.</summary>
    public Point End { get; set; }

    /// <summary>Freehand path points, in image pixels.</summary>
    public List<Point> Points { get; init; } = [];
    public List<EraserMask> Erasures { get; init; } = [];

    /// <summary>True for tools painted as a stroke whose pixels get an effect, not a shape.</summary>
    public bool IsBrushEffect => Tool is EditorTool.Blur or EditorTool.Pixelate;

    /// <summary>Half the painted stroke's width, in image pixels. Scales with the thickness slider.</summary>
    public double BrushRadius => Math.Max(8, StrokeThickness * 3);

    public string Text { get; set; } = string.Empty;

    /// <summary>Text annotation formatting, in image pixels for the size.</summary>
    public double FontSize { get; set; } = 20;
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public bool IsUnderline { get; set; }

    /// <summary>Step number rendered by <see cref="EditorTool.Counter"/>.</summary>
    public int CounterValue { get; set; }

    public string ColorHex { get; set; } = "#FF3B30";
    public double StrokeThickness { get; set; } = 4;

    /// <summary>True when the tool is drawn as a freehand path rather than a two-point shape.</summary>
    public bool IsStrokeTool => Tool is EditorTool.Pen or EditorTool.Brush or EditorTool.Eraser
        or EditorTool.Blur or EditorTool.Pixelate;

    /// <summary>The diameter of a counter badge, shared by hit testing and rendering. The width
    /// slider scales it, but gently: at x8 a mid-range width produced a badge that swamped the
    /// screenshot it was meant to annotate.</summary>
    public double CounterDiameter => Math.Max(20, 14 + StrokeThickness * 2.5);

    /// <summary>Bounding box in image pixels, normalised so width and height are non-negative.</summary>
    public Rect Bounds
    {
        get
        {
            if ((Tool is EditorTool.Pen or EditorTool.Brush or EditorTool.Eraser || IsBrushEffect) && Points.Count > 0)
            {
                var minX = Points.Min(p => p.X);
                var minY = Points.Min(p => p.Y);
                var box = new Rect(minX, minY, Points.Max(p => p.X) - minX, Points.Max(p => p.Y) - minY);
                if (!IsBrushEffect) return box;

                // A brush stroke's footprint extends half a brush beyond its centreline.
                var radius = BrushRadius;
                return new Rect(box.X - radius, box.Y - radius, box.Width + radius * 2, box.Height + radius * 2);
            }

            if (Tool == EditorTool.Counter)
            {
                // The counter is a circle centred on Start; Start==End, so the two-point box
                // would be empty and the badge unselectable except at its exact centre.
                var diameter = CounterDiameter;
                return new Rect(Start.X - diameter / 2, Start.Y - diameter / 2, diameter, diameter);
            }

            var x = Math.Min(Start.X, End.X);
            var y = Math.Min(Start.Y, End.Y);
            return new Rect(x, y, Math.Abs(End.X - Start.X), Math.Abs(End.Y - Start.Y));
        }
    }

    /// <summary>True when the tool is defined by two free endpoints rather than a box.</summary>
    public bool IsLinear => Tool is EditorTool.Line or EditorTool.Arrow;

    /// <summary>Shifts the annotation by a delta in image pixels.</summary>
    public void Translate(double dx, double dy)
    {
        Start = new Point(Start.X + dx, Start.Y + dy);
        End = new Point(End.X + dx, End.Y + dy);
        for (var i = 0; i < Points.Count; i++)
            Points[i] = new Point(Points[i].X + dx, Points[i].Y + dy);
        foreach (var mask in Erasures)
        {
            for (var i = 0; i < mask.Points.Count; i++)
                mask.Points[i] = new Point(mask.Points[i].X + dx, mask.Points[i].Y + dy);
        }
    }

    /// <summary>Hit test in image pixels, with a slack radius so thin strokes stay grabbable.</summary>
    public bool HitTest(Point point, double slack = 6)
    {
        if (Tool is EditorTool.Pen or EditorTool.Brush or EditorTool.Eraser || IsBrushEffect)
        {
            var reach = slack + (IsBrushEffect ? BrushRadius : 0);
            return Points.Any(p => Math.Abs(p.X - point.X) <= reach && Math.Abs(p.Y - point.Y) <= reach);
        }

        if (IsLinear) return DistanceToSegment(point, Start, End) <= slack + StrokeThickness / 2;

        var bounds = Bounds;
        return point.X >= bounds.Left - slack && point.X <= bounds.Right + slack
            && point.Y >= bounds.Top - slack && point.Y <= bounds.Bottom + slack;
    }

    /// <summary>Deep copy, for undo snapshots: undo must restore values, not shared references.</summary>
    public Annotation Clone() => new()
    {
        Id = Id,
        Tool = Tool,
        Start = Start,
        End = End,
        Points = [.. Points],
        Erasures = Erasures.Select(mask => new EraserMask
        {
            Radius = mask.Radius,
            Points = [.. mask.Points],
        }).ToList(),
        Text = Text,
        CounterValue = CounterValue,
        ColorHex = ColorHex,
        StrokeThickness = StrokeThickness,
        FontSize = FontSize,
        IsBold = IsBold,
        IsItalic = IsItalic,
        IsUnderline = IsUnderline,
    };

    public static double DistanceToSegment(Point point, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared == 0) return Math.Sqrt(Square(point.X - a.X) + Square(point.Y - a.Y));
        var t = Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0, 1);
        return Math.Sqrt(Square(point.X - (a.X + t * dx)) + Square(point.Y - (a.Y + t * dy)));
    }

    private static double Square(double value) => value * value;
}

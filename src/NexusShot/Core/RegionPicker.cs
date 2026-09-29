namespace NexusShot.Core;

/// <summary>What the picker takes. Text reads what it takes instead of filing it.</summary>
public enum PickerMode { Region, Freeform, Window, Screen, Text }

/// <summary>What Text mode reads: an area dragged over, or a clicked window.</summary>
public enum TextScope { Area, Window }

/// <summary>What the picker chose, in overlay pixels. <paramref name="Outline"/> is a freeform
/// lasso relative to <paramref name="Region"/>'s top-left; pixels outside it are not part of the
/// capture. <paramref name="Text"/> asks for the text in it rather than the image.</summary>
public sealed record PickResult(Rect Region, IReadOnlyList<Point>? Outline = null, bool Text = false);

/// <summary>
/// The region picker's state, in the overlay's client pixels: the mode, the drag, what is under the
/// pointer, the lasso. The overlay only forwards input here and draws what this reports.
///
/// Region drags a rectangle - Shift squares it, Space moves it whole - or takes a window with a
/// click. Freeform lassoes any shape. Window and Screen take what is clicked. Text reads an area or
/// a window, as its scope says.
/// </summary>
public sealed class RegionPicker(
    Size desktop, IReadOnlyList<Rect> windows, IReadOnlyList<Rect> screens, Rect? lastRegion, PickerMode mode)
{
    private const double MinimumSide = 2;

    private readonly List<Point> _path = [];
    private Point _origin;
    private Point _corner;

    public PickerMode Mode { get; private set; } = mode;
    public TextScope TextScope { get; private set; }
    public Point Pointer { get; private set; }
    public bool IsDragging { get; private set; }

    /// <summary>The lasso so far, in overlay pixels.</summary>
    public IReadOnlyList<Point> Path => _path;

    /// <summary>Whether a press starts a drag; otherwise a click takes <see cref="Target"/>.</summary>
    public bool Drags => Mode is PickerMode.Region or PickerMode.Freeform
        || Mode == PickerMode.Text && TextScope == TextScope.Area;

    /// <summary>What a click here takes: the top-most window, or in Screen mode the monitor, clipped to
    /// the desktop. None while dragging, and none for a lasso, where it would only be noise.</summary>
    public Rect? Target => IsDragging || Mode == PickerMode.Freeform ? null : TargetAt(Pointer);

    /// <summary>The rectangle being dragged, or the lasso's bounds; null before a drag.</summary>
    public Rect? Selection => !IsDragging || !Drags ? null
        : Mode == PickerMode.Freeform ? Bounds(_path)
        : Rect.FromEdges(_origin.X, _origin.Y, _corner.X, _corner.Y);

    public bool HasLastRegion => lastRegion is not null;

    /// <summary>Ignored mid-drag: the gesture belongs to the mode it started in.</summary>
    public void SetMode(PickerMode next)
    {
        if (!IsDragging) Mode = next;
    }

    public void NextMode() => SetMode((PickerMode)(((int)Mode + 1) % Enum.GetValues<PickerMode>().Length));

    public void SetTextScope(TextScope scope)
    {
        if (!IsDragging) TextScope = scope;
    }

    public void Press(Point point)
    {
        Pointer = point;
        IsDragging = true;
        _origin = _corner = point;
        _path.Clear();
        _path.Add(point);
    }

    /// <summary><paramref name="square"/> (Shift) makes the rectangle square on its longer side;
    /// <paramref name="moveWhole"/> (Space) carries the rectangle with the pointer instead of
    /// resizing it.</summary>
    public void Move(Point point, bool square = false, bool moveWhole = false)
    {
        var delta = point - Pointer;
        Pointer = point;
        if (!IsDragging || !Drags) return;

        if (Mode == PickerMode.Freeform)
        {
            if (_path[^1] != point) _path.Add(point);
            return;
        }

        if (moveWhole)
        {
            _origin += delta;
            _corner += delta;
            return;
        }

        _corner = square ? Squared(_origin, point) : point;
    }

    /// <summary>Ends the gesture: the dragged region, or on a click what is under it. Null cancels -
    /// a click on bare desktop, or a lasso too small to hold a pixel.</summary>
    public PickResult? Release(Point point, bool square = false, bool moveWhole = false)
    {
        Move(point, square, moveWhole);
        var selection = Selection;
        IsDragging = false;
        var text = Mode == PickerMode.Text;

        if (Mode == PickerMode.Freeform)
        {
            if (selection is not { } lasso || Whole(lasso) is not { } box) return null;
            return new PickResult(box, [.. _path.Select(p => new Point(p.X - box.X, p.Y - box.Y))]);
        }

        if (selection is { } drag && Whole(drag.Intersect(new Rect(0, 0, desktop.Width, desktop.Height))) is { } region) return new PickResult(region, Text: text);
        return TargetAt(point) is { } target ? new PickResult(target, Text: text) : null;
    }

    /// <summary>The previous capture's region again, if it still fits on the desktop - read as text
    /// in Text mode.</summary>
    public PickResult? RepeatLast() =>
        lastRegion is { } last && Whole(last.Intersect(new Rect(0, 0, desktop.Width, desktop.Height))) is { } region
            ? new PickResult(region, Text: Mode == PickerMode.Text)
            : null;

    private Rect? TargetAt(Point point)
    {
        var screen = new Rect(0, 0, desktop.Width, desktop.Height);
        foreach (var target in Mode == PickerMode.Screen ? screens : windows)
        {
            if (!target.Contains(point)) continue;
            return Whole(target.Intersect(screen));
        }
        return null;
    }

    /// <summary>Whole pixels, or null under two on either side: that is a click, not a region.</summary>
    private static Rect? Whole(Rect rect)
    {
        var left = Math.Round(rect.X);
        var top = Math.Round(rect.Y);
        var box = Rect.FromEdges(left, top, Math.Round(rect.Right), Math.Round(rect.Bottom));
        return box.Width < MinimumSide || box.Height < MinimumSide ? null : box;
    }

    private static Point Squared(Point origin, Point point)
    {
        var side = Math.Max(Math.Abs(point.X - origin.X), Math.Abs(point.Y - origin.Y));
        return new Point(origin.X + Math.CopySign(side, point.X - origin.X), origin.Y + Math.CopySign(side, point.Y - origin.Y));
    }

    private static Rect Bounds(IReadOnlyList<Point> points) => Rect.FromEdges(
        points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
}

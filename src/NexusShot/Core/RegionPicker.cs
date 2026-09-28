namespace NexusShot.Core;

public enum PickerShape { Rectangle, Freeform }

/// <summary>What the picker chose, in overlay pixels. <paramref name="Outline"/> is a freeform
/// lasso relative to <paramref name="Region"/>'s top-left; pixels outside it are not part of the
/// capture.</summary>
public sealed record PickResult(Rect Region, IReadOnlyList<Point>? Outline = null);

/// <summary>
/// The region picker's state, in the overlay's client pixels: the drag, the window under the pointer,
/// the lasso. The overlay only forwards input here and draws what this reports.
///
/// A drag selects a rectangle - Shift squares it, Space moves it whole. A click without a drag takes
/// the window under the pointer. Freeform lassoes any shape.
/// </summary>
public sealed class RegionPicker(Size desktop, IReadOnlyList<Rect> windows, Rect? lastRegion)
{
    private const double MinimumSide = 2;

    private readonly List<Point> _path = [];
    private Point _origin;
    private Point _corner;

    public PickerShape Shape { get; private set; }
    public Point Pointer { get; private set; }
    public bool IsDragging { get; private set; }

    /// <summary>The lasso so far, in overlay pixels.</summary>
    public IReadOnlyList<Point> Path => _path;

    /// <summary>The top-most window under the pointer, clipped to the desktop: what a click takes.
    /// None while dragging or lassoing, where it would only be noise.</summary>
    public Rect? HoveredWindow => IsDragging || Shape == PickerShape.Freeform ? null : WindowAt(Pointer);

    /// <summary>The rectangle being dragged, or the lasso's bounds; null before a drag.</summary>
    public Rect? Selection => !IsDragging ? null
        : Shape == PickerShape.Freeform ? Bounds(_path)
        : Rect.FromEdges(_origin.X, _origin.Y, _corner.X, _corner.Y);

    public bool HasLastRegion => lastRegion is not null;

    public void ToggleShape()
    {
        if (IsDragging) return;
        Shape = Shape == PickerShape.Rectangle ? PickerShape.Freeform : PickerShape.Rectangle;
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
        if (!IsDragging) return;

        if (Shape == PickerShape.Freeform)
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

    /// <summary>Ends the gesture: the dragged region, or on a click the window under it. Null
    /// cancels - a click on bare desktop, or a lasso too small to hold a pixel.</summary>
    public PickResult? Release(Point point, bool square = false, bool moveWhole = false)
    {
        Move(point, square, moveWhole);
        var selection = Selection;
        IsDragging = false;
        if (selection is not { } drag) return null;

        if (Shape == PickerShape.Freeform)
        {
            if (Whole(drag) is not { } box) return null;
            return new PickResult(box, [.. _path.Select(p => new Point(p.X - box.X, p.Y - box.Y))]);
        }

        return Whole(drag) is { } region ? new PickResult(region) : WindowAt(point) is { } window ? new PickResult(window) : null;
    }

    /// <summary>The previous capture's region again, if it still fits on the desktop.</summary>
    public PickResult? RepeatLast() =>
        lastRegion is { } last && Whole(last.Intersect(new Rect(0, 0, desktop.Width, desktop.Height))) is { } region
            ? new PickResult(region)
            : null;

    private Rect? WindowAt(Point point)
    {
        var screen = new Rect(0, 0, desktop.Width, desktop.Height);
        foreach (var window in windows)
        {
            if (!window.Contains(point)) continue;
            return Whole(window.Intersect(screen));
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

namespace NexusShot.Core;

/// <summary>
/// A point in image or client space. Structurally identical to Windows.Foundation.Point.
/// </summary>
public readonly record struct Point(double X, double Y)
{
    public static Point Zero => new(0, 0);

    public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);
    public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);

    public double DistanceTo(Point other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

public readonly record struct Size(double Width, double Height)
{
    public static Size Empty => new(0, 0);
}

/// <summary>
/// Replaces <c>Windows.Foundation.Rect</c>. Width and height are always non-negative; callers
/// that work in edges use <see cref="FromEdges"/>.
/// </summary>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public static Rect Empty => new(0, 0, 0, 0);
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public Point Center => new(X + Width / 2, Y + Height / 2);

    public static Rect FromEdges(double left, double top, double right, double bottom) =>
        new(Math.Min(left, right), Math.Min(top, bottom), Math.Abs(right - left), Math.Abs(bottom - top));

    public bool Contains(Point point) =>
        point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;

    /// <summary>Shrinks the rect on every side, never past its own centre.</summary>
    public Rect Deflate(double amount)
    {
        var x = Math.Min(Math.Max(0, amount), Width / 2);
        var y = Math.Min(Math.Max(0, amount), Height / 2);
        return new Rect(X + x, Y + y, Math.Max(0, Width - x * 2), Math.Max(0, Height - y * 2));
    }

    /// <summary>
    /// Aspect-preserving fit, centred in this rect: the whole of <paramref name="content"/> is
    /// visible, with empty space on one axis.
    ///
    /// <paramref name="enlarge"/> lets a small image grow to fill its container. The preview well
    /// wants that - a 600x350 capture pinned at 1:1 in a 1400px pane is a postage stamp adrift in
    /// empty space. It costs nothing in sharpness there because the GPU upscales the real bitmap,
    /// not a pre-scaled thumbnail. The editor is where 1:1 actually matters, and it opts out.
    ///
    /// Rounded to whole pixels: this places a bitmap, and a half-pixel origin resamples it.
    /// </summary>
    public Rect Fit(Size content, bool enlarge = false)
    {
        if (content.Width <= 0 || content.Height <= 0 || IsEmpty) return this;

        var scale = Math.Min(Width / content.Width, Height / content.Height);
        if (!enlarge) scale = Math.Min(1, scale);

        var width = content.Width * scale;
        var height = content.Height * scale;

        return new Rect(
            Math.Round(X + (Width - width) / 2),
            Math.Round(Y + (Height - height) / 2),
            Math.Round(width),
            Math.Round(height));
    }

    /// <summary>Aspect-preserving fill, centred: covers this rect entirely, overflowing on one axis
    /// for the caller to clip. Unrounded - the edges that would round are the clipped ones.</summary>
    public Rect Cover(Size content)
    {
        if (content.Width <= 0 || content.Height <= 0 || IsEmpty) return this;

        var scale = Math.Max(Width / content.Width, Height / content.Height);
        var width = content.Width * scale;
        var height = content.Height * scale;

        return new Rect(X + (Width - width) / 2, Y + (Height - height) / 2, width, height);
    }
}

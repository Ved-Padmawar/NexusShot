namespace NexusShot.Core;

/// <summary>
/// Replaces <c>Windows.Foundation.Point</c>. Kept structurally identical (double X/Y, value
/// equality, <c>Left/Top/Right/Bottom</c> on <see cref="Rect"/>) so the editing logic ported
/// from the XAML build compiles unchanged against it.
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
}

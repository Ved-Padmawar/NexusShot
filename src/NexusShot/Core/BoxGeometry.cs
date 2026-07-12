namespace NexusShot.Core;

/// <summary>
/// Canonical geometry operations for every rectangular editor interaction. Crop frames,
/// box annotations, and text overlays must use these operations so crossing handles, image
/// bounds, minimum sizes, and hit targets behave identically.
/// </summary>
public static class BoxGeometry
{
    public static readonly ResizeHandle[] Handles =
    [
        ResizeHandle.TopLeft, ResizeHandle.Top, ResizeHandle.TopRight,
        ResizeHandle.Left, ResizeHandle.Right,
        ResizeHandle.BottomLeft, ResizeHandle.Bottom, ResizeHandle.BottomRight,
    ];

    public static Point HandlePosition(Rect bounds, ResizeHandle handle) => handle switch
    {
        ResizeHandle.TopLeft => new(bounds.Left, bounds.Top),
        ResizeHandle.Top => new(bounds.Left + bounds.Width / 2, bounds.Top),
        ResizeHandle.TopRight => new(bounds.Right, bounds.Top),
        ResizeHandle.Left => new(bounds.Left, bounds.Top + bounds.Height / 2),
        ResizeHandle.Right => new(bounds.Right, bounds.Top + bounds.Height / 2),
        ResizeHandle.BottomLeft => new(bounds.Left, bounds.Bottom),
        ResizeHandle.Bottom => new(bounds.Left + bounds.Width / 2, bounds.Bottom),
        ResizeHandle.BottomRight => new(bounds.Right, bounds.Bottom),
        _ => throw new ArgumentOutOfRangeException(nameof(handle), handle, "Not a box handle."),
    };

    public static ResizeHandle? HitTestHandle(Rect bounds, Point point, double tolerance)
    {
        var toleranceSquared = tolerance * tolerance;
        foreach (var handle in Handles)
        {
            var position = HandlePosition(bounds, handle);
            var dx = point.X - position.X;
            var dy = point.Y - position.Y;
            if (dx * dx + dy * dy <= toleranceSquared) return handle;
        }
        return null;
    }

    public static Rect Resize(
        Rect origin,
        ResizeHandle handle,
        Point pointer,
        Rect limits,
        Size minimumSize = default)
    {
        var (left, top, right, bottom) = handle switch
        {
            ResizeHandle.TopLeft => (pointer.X, pointer.Y, origin.Right, origin.Bottom),
            ResizeHandle.Top => (origin.Left, pointer.Y, origin.Right, origin.Bottom),
            ResizeHandle.TopRight => (origin.Left, pointer.Y, pointer.X, origin.Bottom),
            ResizeHandle.Left => (pointer.X, origin.Top, origin.Right, origin.Bottom),
            ResizeHandle.Right => (origin.Left, origin.Top, pointer.X, origin.Bottom),
            ResizeHandle.BottomLeft => (pointer.X, origin.Top, origin.Right, pointer.Y),
            ResizeHandle.Bottom => (origin.Left, origin.Top, origin.Right, pointer.Y),
            ResizeHandle.BottomRight => (origin.Left, origin.Top, pointer.X, pointer.Y),
            _ => throw new ArgumentOutOfRangeException(nameof(handle), handle, "Not a box handle."),
        };

        var normalized = Rect.FromEdges(left, top, right, bottom);
        return Constrain(normalized, limits, minimumSize, handle);
    }

    public static Rect Translate(Rect bounds, double dx, double dy, Rect limits)
    {
        if (limits.Width <= 0 || limits.Height <= 0)
            return new Rect(bounds.X + dx, bounds.Y + dy, bounds.Width, bounds.Height);

        dx = Math.Clamp(dx, Math.Min(0, limits.Left - bounds.Left), Math.Max(0, limits.Right - bounds.Right));
        dy = Math.Clamp(dy, Math.Min(0, limits.Top - bounds.Top), Math.Max(0, limits.Bottom - bounds.Bottom));
        return new Rect(bounds.X + dx, bounds.Y + dy, bounds.Width, bounds.Height);
    }

    public static Rect Constrain(Rect bounds, Rect limits, Size minimumSize = default, ResizeHandle? movingHandle = null)
    {
        var left = Math.Clamp(bounds.Left, limits.Left, limits.Right);
        var right = Math.Clamp(bounds.Right, limits.Left, limits.Right);
        var top = Math.Clamp(bounds.Top, limits.Top, limits.Bottom);
        var bottom = Math.Clamp(bounds.Bottom, limits.Top, limits.Bottom);

        var minWidth = Math.Min(Math.Max(0, minimumSize.Width), limits.Width);
        var minHeight = Math.Min(Math.Max(0, minimumSize.Height), limits.Height);
        if (right - left < minWidth)
        {
            if (movingHandle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft)
                left = Math.Max(limits.Left, right - minWidth);
            else
                right = Math.Min(limits.Right, left + minWidth);
        }
        if (bottom - top < minHeight)
        {
            if (movingHandle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight)
                top = Math.Max(limits.Top, bottom - minHeight);
            else
                bottom = Math.Min(limits.Bottom, top + minHeight);
        }
        return new Rect(left, top, right - left, bottom - top);
    }
}

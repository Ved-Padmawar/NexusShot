namespace NexusShot.Core;

/// <summary>
/// Placement on the full-desktop pickers - the region overlay and the eyedropper - in the overlay's
/// client pixels. Each piece sits beside what the pointer is aiming at and flips at a screen edge, so
/// it never covers the pixels being chosen.
/// </summary>
public static class OverlayGeometry
{
    /// <summary>The size badge's top-left: <paramref name="gap"/> below the selection, above it when
    /// there is no room below, and slid sideways to stay on the desktop.</summary>
    public static Point SizeBadge(Rect selection, Size badge, Size desktop, double gap)
    {
        var y = selection.Bottom + gap;
        if (y + badge.Height > desktop.Height) y = Math.Max(0, selection.Top - badge.Height - gap);
        var x = Math.Clamp(selection.X, 0, Math.Max(0, desktop.Width - badge.Width));
        return new Point(x, y);
    }

    /// <summary>The loupe's top-left: <paramref name="offset"/> below-right of the pointer, moved to
    /// the pointer's other side on whichever axis would run off the desktop. <paramref name="loupe"/>
    /// includes the label hung beneath it.</summary>
    public static Point Loupe(Point pointer, Size loupe, double offset, Size desktop)
    {
        var x = pointer.X + offset;
        var y = pointer.Y + offset;
        if (x + loupe.Width > desktop.Width) x = pointer.X - offset - loupe.Width;
        if (y + loupe.Height > desktop.Height) y = pointer.Y - offset - loupe.Height;
        return new Point(x, y);
    }
}

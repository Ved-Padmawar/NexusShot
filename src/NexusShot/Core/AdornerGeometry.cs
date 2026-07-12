namespace NexusShot.Core;

/// <summary>
/// The exact geometry of selection and crop adorners, as pure maths. This is the precision the
/// Kept framework-free so the renderer cannot drift from it.
///
/// Note on stroke alignment: XAML shapes always centre their stroke on the layout bounds, so the
/// old renderer had to inset every rect by half a stroke to keep the paint inside the shape.
/// Direct2D centres strokes on the geometry too, so the same inset applies and the visual result
/// is identical.
/// </summary>
public static class AdornerGeometry
{
    /// <summary>Insets a centred stroke so its complete footprint stays inside the bounds.</summary>
    public static Rect InsetForStroke(Rect bounds, double thickness) => bounds.Deflate(thickness / 2);

    /// <summary>The four dim bands around a region, clipped to the canvas.</summary>
    public static IEnumerable<Rect> DimAround(Rect region, double canvasWidth, double canvasHeight)
    {
        if (double.IsNaN(canvasWidth) || double.IsNaN(canvasHeight)) yield break;

        var top = Math.Clamp(region.Top, 0, canvasHeight);
        var bottom = Math.Clamp(region.Bottom, 0, canvasHeight);
        var left = Math.Clamp(region.Left, 0, canvasWidth);
        var right = Math.Clamp(region.Right, 0, canvasWidth);

        var bands = new[]
        {
            new Rect(0, 0, canvasWidth, top),
            new Rect(0, bottom, canvasWidth, canvasHeight - bottom),
            new Rect(0, top, left, bottom - top),
            new Rect(right, top, canvasWidth - right, bottom - top),
        };

        foreach (var band in bands)
        {
            if (band.Width > 0 && band.Height > 0) yield return band;
        }
    }

    /// <summary>Grip metrics for a box. Arms shrink on tiny boxes so opposing grips never cross.
    /// <paramref name="adornerScale"/> is the inverse display scale, keeping grips a constant
    /// on-screen size however far the image is zoomed.</summary>
    public static GripMetrics Grips(Rect bounds, double adornerScale) => new(
        Thickness: 4 * adornerScale,
        UnderlayPad: 1.5 * adornerScale,
        Arm: Math.Min(18 * adornerScale, Math.Min(bounds.Width, bounds.Height) / 3),
        Bar: Math.Min(26 * adornerScale, Math.Min(bounds.Width, bounds.Height) / 3));

    /// <summary>
    /// The centreline of one grip, offset inward by <paramref name="inset"/> so the full stroke
    /// stays inside the box. Corners are L-shaped; edges are short bars. Returns null for a
    /// handle that has no path (line endpoints, or a box too small to host it).
    /// </summary>
    public static Point[]? GripPath(ResizeHandle handle, Rect bounds, double arm, double bar, double inset)
    {
        var (l, t, r, b) = (bounds.Left + inset, bounds.Top + inset, bounds.Right - inset, bounds.Bottom - inset);
        if (r < l || b < t) return null;
        var cx = bounds.Left + bounds.Width / 2;
        var cy = bounds.Top + bounds.Height / 2;
        var half = bar / 2;

        return handle switch
        {
            ResizeHandle.TopLeft => [new(l + arm, t), new(l, t), new(l, t + arm)],
            ResizeHandle.TopRight => [new(r - arm, t), new(r, t), new(r, t + arm)],
            ResizeHandle.BottomLeft => [new(l, b - arm), new(l, b), new(l + arm, b)],
            ResizeHandle.BottomRight => [new(r, b - arm), new(r, b), new(r - arm, b)],
            ResizeHandle.Top => [new(cx - half, t), new(cx + half, t)],
            ResizeHandle.Bottom => [new(cx - half, b), new(cx + half, b)],
            ResizeHandle.Left => [new(l, cy - half), new(l, cy + half)],
            ResizeHandle.Right => [new(r, cy - half), new(r, cy + half)],
            _ => null,
        };
    }

    /// <summary>
    /// Where a selected annotation's grips sit. Rectangles and ellipses draw their own stroke, so
    /// grips ride that stroke's rendered centreline (one half-stroke inset in) and no extra
    /// dashed frame is drawn. Everything else has no outline of its own, so it gets a dashed frame
    /// and its grips ride that.
    /// </summary>
    public static SelectionAdorner Selection(Annotation annotation, double adornerScale)
    {
        var bounds = annotation.Bounds;

        if (annotation.Tool is EditorTool.Rectangle or EditorTool.Ellipse)
        {
            // Grips ride the shape's own painted stroke centreline, not a second inset past it.
            return new SelectionAdorner(
                GripBounds: InsetForStroke(bounds, annotation.StrokeThickness),
                DashedFrame: null,
                FrameThickness: 0);
        }

        var frameThickness = 1.5 * adornerScale;
        var frameBounds = InsetForStroke(bounds, frameThickness);
        return new SelectionAdorner(
            GripBounds: frameBounds,
            DashedFrame: frameBounds,
            FrameThickness: frameThickness);
    }

    /// <summary>The live crop frame: a solid frame plus grips riding its centreline.</summary>
    public static CropAdorner Crop(Rect crop, double adornerScale)
    {
        var frameThickness = 1.5 * adornerScale;
        var frameBounds = InsetForStroke(crop, frameThickness);
        return new CropAdorner(
            Frame: frameBounds,
            FrameThickness: frameThickness,
            GripBounds: frameBounds);
    }
}

public readonly record struct GripMetrics(double Thickness, double UnderlayPad, double Arm, double Bar);

/// <summary><paramref name="DashedFrame"/> is null for shapes that already draw their own outline.</summary>
public readonly record struct SelectionAdorner(Rect GripBounds, Rect? DashedFrame, double FrameThickness);

public readonly record struct CropAdorner(Rect Frame, double FrameThickness, Rect GripBounds);

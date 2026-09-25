namespace NexusShot.Core;

/// <summary>
/// Where the image sits on the editor's stage: fit, or an explicit zoom with a pan.
///
/// Scale is image pixels to physical pixels, so 1 means one image pixel per screen pixel - "100%"
/// never resamples. The placement is recomputed from the well on every call rather than stored, so it
/// cannot lag a resize; only the user's intent (zoom and pan) is kept.
/// </summary>
public sealed class Viewport
{
    public const double MinZoom = 0.1;
    public const double MaxZoom = 4;
    public const double Step = 1.2;

    /// <summary>The explicit zoom, or null to fit.</summary>
    public double? Zoom { get; private set; }

    /// <summary>Offset from the centred position, in client pixels. Only meaningful while the image
    /// overflows the well; a fitting image is always centred.</summary>
    private Point _pan;

    public bool IsFit => Zoom is null;

    /// <summary>Fits the image to the well. Fit never enlarges past 1:1: upscaling would soften the
    /// capture and inflate every stroke drawn over it.</summary>
    public static double FitScale(Rect well, Size image) =>
        image.Width <= 0 || image.Height <= 0 || well.IsEmpty
            ? 1
            : Math.Min(1, Math.Min(well.Width / image.Width, well.Height / image.Height));

    public double Scale(Rect well, Size image) => Zoom ?? FitScale(well, image);

    /// <summary>The image's on-screen rectangle, rounded to whole pixels: a half-pixel origin
    /// resamples the bitmap. The pan is clamped here, so an overflowing image can be scrolled to
    /// each edge and no further, and one that fits is centred.</summary>
    public Rect Place(Rect well, Size image)
    {
        var scale = Scale(well, image);
        var width = image.Width * scale;
        var height = image.Height * scale;

        _pan = new Point(ClampPan(_pan.X, width, well.Width), ClampPan(_pan.Y, height, well.Height));

        return new Rect(
            Math.Round(well.X + (well.Width - width) / 2 + _pan.X),
            Math.Round(well.Y + (well.Height - height) / 2 + _pan.Y),
            width, height);
    }

    public void Fit()
    {
        Zoom = null;
        _pan = Point.Zero;
    }

    /// <summary>1:1, keeping the image point under <paramref name="anchor"/> where it is.</summary>
    public void Actual(Rect well, Size image, Point anchor) => SetZoom(1, well, image, anchor);

    /// <summary>Multiplies the zoom, keeping the image point under <paramref name="anchor"/> still -
    /// the pointer for a wheel, the well's centre for a button.</summary>
    public void ZoomBy(double factor, Rect well, Size image, Point anchor) =>
        SetZoom(Scale(well, image) * factor, well, image, anchor);

    /// <summary>Scrolls an overflowing image. The clamp in <see cref="Place"/> stops it at the edges.</summary>
    public void PanBy(double dx, double dy) => _pan = new Point(_pan.X + dx, _pan.Y + dy);

    private void SetZoom(double zoom, Rect well, Size image, Point anchor)
    {
        var before = Place(well, image);
        var scale = Scale(well, image);
        var target = Math.Clamp(zoom, MinZoom, MaxZoom);

        // The image point under the anchor stays under it.
        var imageX = (anchor.X - before.X) / scale;
        var imageY = (anchor.Y - before.Y) / scale;

        Zoom = target;
        var centredX = well.X + (well.Width - image.Width * target) / 2;
        var centredY = well.Y + (well.Height - image.Height * target) / 2;
        _pan = new Point(anchor.X - imageX * target - centredX, anchor.Y - imageY * target - centredY);
    }

    /// <summary>How far an axis may move off centre: none when it fits, otherwise until an edge
    /// meets the well's edge.</summary>
    private static double ClampPan(double pan, double content, double well)
    {
        var slack = Math.Max(0, (content - well) / 2);
        return Math.Clamp(pan, -slack, slack);
    }
}

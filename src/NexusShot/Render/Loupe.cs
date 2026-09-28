using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>The magnifier the full-desktop pickers share: the pixels around the pointer drawn large
/// and unsmoothed, the centre one ringed, a caption beneath. It sits below-right of the pointer and
/// flips at the screen edges, so it never covers what is being aimed at.</summary>
public static class Loupe
{
    /// <summary>Snapshot pixels shown across, and each one's drawn size. Odd, so one pixel is the centre.</summary>
    private const int Pixels = 11;
    private const int Cell = 11;

    /// <summary><paramref name="swatch"/> is a colour well before the caption, for the eyedropper.</summary>
    public static unsafe void Draw(Ui ui, IComObject<ID2D1RenderTarget> target, ImageSurface snapshot,
        Point pointer, Size desktop, string caption, Rgba? swatch = null)
    {
        var theme = ui.Theme;
        var s = ui.Scale;
        var cell = Cell * s;
        var size = Pixels * cell;
        var label = 30 * s;

        var origin = OverlayGeometry.Loupe(pointer, new Size(size, size + label), 24 * s, desktop);
        var loupe = new Rect(origin.X, origin.Y, size, size);
        var radius = (float)(Metrics.RadiusLg * s);
        ui.Shadow(loupe, radius, 22 * s, 10 * s, Rgba.Black.WithAlpha(120));

        // Nearest-neighbour, so each screen pixel lands as a crisp square rather than a blur.
        var half = Pixels / 2;
        var source = new D2D_RECT_F(
            (float)Math.Floor(pointer.X) - half, (float)Math.Floor(pointer.Y) - half,
            (float)Math.Floor(pointer.X) + half + 1, (float)Math.Floor(pointer.Y) + half + 1);
        var destination = AnnotationRenderer.ToRect(loupe);
        ui.PushRoundedLayer(loupe, radius);
        ui.FillRect(loupe, Rgba.Black);
        target.Object.DrawBitmap(snapshot.Bitmap.Object, (nint)(&destination), 1f,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_NEAREST_NEIGHBOR, (nint)(&source));
        ui.PopLayer();
        ui.StrokeRounded(loupe, radius, Rgba.White.WithAlpha(200), (float)(2 * s));

        var centre = new Rect(loupe.X + half * cell, loupe.Y + half * cell, cell, cell);
        ui.StrokeRounded(centre.Deflate(-1 * s), 0, Rgba.Black.WithAlpha(160), (float)(3 * s));
        ui.StrokeRounded(centre, 0, Rgba.White, (float)(1.5 * s));

        var pill = new Rect(loupe.X, loupe.Bottom + 6 * s, loupe.Width, label - 6 * s);
        ui.FillRounded(pill, (float)(pill.Height / 2), theme.SurfaceRaised);
        ui.StrokeRounded(pill, (float)(pill.Height / 2), theme.StrokeDefault);
        var text = new Rect(pill.X + 10 * s, pill.Y, pill.Width - 20 * s, pill.Height);
        if (swatch is { } color)
        {
            var well = new Rect(pill.X + 6 * s, pill.Center.Y - 7 * s, 14 * s, 14 * s);
            ui.FillRounded(well, (float)(3 * s), color);
            ui.StrokeRounded(well, (float)(3 * s), theme.StrokeStrong);
            text = new Rect(well.Right + 6 * s, pill.Y, pill.Width - 26 * s, pill.Height);
        }
        ui.Text(caption, text, theme.TextPrimary, 12 * s, Weight.Semibold, face: Face.Mono);
    }
}

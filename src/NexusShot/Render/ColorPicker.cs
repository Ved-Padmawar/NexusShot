using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>
/// A hex-first colour picker. HSV is the source of truth: black and white cannot express a hue, so a
/// picker that round-trips through RGB loses the rail position as soon as you drag into a corner.
/// </summary>
public sealed class ColorPicker
{
    public const double Width = 232;
    public const double Height = 214;

    private double _hue;
    private double _saturation = 1;
    private double _value = 1;

    private bool _open;

    /// <summary>Reopens the picker on a colour, so it lands where the current colour actually is.</summary>
    public void Open(Rgba color)
    {
        (_hue, _saturation, _value) = ToHsv(color);
        _open = true;
    }

    public void Close() => _open = false;
    public bool IsOpen => _open;

    /// <summary>
    /// Draws the picker below <paramref name="anchor"/>. Returns the colour when it changes.
    ///
    /// Called last in the frame so it paints over the toolbar, and it swallows the pointer while
    /// open - a click inside the picker must not also land on whatever is underneath it.
    /// </summary>
    public Rgba? Draw(Ui ui, Rect anchor, double scale)
    {
        if (!_open) return null;

        var s = scale;
        var panel = new Rect(
            Math.Max(8 * s, anchor.X),
            anchor.Bottom + 6 * s,
            Width * s,
            Height * s);

        // Clicking outside closes it - and does not fall through to the canvas underneath.
        if (ui.PointerPressed && !panel.Contains(ui.Pointer) && !anchor.Contains(ui.Pointer))
        {
            _open = false;
            return null;
        }

        ui.FillRounded(panel, (float)(8 * s), ui.Theme.SurfaceOverlay);
        ui.StrokeRounded(panel, (float)(8 * s), ui.Theme.StrokeDefault);

        Rgba? picked = null;

        var padding = 12 * s;
        var field = new Rect(
            panel.X + padding,
            panel.Y + padding,
            panel.Width - padding * 2,
            120 * s);

        if (DrawSpectrum(ui, field)) picked = Current;

        var rail = new Rect(field.X, field.Bottom + 10 * s, field.Width, 14 * s);
        if (DrawHueRail(ui, rail)) picked = Current;

        // The readout: a swatch and the hex, so the panel states its result.
        var readout = new Rect(field.X, rail.Bottom + 12 * s, field.Width, 26 * s);

        var chip = new Rect(readout.X, readout.Y, 26 * s, 26 * s);
        ui.FillRounded(chip, (float)(4 * s), Current);
        ui.StrokeRounded(chip, (float)(4 * s), ui.Theme.StrokeStrong);

        ui.Text(Current.ToHex(),
            new Rect(chip.Right + 8 * s, readout.Y, readout.Width - 34 * s, readout.Height),
            ui.Theme.TextPrimary, (float)(Metrics.FontBody * s), monospace: true);

        return picked;
    }

    private Rgba Current => FromHsv(_hue, _saturation, _value);

    /// <summary>
    /// The saturation/value field: full-saturation hue in the corner, white across, black down.
    ///
    /// Drawn as vertical strips rather than a gradient mesh - a 200px field is 200 fills, which is
    /// nothing on a GPU, and it avoids standing up a gradient brush per frame.
    /// </summary>
    private bool DrawSpectrum(Ui ui, Rect field)
    {
        var hue = FromHsv(_hue, 1, 1);
        var columns = Math.Max(1, (int)field.Width);

        for (var i = 0; i < columns; i++)
        {
            var saturation = i / (double)columns;
            var top = Lerp(Rgba.White, hue, saturation);

            // Each column runs from its saturated top colour down to black.
            var rows = Math.Max(1, (int)(field.Height / 2));
            for (var j = 0; j < rows; j++)
            {
                var value = 1 - j / (double)rows;
                ui.FillRect(
                    new Rect(field.X + i, field.Y + j * (field.Height / rows),
                        1.5, field.Height / rows + 1),
                    Scale(top, value));
            }
        }

        ui.StrokeRounded(field, 0, ui.Theme.StrokeSubtle);

        var thumb = new Point(
            field.X + _saturation * field.Width,
            field.Y + (1 - _value) * field.Height);

        ui.StrokeCircle(thumb, (float)(7 * (field.Width / (Width - 24))), Rgba.Black.WithAlpha(160), 3f);
        ui.StrokeCircle(thumb, (float)(7 * (field.Width / (Width - 24))), Rgba.White, 1.6f);

        if (!ui.Interact(7101, field) && !ui.IsActive(7101)) return false;
        if (!ui.IsActive(7101)) return false;

        _saturation = Math.Clamp((ui.Pointer.X - field.X) / field.Width, 0, 1);
        _value = Math.Clamp(1 - (ui.Pointer.Y - field.Y) / field.Height, 0, 1);
        return true;
    }

    private bool DrawHueRail(Ui ui, Rect rail)
    {
        var columns = Math.Max(1, (int)rail.Width);
        for (var i = 0; i < columns; i++)
        {
            var hue = i / (double)columns * 360;
            ui.FillRect(new Rect(rail.X + i, rail.Y, 1.5, rail.Height), FromHsv(hue, 1, 1));
        }
        ui.StrokeRounded(rail, 0, ui.Theme.StrokeSubtle);

        var x = rail.X + _hue / 360 * rail.Width;
        ui.FillRect(new Rect(x - 1.5, rail.Y - 3, 3, rail.Height + 6), Rgba.White);
        ui.StrokeRounded(new Rect(x - 2.5, rail.Y - 4, 5, rail.Height + 8), 1,
            Rgba.Black.WithAlpha(160));

        if (!ui.Interact(7102, rail) && !ui.IsActive(7102)) return false;
        if (!ui.IsActive(7102)) return false;

        _hue = Math.Clamp((ui.Pointer.X - rail.X) / rail.Width, 0, 1) * 360;
        return true;
    }

    private static Rgba Lerp(Rgba a, Rgba b, double t) => new(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private static Rgba Scale(Rgba color, double value) => new(
        (byte)(color.R * value),
        (byte)(color.G * value),
        (byte)(color.B * value));

    public static Rgba FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;

        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;

        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return new Rgba(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    public static (double Hue, double Saturation, double Value) ToHsv(Rgba color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue = 0;
        if (delta > 0.0001)
        {
            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
        }

        var saturation = max <= 0.0001 ? 0 : delta / max;
        return (((hue % 360) + 360) % 360, saturation, max);
    }
}

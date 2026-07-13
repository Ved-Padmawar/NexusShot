using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>
/// A hex-first colour picker. HSV is the source of truth: black and white cannot express a hue, so a
/// picker that round-trips through RGB loses the rail position as soon as you drag into a corner.
///
/// The hex and R/G/B boxes are editable: a colour you can name is one you can match to a brand or a
/// spec, which dragging a spectrum by eye cannot do.
/// </summary>
public sealed class ColorPicker
{
    public const double Width = 232;
    public const double Height = 262;

    private double _hue;
    private double _saturation = 1;
    private double _value = 1;

    private bool _open;

    /// <summary>The box being typed into, if any, and the text as typed - which is deliberately not
    /// the committed colour: a half-typed "#F" must not repaint the canvas.</summary>
    private Field _editing = Field.None;
    private string _draft = "";

    private enum Field { None, Hex, Red, Green, Blue }

    /// <summary>Reopens the picker on a colour, so it lands where the current colour actually is.</summary>
    public void Open(Rgba color)
    {
        (_hue, _saturation, _value) = ToHsv(color);
        _editing = Field.None;
        _open = true;
    }

    public void Close()
    {
        _open = false;
        _editing = Field.None;
    }

    public bool IsOpen => _open;

    /// <summary>True while a box has the keyboard, so the window sends it the keys instead of acting
    /// on them as tool shortcuts - typing "E" into the hex box must not select the ellipse.</summary>
    public bool IsEditing => _open && _editing != Field.None;

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

        // Clicking outside closes it - and does not fall through to the canvas underneath. A box
        // being typed into commits first: dismissing the panel is not a reason to discard the edit.
        if (ui.PointerPressed && !panel.Contains(ui.Pointer) && !anchor.Contains(ui.Pointer))
        {
            var committed = Commit();
            _editing = Field.None;
            _open = false;
            return committed ? Current : null;
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

        // Dragging the spectrum or the rail takes the keyboard back from whichever box had it.
        if (DrawSpectrum(ui, field))
        {
            _editing = Field.None;
            picked = Current;
        }

        var rail = new Rect(field.X, field.Bottom + 10 * s, field.Width, 14 * s);
        if (DrawHueRail(ui, rail))
        {
            _editing = Field.None;
            picked = Current;
        }

        // The readout: a swatch, then the hex box it names.
        var readout = new Rect(field.X, rail.Bottom + 12 * s, field.Width, 26 * s);

        var chip = new Rect(readout.X, readout.Y, 26 * s, 26 * s);
        ui.FillRounded(chip, (float)(4 * s), Current);
        ui.StrokeRounded(chip, (float)(4 * s), ui.Theme.StrokeStrong);

        var hexBox = new Rect(chip.Right + 8 * s, readout.Y,
            readout.Right - chip.Right - 8 * s, 26 * s);
        if (TextBox(ui, 7103, hexBox, Field.Hex, Current.ToHex(), s)) picked = Current;

        // R / G / B, three equal boxes under the hex, each labelled beneath.
        var channels = new Rect(field.X, readout.Bottom + 10 * s, field.Width, 26 * s);
        var gap = 8 * s;
        var boxWidth = (channels.Width - gap * 2) / 3;

        var current = Current;
        ReadOnlySpan<Field> order = [Field.Red, Field.Green, Field.Blue];
        ReadOnlySpan<byte> values = [current.R, current.G, current.B];
        ReadOnlySpan<string> labels = ["R", "G", "B"];

        for (var i = 0; i < 3; i++)
        {
            var box = new Rect(channels.X + (boxWidth + gap) * i, channels.Y, boxWidth, channels.Height);

            if (TextBox(ui, 7104 + i, box, order[i], values[i].ToString(), s)) picked = Current;

            ui.Text(labels[i], new Rect(box.X, box.Bottom + 2 * s, box.Width, 14 * s),
                ui.Theme.TextTertiary, (float)(Metrics.FontCaption * s), align: TextAlign.Center);
        }

        return picked;
    }

    /// <summary>An editable box. Clicking focuses it and seeds the draft with the live value; the
    /// window feeds keys in while it holds focus.</summary>
    private bool TextBox(Ui ui, int id, Rect bounds, Field field, string live, double s)
    {
        // Focus, then fall through and draw. Returning here would skip the fill, border and text,
        // so the box just clicked would be the one box not painted this frame.
        if (ui.Interact(id, bounds) && _editing != field)
        {
            Commit();            // Moving between boxes commits the one being left.
            _editing = field;
            _draft = live;
        }

        var focused = _editing == field;

        var radius = (float)(4 * s);
        ui.FillRounded(bounds, radius, ui.Theme.SurfaceSunken);
        ui.StrokeRounded(bounds, radius,
            focused ? ui.Theme.Accent : ui.IsHot(id) ? ui.Theme.StrokeStrong : ui.Theme.StrokeDefault,
            focused ? 1.5f : 1f);

        // The draft while typing, the live value otherwise: a box you are not in always tells the
        // truth about the colour.
        var text = focused ? _draft : live;
        var padding = 8 * s;
        var inner = new Rect(bounds.X + padding, bounds.Y, bounds.Width - padding * 2, bounds.Height);

        ui.Text(text, inner, ui.Theme.TextPrimary, (float)(Metrics.FontBody * s), monospace: true);

        // A caret, so a focused empty box does not look like a dead one.
        if (focused)
        {
            var caretX = inner.X + ui.MeasureText(text, Metrics.FontBody * s, monospace: true) + 1 * s;
            ui.FillRect(new Rect(caretX, bounds.Y + 6 * s, 1.5 * s, bounds.Height - 12 * s),
                ui.Theme.TextPrimary);
        }

        return false;
    }

    /// <summary>Feeds a key to the focused box, returning true when the colour changed. The window
    /// routes keys here before its own shortcuts, or typing would drive the toolbar.</summary>
    public bool HandleKey(char character, bool backspace, bool enter, bool escape, out Rgba? color)
    {
        color = null;
        if (_editing == Field.None) return false;

        if (escape)
        {
            _editing = Field.None;
            return false;
        }

        if (enter)
        {
            var changed = Commit();
            _editing = Field.None;
            if (changed) color = Current;
            return changed;
        }

        if (backspace)
        {
            if (_draft.Length > 0) _draft = _draft[..^1];
            return false;
        }

        if (character != '\0' && Accepts(character)) _draft += character;
        return false;
    }

    /// <summary>Only what the focused box can hold: hex digits and a hash, or decimal digits.</summary>
    private bool Accepts(char character) => _editing switch
    {
        Field.Hex => _draft.Length < 7
            && (char.IsAsciiHexDigit(character) || (character == '#' && _draft.Length == 0)),
        Field.Red or Field.Green or Field.Blue => _draft.Length < 3 && char.IsAsciiDigit(character),
        _ => false,
    };

    /// <summary>Applies the draft to the colour. A draft that does not parse is dropped, so the box
    /// reverts to the live value rather than blanking the colour.</summary>
    private bool Commit()
    {
        if (_editing == Field.None) return false;

        var current = Current;
        Rgba target;

        if (_editing == Field.Hex)
        {
            if (!TryParseHex(_draft, out target)) return false;
        }
        else
        {
            if (!byte.TryParse(_draft, out var channel)) return false;
            target = _editing switch
            {
                Field.Red => new Rgba(channel, current.G, current.B),
                Field.Green => new Rgba(current.R, channel, current.B),
                _ => new Rgba(current.R, current.G, channel),
            };
        }

        if (target.R == current.R && target.G == current.G && target.B == current.B) return false;

        Adopt(target);
        return true;
    }

    /// <summary>Rebuilds HSV from an RGB colour, keeping the hue the user was on when the new colour
    /// is a grey that carries none of its own - otherwise the rail jumps to red on every black.</summary>
    private void Adopt(Rgba color)
    {
        var (hue, saturation, value) = ToHsv(color);
        _hue = saturation <= 0 ? _hue : hue;
        _saturation = saturation;
        _value = value;
    }

    /// <summary>Accepts #RGB, #RRGGBB, and either without the hash.</summary>
    public static bool TryParseHex(string? text, out Rgba color)
    {
        color = Rgba.Black;

        var hex = text?.Trim().TrimStart('#');
        if (string.IsNullOrEmpty(hex)) return false;

        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        if (hex.Length != 6) return false;

        const System.Globalization.NumberStyles style = System.Globalization.NumberStyles.HexNumber;
        if (!byte.TryParse(hex.AsSpan(0, 2), style, null, out var r)
            || !byte.TryParse(hex.AsSpan(2, 2), style, null, out var g)
            || !byte.TryParse(hex.AsSpan(4, 2), style, null, out var b))
            return false;

        color = new Rgba(r, g, b);
        return true;
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

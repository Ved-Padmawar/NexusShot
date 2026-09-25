using System.Globalization;
using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>What the picker asked for this frame. The picker changes nothing outside itself: the
/// window applies the colour, runs the eyedropper, copies text and edits the colour lists.
/// <see cref="Live"/> marks a step of a drag, which shares one undo entry with the rest of it.</summary>
public readonly record struct PickerResult(
    Rgba? Color, bool Live, bool PickFromScreen, string? Copy, string? Save, string? Forget, bool Closed);

/// <summary>
/// The colour picker popover. HSV plus alpha is the one state: black and white carry no hue, so a
/// picker that stored RGB would lose the rail position the moment it touched a corner, and every
/// model it shows - HEX, RGB, HSL, HSB - is a view onto that one value, so switching models never
/// rounds the colour.
///
/// Numbers can be typed, stepped with the arrow keys, or scrubbed by dragging a field's label. A
/// typed value applies once it is complete and valid, so a half-typed hex never repaints the canvas.
/// </summary>
public sealed class ColorPicker
{
    /// <summary>Wide enough that a value box holds its longest value - "360" and a unit - without
    /// crowding.</summary>
    public const double Width = 336;

    private enum Model { Hex, Rgb, Hsl, Hsb }
    private static readonly string[] ModelNames = ["HEX", "RGB", "HSL", "HSB"];

    private Hsva _color;
    private Rgba _original;
    private Model _model;
    private bool _open;
    private Rect _bounds;

    /// <summary>The value a scrub started from; the drag is measured against it.</summary>
    private double _scrubStart;

    public bool IsOpen => _open;

    /// <summary>Where the picker is, so the window can keep the canvas from seeing clicks inside it.</summary>
    public bool Contains(Point point) => _open && _bounds.Contains(point);

    public void Open(Rgba color)
    {
        _color = Hsva.From(color);
        _original = color;
        _open = true;
    }

    public void Close() => _open = false;

    /// <summary>A colour picked from the screen, taken as if it had been chosen here.</summary>
    public void Adopt(Rgba color) => _color = Hsva.From(color, _color.Hue);

    public Rgba Current => _color.ToRgba();

    /// <summary>
    /// Draws the picker with its bottom-left near <paramref name="anchor"/>'s top-left, kept inside
    /// <paramref name="within"/>. Called last in the frame, so it paints over the chrome under it.
    /// </summary>
    public PickerResult Draw(Ui ui, Rect anchor, Rect within,
        IReadOnlyList<string> recent, IReadOnlyList<string> saved)
    {
        if (!_open) return default;

        var s = ui.Scale;
        double S(double units) => units * s;

        var width = S(Width);
        var height = S(464);
        var x = Math.Clamp(anchor.X - S(8), within.X + S(8), within.Right - width - S(8));
        var y = Math.Max(within.Y + S(8), anchor.Y - S(12) - height);
        _bounds = new Rect(x, y, width, height);

        // Clicking outside closes it - and does not fall through to whatever is underneath.
        if (ui.PointerPressed && !_bounds.Contains(ui.Pointer) && !anchor.Contains(ui.Pointer))
        {
            _open = false;
            return new PickerResult(null, false, false, null, null, null, Closed: true);
        }

        var theme = ui.Theme;
        var appear = ui.Animate(Ui.Id("picker.appear"), 1, Metrics.Motion);
        var panel = new Rect(_bounds.X, _bounds.Y + S(6) * (1 - appear), _bounds.Width, _bounds.Height);
        var radius = (float)S(Metrics.RadiusXl);
        ui.FloatShadow(panel, radius);
        ui.FillRounded(panel, radius, theme.SurfaceRaised);
        ui.StrokeRounded(panel, radius, theme.StrokeDefault);

        Rgba? color = null;
        var live = false;
        var pick = false;
        string? copy = null, save = null, forget = null;

        var left = panel.X + S(12);
        var inner = panel.Width - S(24);
        var top = panel.Y + S(12);

        // ---- header: before/after, the model tabs, the eyedropper ----
        var compare = new Rect(left, top, S(44), S(28));
        ui.FillChecker(compare, (float)S(Metrics.RadiusSm));
        var before = new Rect(compare.X, compare.Y, compare.Width / 2, compare.Height);
        var after = new Rect(before.Right, compare.Y, compare.Width / 2, compare.Height);
        var revertId = Ui.Id("picker.revert");
        if (ui.Interact(revertId, before))
        {
            _color = Hsva.From(_original, _color.Hue);
            color = _original;
        }
        ui.FillRect(before, _original);
        ui.FillRect(after, _color.ToRgba());
        ui.StrokeRounded(compare, (float)S(Metrics.RadiusSm), theme.StrokeDefault);
        if (ui.IsHot(revertId)) ui.StrokeRounded(before.Deflate(S(1)), 0, Rgba.White.WithAlpha(128), (float)S(2));
        ui.Tip(revertId, before, $"Revert to {_original.ToHex()[1..]}");

        var dropper = new Rect(panel.Right - S(12) - S(28), top, S(28), S(28));
        if (ui.IconButton(Ui.Id("picker.dropper"), dropper, Icons.Dropper, "Pick from screen", "I", iconSize: 15))
            pick = true;

        var tabs = new Rect(compare.Right + S(8), top, dropper.X - S(8) - compare.Right - S(8), S(28));
        DrawModelTabs(ui, tabs);

        top += S(38);

        // ---- saturation/value field ----
        var field = new Rect(left, top, inner, S(156));
        if (DrawSpectrum(ui, field)) { color = _color.ToRgba(); live = true; }
        top = field.Bottom + S(10);

        // ---- hue and alpha rails ----
        var hueRail = new Rect(left, top, inner, S(14));
        if (DrawHueRail(ui, hueRail)) { color = _color.ToRgba(); live = true; }
        top = hueRail.Bottom + S(10);

        var alphaRail = new Rect(left, top, inner, S(14));
        if (DrawAlphaRail(ui, alphaRail)) { color = _color.ToRgba(); live = true; }
        top = alphaRail.Bottom + S(10);

        // ---- the fields for the current model ----
        var fields = new Rect(left, top, inner, S(30));
        if (DrawFields(ui, fields, out var scrubbing, ref copy)) { color = _color.ToRgba(); live = scrubbing; }
        top = fields.Bottom + S(10);

        // ---- shades, recent, saved ----
        top = Section(ui, "Shades", left, top, inner);
        if (DrawShades(ui, new Rect(left, top, inner, S(24))) is { } shade)
        {
            _color = Hsva.From(shade, _color.Hue);
            color = shade;
        }
        top += S(24) + S(10);

        top = Section(ui, "Recent", left, top, inner);
        if (recent.Count == 0)
            ui.Text("Colours you use appear here", new Rect(left, top, inner, S(22)), theme.TextQuaternary, S(11.5));
        else if (DrawChips(ui, Ui.Id("picker.recent"), left, top, recent, addButton: false, out _) is { } used)
        {
            _color = Hsva.From(used, _color.Hue);
            color = used;
        }
        top += S(22) + S(10);

        top = Section(ui, "Saved", left, top, inner);
        if (DrawChips(ui, Ui.Id("picker.saved"), left, top, saved, addButton: true, out var add) is { } kept)
        {
            if (IsShiftDown()) forget = kept.ToHex();
            else
            {
                _color = Hsva.From(kept, _color.Hue);
                color = kept;
            }
        }
        if (add) save = _color.ToRgba().ToHex();

        return new PickerResult(color, live, pick, copy, save, forget, Closed: false);
    }

    /// <summary>Shift-click on a saved colour removes it. The Ui only sees the pointer, so the window
    /// supplies the modifier state.</summary>
    public Func<bool> IsShiftDown { get; set; } = () => false;

    private void DrawModelTabs(Ui ui, Rect bounds)
    {
        var s = ui.Scale;
        ui.FillRounded(bounds, (float)(Metrics.RadiusSm * s), ui.Theme.SurfacePane);
        ui.StrokeRounded(bounds, (float)(Metrics.RadiusSm * s), ui.Theme.StrokeSubtle);

        var inner = bounds.Deflate(2 * s);
        var tab = inner.Width / ModelNames.Length;
        for (var i = 0; i < ModelNames.Length; i++)
        {
            var slot = new Rect(inner.X + tab * i, inner.Y, tab, inner.Height);
            var id = Ui.Id(Ui.Id("picker.model"), i);
            if (ui.Interact(id, slot)) { _model = (Model)i; ui.Blur(); }

            var on = (int)_model == i;
            if (on) ui.FillRounded(slot, (float)(Metrics.RadiusXs * s), ui.Theme.SurfacePressed);
            ui.Text(ModelNames[i], slot, on || ui.IsHot(id) ? ui.Theme.TextPrimary : ui.Theme.TextTertiary,
                10.5 * s, Weight.Bold, TextAlign.Center);
        }
    }

    /// <summary>
    /// The saturation/value field: two gradients, not a fill per cell. White to the pure hue across,
    /// then transparent to black down over it, is the HSV square by definition, and the GPU
    /// interpolates both.
    /// </summary>
    private bool DrawSpectrum(Ui ui, Rect field)
    {
        var id = Ui.Id("picker.field");
        ui.Interact(id, field);

        var radius = (float)(Metrics.RadiusMd * ui.Scale);
        ui.FillRoundedGradient(field, radius, Rgba.White, new Hsva(_color.Hue, 1, 1).ToRgba(),
            new Point(field.X, field.Y), new Point(field.Right, field.Y));
        ui.FillRoundedGradient(field, radius, Rgba.Black.WithAlpha(0), Rgba.Black,
            new Point(field.X, field.Y), new Point(field.X, field.Bottom));
        ui.StrokeRounded(field, radius, ui.Theme.StrokeDefault);

        var changed = false;
        if (ui.IsActive(id))
        {
            _color = _color with
            {
                Saturation = Math.Clamp((ui.Pointer.X - field.X) / field.Width, 0, 1),
                Value = Math.Clamp(1 - (ui.Pointer.Y - field.Y) / field.Height, 0, 1),
            };
            ui.Blur();
            changed = true;
        }

        Knob(ui, new Point(field.X + _color.Saturation * field.Width, field.Y + (1 - _color.Value) * field.Height),
            _color.ToRgba() with { A = 255 });
        return changed;
    }

    /// <summary>The hue rail: six stops of the colour wheel. Six gradient spans, each drawn by the
    /// GPU, rather than a strip of one-pixel fills.</summary>
    private bool DrawHueRail(Ui ui, Rect rail)
    {
        var id = Ui.Id("picker.hue");
        ui.Interact(id, rail);

        var radius = (float)(rail.Height / 2);
        ui.PushClip(rail);
        ui.FillRounded(rail, radius, new Hsva(0, 1, 1).ToRgba());
        for (var i = 0; i < 6; i++)
        {
            var from = rail.X + rail.Width * i / 6;
            var to = rail.X + rail.Width * (i + 1) / 6;
            var span = new Rect(from, rail.Y, to - from + 1, rail.Height);
            if (i == 0) span = new Rect(from + radius, rail.Y, to - from - radius + 1, rail.Height);
            if (i == 5) span = span with { Width = to - from - radius };
            ui.FillRectGradient(span, new Hsva(i * 60, 1, 1).ToRgba(), new Hsva((i + 1) * 60 % 360, 1, 1).ToRgba(),
                new Point(from, rail.Y), new Point(to, rail.Y));
        }
        ui.PopClip();
        ui.StrokeRounded(rail, radius, ui.Theme.StrokeDefault);

        var changed = false;
        if (ui.IsActive(id))
        {
            _color = _color with { Hue = Math.Clamp((ui.Pointer.X - rail.X) / rail.Width, 0, 0.9999) * 360 };
            ui.Blur();
            changed = true;
        }
        Knob(ui, new Point(rail.X + _color.Hue / 360 * rail.Width, rail.Center.Y), new Hsva(_color.Hue, 1, 1).ToRgba());
        return changed;
    }

    /// <summary>The alpha rail: the colour fading to nothing over a checkerboard.</summary>
    private bool DrawAlphaRail(Ui ui, Rect rail)
    {
        var id = Ui.Id("picker.alpha");
        ui.Interact(id, rail);

        var radius = (float)(rail.Height / 2);
        var solid = _color.ToRgba() with { A = 255 };
        ui.FillChecker(rail, radius);
        ui.FillRoundedGradient(rail, radius, solid.WithAlpha(0), solid,
            new Point(rail.X, rail.Y), new Point(rail.Right, rail.Y));
        ui.StrokeRounded(rail, radius, ui.Theme.StrokeDefault);

        var changed = false;
        if (ui.IsActive(id))
        {
            _color = _color with { Alpha = Math.Round(Math.Clamp((ui.Pointer.X - rail.X) / rail.Width, 0, 1), 2) };
            ui.Blur();
            changed = true;
        }
        Knob(ui, new Point(rail.X + _color.Alpha * rail.Width, rail.Center.Y), _color.ToRgba(), checker: true);
        return changed;
    }

    /// <summary>A ringed knob: white ring, dark hairline, soft shadow - readable on any colour.</summary>
    private static void Knob(Ui ui, Point center, Rgba fill, bool checker = false)
    {
        var s = ui.Scale;
        var r = (float)(8 * s);
        ui.FillCircle(center with { Y = center.Y + 2 * s }, r + (float)(2 * s), Rgba.Black.WithAlpha(60));
        ui.FillCircle(center, r + (float)(1 * s), Rgba.Black.WithAlpha(64));
        ui.FillCircle(center, r, Rgba.White);
        if (checker) ui.FillChecker(new Rect(center.X - r + 2.5 * s, center.Y - r + 2.5 * s, 2 * r - 5 * s, 2 * r - 5 * s), r);
        ui.FillCircle(center, r - (float)(2.5 * s), fill);
    }

    /// <summary>The value boxes for the current model, plus alpha. Every numeric box can be typed,
    /// stepped, or scrubbed by dragging its label.</summary>
    private bool DrawFields(Ui ui, Rect row, out bool scrubbing, ref string? copy)
    {
        var s = ui.Scale;
        var gap = 6 * s;
        scrubbing = false;
        var changed = false;
        var rgba = _color.ToRgba();

        if (_model == Model.Hex)
        {
            var alphaWidth = 84 * s;
            var hexBox = new Rect(row.X, row.Y, row.Width - alphaWidth - gap, row.Height);
            var copyButton = new Rect(hexBox.Right - 26 * s, hexBox.Y + 4 * s, 22 * s, 22 * s);

            var hex = ui.Field(Ui.Id("picker.hex"), hexBox with { Width = hexBox.Width - 28 * s },
                rgba.ToHex()[1..7], char.IsAsciiHexDigit, 8, prefix: "#");
            if (hex.Changed && hex.Text.Length is 6 or 8 && Palette.TryParse(hex.Text, out var typed))
            {
                _color = Hsva.From(hex.Text.Length == 6 ? typed with { A = rgba.A } : typed, _color.Hue);
                changed = true;
            }

            if (ui.IconButton(Ui.Id("picker.copy"), copyButton, Icons.Copy, "Copy", iconSize: 14))
                copy = _color.ToRgba().ToHex();

            changed |= NumberBox(ui, Ui.Id("picker.a"), new Rect(hexBox.Right + gap, row.Y, alphaWidth, row.Height),
                "A", "%", 100, (int)Math.Round(_color.Alpha * 100), value => _color = _color with { Alpha = value / 100.0 },
                ref scrubbing);
            return changed;
        }

        var width = (row.Width - gap * 3) / 4;
        Rect Box(int index) => new(row.X + (width + gap) * index, row.Y, width, row.Height);

        switch (_model)
        {
            case Model.Rgb:
                changed |= NumberBox(ui, Ui.Id("picker.r"), Box(0), "R", null, 255, rgba.R,
                    value => _color = Hsva.From(rgba with { R = (byte)value }, _color.Hue), ref scrubbing);
                changed |= NumberBox(ui, Ui.Id("picker.g"), Box(1), "G", null, 255, rgba.G,
                    value => _color = Hsva.From(rgba with { G = (byte)value }, _color.Hue), ref scrubbing);
                changed |= NumberBox(ui, Ui.Id("picker.b"), Box(2), "B", null, 255, rgba.B,
                    value => _color = Hsva.From(rgba with { B = (byte)value }, _color.Hue), ref scrubbing);
                break;

            case Model.Hsl:
                var (hslS, hslL) = _color.ToHsl();
                changed |= NumberBox(ui, Ui.Id("picker.h"), Box(0), "H", "°", 360, (int)Math.Round(_color.Hue),
                    value => _color = _color with { Hue = Math.Min(value, 359.9) }, ref scrubbing);
                changed |= NumberBox(ui, Ui.Id("picker.sl"), Box(1), "S", "%", 100, (int)Math.Round(hslS * 100),
                    value => _color = Hsva.FromHsl(_color.Hue, value / 100.0, hslL, _color.Alpha), ref scrubbing);
                changed |= NumberBox(ui, Ui.Id("picker.l"), Box(2), "L", "%", 100, (int)Math.Round(hslL * 100),
                    value => _color = Hsva.FromHsl(_color.Hue, hslS, value / 100.0, _color.Alpha), ref scrubbing);
                break;

            case Model.Hsb:
                changed |= NumberBox(ui, Ui.Id("picker.h"), Box(0), "H", "°", 360, (int)Math.Round(_color.Hue),
                    value => _color = _color with { Hue = Math.Min(value, 359.9) }, ref scrubbing);
                changed |= NumberBox(ui, Ui.Id("picker.sv"), Box(1), "S", "%", 100, (int)Math.Round(_color.Saturation * 100),
                    value => _color = _color with { Saturation = value / 100.0 }, ref scrubbing);
                changed |= NumberBox(ui, Ui.Id("picker.v"), Box(2), "B", "%", 100, (int)Math.Round(_color.Value * 100),
                    value => _color = _color with { Value = value / 100.0 }, ref scrubbing);
                break;
        }

        changed |= NumberBox(ui, Ui.Id("picker.a"), Box(3), "A", "%", 100, (int)Math.Round(_color.Alpha * 100),
            value => _color = _color with { Alpha = value / 100.0 }, ref scrubbing);
        return changed;
    }

    /// <summary>
    /// One numeric box. The label is a scrub handle: dragging it left or right moves the value by a
    /// unit every two pixels, the way design tools let you adjust without aiming at a number.
    /// </summary>
    private bool NumberBox(Ui ui, int id, Rect bounds, string label, string? unit, int max, int value,
        Action<int> apply, ref bool scrubbing)
    {
        var s = ui.Scale;
        var result = ui.Field(id, bounds, value.ToString(CultureInfo.InvariantCulture), char.IsAsciiDigit, 3,
            prefix: label, suffix: unit);

        var changed = false;
        if (result.Changed && int.TryParse(result.Text, out var typed) && typed <= max)
        {
            apply(typed);
            changed = true;
        }
        if (result.Step != 0)
        {
            apply(Math.Clamp(value + result.Step, 0, max));
            changed = true;
        }

        // Called after the field, so the handle takes the press over the box it sits in.
        var handle = new Rect(bounds.X, bounds.Y, 20 * s, bounds.Height);
        var scrubId = Ui.Id(id, 1);
        ui.Interact(scrubId, handle);
        if (ui.IsActive(scrubId))
        {
            if (ui.PointerPressed) _scrubStart = value;
            var next = (int)Math.Clamp(Math.Round(_scrubStart + (ui.Pointer.X - ui.PressPoint.X) / (2 * s)), 0, max);
            if (next != value)
            {
                apply(next);
                changed = true;
                scrubbing = true;
            }
            ui.Blur();
        }
        return changed;
    }

    private static double Section(Ui ui, string title, double x, double y, double width)
    {
        var s = ui.Scale;
        var font = 10.5 * s;
        var label = title.ToUpperInvariant();
        var w = ui.MeasureText(label, font, Weight.Bold);
        ui.Text(label, new Rect(x, y, w + 1, 14 * s), ui.Theme.TextTertiary, font, Weight.Bold);
        ui.FillRect(new Rect(x + w + 8 * s, y + 7 * s, width - w - 8 * s, 1), ui.Theme.StrokeSubtle);
        return y + 14 * s + 6 * s;
    }

    /// <summary>Seven steps from near-white to near-black at the current hue, one strip.</summary>
    private Rgba? DrawShades(Ui ui, Rect strip)
    {
        var radius = (float)(Metrics.RadiusSm * ui.Scale);
        var (saturation, _) = _color.ToHsl();
        ReadOnlySpan<double> lightness = [0.92, 0.8, 0.66, 0.5, 0.38, 0.26, 0.14];

        Rgba? picked = null;
        var current = _color.ToRgba() with { A = 255 };
        var cell = strip.Width / lightness.Length;

        // One rounded clip for the strip: rounding the end cells made them overlap their neighbours.
        ui.PushRoundedLayer(strip, radius);
        for (var i = 0; i < lightness.Length; i++)
        {
            var shade = Hsva.FromHsl(_color.Hue, Math.Max(saturation, 0.15), lightness[i], 1).ToRgba();
            var slot = new Rect(strip.X + cell * i, strip.Y, cell + 1, strip.Height);
            var id = Ui.Id(Ui.Id("picker.shade"), i);
            if (ui.Interact(id, slot)) picked = shade;

            ui.FillRect(slot, ui.IsHot(id) ? shade.Mix(Rgba.White, 0.08) : shade);
            if (shade == current) ui.StrokeRounded(slot.Deflate(1), 0, Rgba.White, (float)(2 * ui.Scale));
        }
        ui.PopRoundedLayer();
        ui.StrokeRounded(strip, radius, ui.Theme.StrokeDefault);
        return picked;
    }

    /// <summary>A row of colour chips, optionally ending in an add button.</summary>
    private Rgba? DrawChips(Ui ui, int owner, double x, double y, IReadOnlyList<string> colors, bool addButton, out bool add)
    {
        var s = ui.Scale;
        var size = 22 * s;
        var gap = 6 * s;
        var radius = (float)(Metrics.RadiusSm * s);
        var current = _color.ToRgba();
        Rgba? picked = null;

        for (var i = 0; i < colors.Count; i++)
        {
            if (!Palette.TryParse(colors[i], out var chip)) continue;
            var box = new Rect(x + (size + gap) * i, y, size, size);
            var id = Ui.Id(owner, i);
            if (ui.Interact(id, box)) picked = chip;

            var grown = ui.Animate(id, ui.IsHot(id) ? 1.12 : 1, Metrics.MotionFast);
            var drawn = box.Deflate(-(grown - 1) * size / 2);
            if (chip == current)
                ui.StrokeRounded(drawn.Deflate(-3.5 * s), radius + (float)(3.5 * s), ui.Theme.TextPrimary, (float)(1.5 * s));
            ui.FillChecker(drawn, radius);
            ui.FillRounded(drawn, radius, chip);
            ui.StrokeRounded(drawn, radius, ui.Theme.StrokeDefault);
            ui.Tip(id, box, chip.ToHex()[1..] + (addButton ? "  ·  Shift-click to remove" : ""));
        }

        add = false;
        if (!addButton) return picked;

        var plus = new Rect(x + (size + gap) * colors.Count, y, size, size);
        var plusId = Ui.Id(owner, -1);
        add = ui.Interact(plusId, plus);
        ui.StrokeRounded(plus, radius, ui.IsHot(plusId) ? ui.Theme.StrokeStrong : ui.Theme.StrokeDefault);
        ui.Icon(Icons.Plus, plus, ui.IsHot(plusId) ? ui.Theme.TextPrimary : ui.Theme.TextTertiary, 14 * s);
        ui.Tip(plusId, plus, "Save current colour");
        return picked;
    }
}

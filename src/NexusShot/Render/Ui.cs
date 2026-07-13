using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>
/// An immediate-mode UI context: widgets are function calls, not objects. A widget owns no state, so
/// there is nothing to keep in sync with the model.
///
/// The only retained state is what genuinely persists between frames - what the pointer is over, and
/// what it is dragging - keyed by a caller-supplied id.
/// </summary>
public sealed class Ui(D2DResources resources)
{
    public D2DResources Resources => resources;

    public Theme Theme { get; set; } = Theme.Dark;

    /// <summary>Set once per frame, before any widget is called.</summary>
    public Point Pointer { get; private set; }
    public bool PointerDown { get; private set; }

    private bool _pointerPressedThisFrame;
    private bool _pointerReleasedThisFrame;

    /// <summary>True on the frame the pointer went down. Popups use it to dismiss on an outside
    /// click, which they must do before any widget under them gets the event.</summary>
    public bool PointerPressed => _pointerPressedThisFrame;

    /// <summary>The widget under the pointer, and the one being dragged. Ids are stable across
    /// frames because callers derive them from what the widget represents, not from draw order.</summary>
    public int Hot { get; private set; }
    public int Active { get; private set; }

    private IComObject<ID2D1RenderTarget> _target = null!;

    /// <summary>True when any widget wants the pointer, so the canvas ignores it.</summary>
    public bool WantsPointer => Hot != 0 || Active != 0;

    public void BeginFrame(IComObject<ID2D1RenderTarget> target, Point pointer, bool pointerDown)
    {
        _target = target;
        _pointerPressedThisFrame = pointerDown && !PointerDown;
        _pointerReleasedThisFrame = !pointerDown && PointerDown;
        Pointer = pointer;
        PointerDown = pointerDown;
        Hot = 0;

        _clips.Clear();
    }

    /// <summary>Ends the frame, unwinding any clip a caller pushed and did not pop. An unbalanced
    /// stack faults the device with D2DERR_WRONG_STATE, so a caller that returns early must not be
    /// able to leave one open.</summary>
    public void EndFrame()
    {
        while (_clips.Count > 0) Pop();
        if (_pointerReleasedThisFrame) Active = 0;
    }

    // ============================  PRIMITIVES  ============================

    public void FillRect(Rect rect, Rgba color) =>
        _target.FillRectangle(AnnotationRenderer.ToRect(rect), resources.Brush(color));

    public void FillRounded(Rect rect, float radius, Rgba color) =>
        _target.FillRoundedRectangle(Rounded(rect, radius), resources.Brush(color));

    public void StrokeRounded(Rect rect, float radius, Rgba color, float thickness = 1) =>
        _target.DrawRoundedRectangle(
            Rounded(rect.Deflate(thickness / 2), radius), resources.Brush(color), thickness);

    public void FillCircle(Point center, float radius, Rgba color) =>
        _target.FillEllipse(Ellipse(center, radius), resources.Brush(color));

    public void StrokeCircle(Point center, float radius, Rgba color, float thickness = 1) =>
        _target.DrawEllipse(Ellipse(center, radius), resources.Brush(color), thickness);

    public void Line(Point a, Point b, Rgba color, float thickness = 1) =>
        _target.DrawLine(
            AnnotationRenderer.ToPoint(a), AnnotationRenderer.ToPoint(b),
            resources.Brush(color), thickness, resources.RoundStroke);

    /// <summary>
    /// Confines drawing to a rectangle. Scrolled content must be clipped, or the rows that have run
    /// off the top of a list paint straight over the header above it.
    ///
    /// The pointer is clipped too: a widget scrolled out of view must not still be clickable where
    /// it used to be.
    /// </summary>
    public void PushClip(Rect bounds)
    {
        _clips.Push((bounds, Layer: false));
        _target.Object.PushAxisAlignedClip(
            AnnotationRenderer.ToRect(bounds), D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
    }

    public void PopClip() => Pop();

    /// <summary>Clips to a rounded rectangle, which an axis-aligned clip cannot express. Costs a
    /// layer, so it is for the odd shaped element rather than for general use.</summary>
    /// <summary>Pops whatever is on top, with the call that matches how it was pushed. A layer popped
    /// as an axis-aligned clip is what faults the device.</summary>
    private void Pop()
    {
        if (_clips.Count == 0) return;

        var (_, layer) = _clips.Pop();
        if (layer) _target.Object.PopLayer();
        else _target.Object.PopAxisAlignedClip();
    }

    /// <summary>Fills the part of a rounded rectangle that falls inside <paramref name="polygon"/>.
    /// Intersecting the geometries means the polygon picks up the rectangle's rounded corners exactly,
    /// which is what a clipping layer would give - and a layer allocates an intermediate surface that
    /// faults the device when the target is mid-resize.</summary>
    public void FillRoundedRegion(Rect bounds, float radius, IReadOnlyList<Point> polygon, Rgba color)
    {
        using var rounded = resources.Factory.CreateRoundedRectangleGeometry(Rounded(bounds, radius));
        using var shape = Path(polygon, closed: true);

        var region = resources.CreatePathGeometry();
        using (var sink = region.Open())
        {
            rounded.AsGeometry().CombineWithGeometry(
                shape.AsGeometry(), sink, D2D1_COMBINE_MODE.D2D1_COMBINE_MODE_INTERSECT);
            sink.Object.Close();
        }

        using (region)
            _target.Object.FillGeometry(region.Object, resources.Brush(color).Object, null!);
    }

    /// <summary>An open path with round caps and joins.</summary>
    public void Polyline(IReadOnlyList<Point> points, Rgba color, float thickness)
    {
        using var geometry = Path(points, closed: false);
        _target.Object.DrawGeometry(
            geometry.Object, resources.Brush(color).Object, thickness, resources.RoundStroke.Object);
    }

    private IComObject<ID2D1PathGeometry> Path(IReadOnlyList<Point> points, bool closed)
    {
        var geometry = resources.CreatePathGeometry();
        using var sink = geometry.Open();

        sink.Object.BeginFigure(
            AnnotationRenderer.ToPoint(points[0]),
            closed ? D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_FILLED
                   : D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_HOLLOW);

        for (var i = 1; i < points.Count; i++)
            sink.Object.AddLine(AnnotationRenderer.ToPoint(points[i]));

        sink.Object.EndFigure(closed ? D2D1_FIGURE_END.D2D1_FIGURE_END_CLOSED
                                     : D2D1_FIGURE_END.D2D1_FIGURE_END_OPEN);
        sink.Object.Close();

        return geometry;
    }

    /// <summary>The open clips, and whether each was pushed as a layer or an axis-aligned clip.</summary>
    private readonly Stack<(Rect Bounds, bool Layer)> _clips = new();

    /// <summary>True when the pointer is inside every active clip - and so can actually see and
    /// reach whatever is being drawn.</summary>
    private bool PointerVisible => _clips.All(clip => clip.Bounds.Contains(Pointer));

    /// <summary>Text in a box, with alignment. Returns nothing: chrome text is never hit-tested.</summary>
    public void Text(
        string text,
        Rect bounds,
        Rgba color,
        float size = Metrics.FontBody,
        bool bold = false,
        TextAlign align = TextAlign.Left,
        bool middle = true,
        bool monospace = false,
        bool wrap = false)
    {
        if (string.IsNullOrEmpty(text)) return;

        // A hex readout is a number, and proportional digits make it jitter as it changes.
        var family = monospace ? Metrics.MonoFamily : Metrics.FontFamily;
        var format = resources.TextFormat(family, size, bold, italic: false);

        format.Object.SetWordWrapping(wrap
            ? DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_WRAP
            : DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP);
        format.Object.SetTextAlignment(align switch
        {
            TextAlign.Center => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER,
            TextAlign.Right => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING,
            _ => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING,
        });
        format.Object.SetParagraphAlignment(middle
            ? DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER
            : DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_NEAR);

        ID2D1RenderTargetExtensions.DrawText(
            _target, text, format, AnnotationRenderer.ToRect(bounds), resources.Brush(color));
    }

    /// <summary>
    /// An icon glyph, centred in its box.
    ///
    /// <paramref name="size"/> is the glyph's em size, not the box: an icon font draws its glyphs to
    /// fill their em square, so the box is the tap target and this is the mark inside it.
    /// </summary>
    public void Icon(string glyph, Rect bounds, Rgba color, double size)
    {
        var format = resources.TextFormat(Icons.Family, (float)size, bold: false, italic: false);
        format.Object.SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER);
        format.Object.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);

        ID2D1RenderTargetExtensions.DrawText(
            _target, glyph, format, AnnotationRenderer.ToRect(bounds), resources.Brush(color));
    }

    // ============================  WIDGETS  ============================

    /// <summary>
    /// The interaction core every widget shares: track hot/active and report a click.
    ///
    /// A click is press-then-release *on the same widget* - pressing a button and dragging off it
    /// must not fire, which is the behaviour every real toolbar has and which you get for free from
    /// a framework but must state explicitly here.
    /// </summary>
    public bool Interact(int id, Rect bounds)
    {
        // A widget scrolled out of view is not clickable where it used to be.
        var inside = bounds.Contains(Pointer) && PointerVisible;
        if (inside && Active == 0) Hot = id;

        if (inside && _pointerPressedThisFrame) Active = id;

        var clicked = Active == id && _pointerReleasedThisFrame && inside;
        return clicked;
    }

    public bool IsHot(int id) => Hot == id;
    public bool IsActive(int id) => Active == id;

    /// <summary>A tool tile: an icon glyph on a rounded fill that reads selected, hot or idle.
    /// <paramref name="neutral"/> selects with the chrome's translucent fill rather than the accent -
    /// a nav destination is somewhere you are, not a mode you have armed.</summary>
    public bool Tile(
        int id, Rect bounds, bool selected, string glyph, double glyphSize,
        string? tooltip = null, Rgba? tint = null, bool neutral = false)
    {
        var clicked = Interact(id, bounds);

        var fill = selected
            ? neutral ? Theme.FillSelected : Theme.Accent
            : IsActive(id) ? Theme.FillPressed
            : IsHot(id) ? Theme.FillHover
            : default;

        if (fill.A > 0) FillRounded(bounds, Metrics.RadiusControl, fill);

        var foreground = selected
            ? neutral ? Theme.TextPrimary : Theme.TextOnAccent
            : IsHot(id) ? Theme.TextPrimary
            : Theme.TextSecondary;
        Icon(glyph, bounds, tint ?? foreground, glyphSize);

        if (tooltip is not null && IsHot(id)) Tooltip(bounds, tooltip);
        return clicked;
    }

    /// <summary>A colour swatch: a 16/26-scaled dot with a selection ring drawn outside it, so the
    /// control is a touch target larger than the dot itself - the old ColorSwatch's proportions.</summary>
    public bool Swatch(int id, Rect bounds, Rgba color, bool selected)
    {
        var clicked = Interact(id, bounds);

        var size = Math.Min(bounds.Width, bounds.Height);
        var center = bounds.Center;
        var dotRadius = (float)(size * 8 / 26);
        var ringRadius = (float)(size - 2) / 2;

        var ringOpacity = selected ? 255 : IsActive(id) ? 128 : IsHot(id) ? 90 : 0;
        if (ringOpacity > 0)
            StrokeCircle(center, ringRadius, Theme.TextPrimary.WithAlpha((byte)ringOpacity), 1.5f);

        FillCircle(center, dotRadius, color);
        StrokeCircle(center, dotRadius, Theme.StrokeStrong);

        return clicked;
    }

    /// <summary>A horizontal slider. Returns true while being dragged, with the new value.</summary>
    public bool Slider(int id, Rect bounds, double min, double max, ref double value)
    {
        Interact(id, bounds);

        var thickness = 6 * Scale;
        var radius = 9 * Scale;

        var track = new Rect(bounds.X, bounds.Center.Y - thickness / 2, bounds.Width, thickness);
        FillRounded(track, (float)(thickness / 2), Theme.StrokeDefault);

        var range = Math.Max(0.0001, max - min);
        var t = Math.Clamp((value - min) / range, 0, 1);

        var filled = new Rect(track.X, track.Y, track.Width * t, track.Height);
        if (filled.Width > 0) FillRounded(filled, (float)(thickness / 2), Theme.Accent);

        var knob = new Point(bounds.X + bounds.Width * t, bounds.Center.Y);
        FillCircle(knob, (float)radius, Theme.SurfaceRaised);
        StrokeCircle(knob, (float)radius,
            IsActive(id) || IsHot(id) ? Theme.Accent : Theme.StrokeStrong, (float)(2 * Scale));

        if (!IsActive(id)) return false;

        var dragged = Math.Clamp((Pointer.X - bounds.X) / Math.Max(1, bounds.Width), 0, 1);
        var updated = min + dragged * range;
        if (Math.Abs(updated - value) < 0.0001) return false;
        value = updated;
        return true;
    }

    /// <summary>A text button, optionally with a leading glyph and an accent (primary) treatment.
    /// Hover lifts the surface a step and strengthens the border; pressing lifts it further, so the
    /// button acknowledges both the pointer arriving and the click landing.</summary>
    public bool Button(
        int id, Rect bounds, string label,
        bool primary = false, bool enabled = true, bool toggled = false,
        string? glyph = null, double glyphSize = 14, double fontSize = Metrics.FontBody,
        bool accent = false)
    {
        // Interact first: it is what sets Hot/Active, so reading them before it would style the
        // button from the previous frame's state and drop the first frame of every hover.
        var clicked = enabled && Interact(id, bounds);

        Rgba fill, text, border;
        var bold = false;

        if (!enabled)
        {
            fill = Theme.SurfaceOverlay;
            border = Theme.StrokeDefault;
            text = Theme.TextTertiary;
        }
        else if (primary)
        {
            fill = IsActive(id) ? Theme.AccentPressed : IsHot(id) ? Theme.AccentHover : Theme.Accent;
            border = fill;
            text = Theme.TextOnAccent;
            bold = true;
        }
        else if (toggled)
        {
            fill = Theme.FillSelected;
            border = Theme.StrokeStrong;
            text = Theme.TextPrimary;
        }
        else
        {
            // The overlay surface is opaque, so the hover/press tint is composited onto it rather
            // than replacing it - otherwise the button would go translucent on hover.
            fill = IsActive(id) ? Over(Theme.SurfaceOverlay, Theme.FillPressed)
                : IsHot(id) ? Over(Theme.SurfaceOverlay, Theme.FillHover)
                : Theme.SurfaceOverlay;

            // A confirmed action tints its text and border rather than filling: a button that turns
            // solid on success reads as a mode it has entered.
            border = accent ? Theme.Accent
                : IsHot(id) || IsActive(id) ? Theme.StrokeStrong
                : Theme.StrokeDefault;
            text = accent ? Theme.Accent : Theme.TextPrimary;
        }

        var radius = (float)(Metrics.RadiusControl * (bounds.Height / 32));
        FillRounded(bounds, radius, fill);
        StrokeRounded(bounds, radius, border);

        if (glyph is null)
        {
            Text(label, bounds, text, (float)fontSize, bold, TextAlign.Center);
            return clicked;
        }

        // Glyph and label as one centred cluster. The label is measured, not estimated from its
        // character count: a guess leaves the pair visibly off-centre in the button.
        var gap = glyphSize * 0.55;
        var labelWidth = MeasureText(label, fontSize, bold);
        var content = glyphSize + gap + labelWidth;
        var x = bounds.X + (bounds.Width - content) / 2;

        Icon(glyph, new Rect(x, bounds.Y, glyphSize, bounds.Height), text, glyphSize);
        Text(label, new Rect(x + glyphSize + gap, bounds.Y, labelWidth + 2, bounds.Height),
            text, (float)fontSize, bold);

        return clicked;
    }

    /// <summary>Composites a translucent tint over an opaque base, so a hover fill can lift a surface
    /// rather than replace it.</summary>
    private static Rgba Over(Rgba baseColor, Rgba tint)
    {
        var a = tint.A / 255.0;
        return new Rgba(
            (byte)Math.Round(tint.R * a + baseColor.R * (1 - a)),
            (byte)Math.Round(tint.G * a + baseColor.G * (1 - a)),
            (byte)Math.Round(tint.B * a + baseColor.B * (1 - a)),
            baseColor.A);
    }

    /// <summary>The rendered width of a string, so a caller can centre or size against it.</summary>
    public double MeasureText(string text, double size, bool bold = false, bool monospace = false)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var family = monospace ? Metrics.MonoFamily : Metrics.FontFamily;
        var format = resources.TextFormat(family, (float)size, bold, italic: false);
        using var layout = resources.DWrite.CreateTextLayout(format, text);

        layout.Object.GetMetrics(out var metrics);
        return metrics.width;
    }

    /// <summary>A small square toggle, for B / I / U.</summary>
    public bool Toggle(int id, Rect bounds, string label, bool on, bool bold = false, bool italic = false)
    {
        var clicked = Interact(id, bounds);

        var fill = on ? Theme.FillSelected
            : IsActive(id) ? Theme.FillPressed
            : IsHot(id) ? Theme.FillHover
            : default;
        if (fill.A > 0) FillRounded(bounds, Metrics.RadiusControl, fill);

        var format = resources.TextFormat(Metrics.FontFamily, Metrics.FontBody, bold, italic);
        format.Object.SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER);
        format.Object.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
        ID2D1RenderTargetExtensions.DrawText(
            _target, label, format, AnnotationRenderer.ToRect(bounds),
            resources.Brush(on ? Theme.TextPrimary : Theme.TextSecondary));

        return clicked;
    }

    /// <summary>A thin vertical rule between toolbar groups.</summary>
    public void Separator(double x, double top, double height) =>
        FillRect(new Rect(x, top, 1, height), Theme.StrokeSubtle);

    /// <summary>A scroll indicator down the right edge of <paramref name="viewport"/>. Nothing is
    /// drawn when the content fits. Indicator only: the wheel scrolls, and a hit region here would
    /// sit over the rows underneath and steal their hover.</summary>
    public void Scrollbar(Rect viewport, double content, double scroll)
    {
        var overflow = content - viewport.Height;
        if (overflow <= 0.5 || viewport.Height <= 0) return;

        var width = 4 * Scale;

        // Floored, or a long list shrinks the thumb to something that indicates nothing.
        var minimum = Math.Min(24 * Scale, viewport.Height);
        var length = Math.Max(minimum, viewport.Height * (viewport.Height / content));

        var travel = viewport.Height - length;
        var progress = Math.Clamp(scroll / overflow, 0, 1);

        FillRounded(
            new Rect(viewport.Right - width - 3 * Scale, viewport.Y + travel * progress, width, length),
            (float)(width / 2),
            Theme.StrokeStrong);
    }

    /// <summary>The display scale, so tooltips are legible on a scaled monitor. Callers that draw
    /// chrome set it once per frame.</summary>
    public double Scale { get; set; } = 1;

    /// <summary>A label under the anchor, sized to its text rather than a character-count guess.</summary>
    private void Tooltip(Rect anchor, string text)
    {
        var font = Metrics.FontBody * Scale;
        var padding = 10 * Scale;
        var height = 26 * Scale;

        var width = MeasureText(text, font) + padding * 2;
        var x = anchor.Center.X - width / 2;
        var bounds = new Rect(x, anchor.Bottom + 6 * Scale, width, height);

        FillRounded(bounds, (float)(4 * Scale), Theme.SurfaceOverlay);
        StrokeRounded(bounds, (float)(4 * Scale), Theme.StrokeDefault);
        Text(text, bounds, Theme.TextPrimary, (float)font, align: TextAlign.Center);
    }

    private static D2D1_ROUNDED_RECT Rounded(Rect rect, float radius) => new()
    {
        rect = AnnotationRenderer.ToRect(rect),
        radiusX = radius,
        radiusY = radius,
    };

    private static D2D1_ELLIPSE Ellipse(Point center, float radius) => new()
    {
        point = AnnotationRenderer.ToPoint(center),
        radiusX = radius,
        radiusY = radius,
    };
}

public enum TextAlign { Left, Center, Right }

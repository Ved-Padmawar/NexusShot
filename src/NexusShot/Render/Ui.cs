using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>
/// An immediate-mode UI context: widgets are function calls, not objects.
///
/// The XAML build needed a class per control (ToolTile, ColorSwatch, HexColorPicker, HotkeyRecorder,
/// CursorInteractionGrid...) plus templates, plus theme dictionaries, plus binding, because a
/// retained tree needs an object to retain. Here a button is "draw a rect, return whether it was
/// clicked" - the widget owns no state, so there is nothing to keep in sync with the model, and no
/// RadioButton reserving a 20px indicator column that clips your content.
///
/// The only retained state is what genuinely persists between frames: what the pointer is over, and
/// what it is dragging. Both are identified by a caller-supplied id.
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
    }

    public void EndFrame()
    {
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

    /// <summary>Text in a box, with alignment. Returns nothing: chrome text is never hit-tested.</summary>
    public void Text(
        string text,
        Rect bounds,
        Rgba color,
        float size = Metrics.FontBody,
        bool bold = false,
        TextAlign align = TextAlign.Left,
        bool middle = true)
    {
        if (string.IsNullOrEmpty(text)) return;
        var format = resources.TextFormat(Metrics.FontFamily, size, bold, italic: false);
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
        var inside = bounds.Contains(Pointer);
        if (inside && Active == 0) Hot = id;

        if (inside && _pointerPressedThisFrame) Active = id;

        var clicked = Active == id && _pointerReleasedThisFrame && inside;
        return clicked;
    }

    public bool IsHot(int id) => Hot == id;
    public bool IsActive(int id) => Active == id;

    /// <summary>A tool tile: an icon glyph on a rounded fill that reads selected, hot or idle.</summary>
    public bool Tile(
        int id, Rect bounds, bool selected, string glyph, double glyphSize,
        string? tooltip = null, Rgba? tint = null)
    {
        var clicked = Interact(id, bounds);

        var fill = selected
            ? Theme.FillSelected
            : IsActive(id) ? Theme.FillPressed
            : IsHot(id) ? Theme.FillHover
            : default;

        if (fill.A > 0) FillRounded(bounds, Metrics.RadiusControl, fill);

        Icon(glyph, bounds, tint ?? (selected ? Theme.TextPrimary : Theme.TextSecondary), glyphSize);

        if (tooltip is not null && IsHot(id)) Tooltip(bounds, tooltip);
        return clicked;
    }

    /// <summary>A colour swatch. Mutual exclusion is the caller's, which is why the XAML build's
    /// RadioButton (and its 20px indicator column that clipped the dot) is not needed.</summary>
    public bool Swatch(int id, Rect bounds, Rgba color, bool selected)
    {
        var clicked = Interact(id, bounds);

        var size = Math.Min(bounds.Width, bounds.Height);
        var center = bounds.Center;
        var radius = (float)(size / 2 - 3);

        if (selected || IsHot(id))
        {
            StrokeCircle(center, radius + 3,
                selected ? Theme.Accent : Theme.StrokeStrong,
                selected ? 2f : 1f);
        }

        FillCircle(center, radius, color);

        // White needs an outline or it vanishes on a light surface.
        if (color.R > 235 && color.G > 235 && color.B > 235)
            StrokeCircle(center, radius, Theme.StrokeStrong);

        return clicked;
    }

    /// <summary>A horizontal slider. Returns true while being dragged, with the new value.</summary>
    public bool Slider(int id, Rect bounds, double min, double max, ref double value)
    {
        Interact(id, bounds);

        var track = new Rect(bounds.X, bounds.Center.Y - 2, bounds.Width, 4);
        FillRounded(track, 2, Theme.StrokeDefault);

        var range = Math.Max(0.0001, max - min);
        var t = Math.Clamp((value - min) / range, 0, 1);

        var filled = new Rect(track.X, track.Y, track.Width * t, track.Height);
        if (filled.Width > 0) FillRounded(filled, 2, Theme.Accent);

        var knobX = bounds.X + bounds.Width * t;
        var knob = new Point(knobX, bounds.Center.Y);
        FillCircle(knob, 7, Theme.SurfaceRaised);
        StrokeCircle(knob, 7, IsActive(id) || IsHot(id) ? Theme.Accent : Theme.StrokeStrong, 1.5f);

        if (!IsActive(id)) return false;

        var dragged = Math.Clamp((Pointer.X - bounds.X) / Math.Max(1, bounds.Width), 0, 1);
        var updated = min + dragged * range;
        if (Math.Abs(updated - value) < 0.0001) return false;
        value = updated;
        return true;
    }

    /// <summary>A text button, optionally with a leading glyph and an accent (primary) treatment.</summary>
    public bool Button(
        int id, Rect bounds, string label,
        bool primary = false, bool enabled = true,
        string? glyph = null, double glyphSize = 14, double fontSize = Metrics.FontBody)
    {
        Rgba fill, text;

        if (!enabled)
        {
            fill = Theme.FillPressed;
            text = Theme.TextTertiary;
        }
        else if (primary)
        {
            fill = IsActive(id) ? Theme.AccentPressed : IsHot(id) ? Theme.AccentHover : Theme.Accent;
            text = Theme.TextOnAccent;
        }
        else
        {
            fill = IsActive(id) ? Theme.FillPressed : IsHot(id) ? Theme.FillHover : Theme.SurfaceOverlay;
            text = Theme.TextPrimary;
        }

        var clicked = enabled && Interact(id, bounds);

        FillRounded(bounds, Metrics.RadiusControl, fill);
        if (!primary) StrokeRounded(bounds, Metrics.RadiusControl, Theme.StrokeSubtle);

        if (glyph is null)
        {
            Text(label, bounds, text, (float)fontSize, align: TextAlign.Center);
            return clicked;
        }

        // Glyph and label as one centred cluster: the glyph sits left of the text, and the pair is
        // centred together rather than each being centred in its own half.
        var gap = glyphSize * 0.45;
        var labelWidth = label.Length * fontSize * 0.58;
        var content = glyphSize + gap + labelWidth;
        var x = bounds.X + (bounds.Width - content) / 2;

        Icon(glyph, new Rect(x, bounds.Y, glyphSize, bounds.Height), text, glyphSize);
        Text(label, new Rect(x + glyphSize + gap, bounds.Y, labelWidth, bounds.Height),
            text, (float)fontSize);

        return clicked;
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

    /// <summary>A label above the pointer. Drawn last by the caller so it is never occluded.</summary>
    private void Tooltip(Rect anchor, string text)
    {
        var width = Math.Max(40, text.Length * 7 + 16);
        var bounds = new Rect(anchor.Center.X - width / 2, anchor.Bottom + 6, width, 24);
        FillRounded(bounds, 4, Theme.SurfaceOverlay);
        StrokeRounded(bounds, 4, Theme.StrokeDefault);
        Text(text, bounds, Theme.TextSecondary, Metrics.FontCaption, align: TextAlign.Center);
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

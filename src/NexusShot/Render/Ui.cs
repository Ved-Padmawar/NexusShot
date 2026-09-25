using NexusShot.Core;

namespace NexusShot.Render;

public enum Weight { Regular = 400, Medium = 500, Semibold = 600, Bold = 700 }

/// <summary>Which family a run of text is set in: running text, headings, or numbers and keycaps.</summary>
public enum Face { Text, Display, Mono }

public enum TextAlign { Left, Center, Right }

public enum ButtonStyle { Ghost, Primary, Outline, Secondary, Danger, Destructive }

/// <summary>Where a tooltip sits relative to its anchor: below for bars, to the right for a rail.</summary>
public enum TipSide { Below, Right }

/// <summary>What a text field reported this frame.</summary>
public readonly record struct FieldResult(bool Changed, string Text, int Step);

/// <summary>
/// An immediate-mode UI context: widgets are function calls, not objects, and every control in the
/// app is drawn by one of them - so a button looks and behaves the same in every window.
///
/// The only retained state is what genuinely persists between frames, keyed by a caller-supplied id:
/// what the pointer is over and dragging, which field has the keyboard, and the value each animation
/// is moving from. Nothing here shadows the model a widget displays.
/// </summary>
public sealed class Ui(D2DResources resources)
{
    public D2DResources Resources => resources;

    public Theme Theme { get; set; } = Theme.Dark;

    /// <summary>The display scale: 1 design unit is <see cref="Scale"/> physical pixels. Set once per
    /// frame by the window.</summary>
    public double Scale { get; set; } = 1;

    private double S(double units) => units * Scale;

    /// <summary>Set once per frame, before any widget is called.</summary>
    public Point Pointer { get; private set; }
    public bool PointerDown { get; private set; }

    /// <summary>True on the frame the pointer went down. Popups use it to dismiss on an outside
    /// click, which they must do before any widget under them gets the event.</summary>
    public bool PointerPressed { get; private set; }
    private bool _pointerReleased;

    /// <summary>Where the pointer went down, for drags measured from their start.</summary>
    public Point PressPoint { get; private set; }

    /// <summary>True once any widget reported a click this frame. The frame handling a click has
    /// already drawn the pre-click state, so its window must schedule one more paint.</summary>
    public bool ClickedThisFrame { get; private set; }

    /// <summary>True while something is mid-motion or waiting to appear. The window keeps painting
    /// while it is set, and stops the moment everything has settled.</summary>
    public bool Animating { get; private set; }

    /// <summary>Milliseconds, sampled once per frame so every animation in it agrees on the time.</summary>
    public long Now { get; private set; }

    public int Hot { get; private set; }
    public int Active { get; private set; }

    /// <summary>True when any widget wants the pointer, so the canvas ignores it.</summary>
    public bool WantsPointer => Hot != 0 || Active != 0;

    private IComObject<ID2D1RenderTarget> _target = null!;
    private D2D_SIZE_F? _area;

    /// <summary>Sends the rest of the frame's drawing to another target. A window drawn as
    /// composition layers fills its surfaces one after another inside one frame, so hover, clicks
    /// and animation stay a single frame's worth.</summary>
    public void Retarget(IComObject<ID2D1RenderTarget> target) => _target = target;

    /// <summary>The current target as a device context, for uploading a bitmap mid-frame.</summary>
    public IComObject<ID2D1DeviceContext>? DeviceContext() => _target.AsDeviceContext();

    /// <param name="area">The window's size, for a target that is not the window - a composition
    /// surface reports the size of the atlas it lives in.</param>
    public void BeginFrame(IComObject<ID2D1RenderTarget> target, Point pointer, bool pointerDown, D2D_SIZE_F? area = null)
    {
        _target = target;
        _area = area;
        PointerPressed = pointerDown && !PointerDown;
        _pointerReleased = !pointerDown && PointerDown;
        if (PointerPressed) PressPoint = pointer;
        Pointer = pointer;
        PointerDown = pointerDown;
        Hot = 0;
        ClickedThisFrame = false;
        Animating = false;
        Blinking = false;
        Now = Environment.TickCount64;
        _tip = null;
        _clips.Clear();

        // A press outside the focused field takes the keyboard back, as it would from a text box.
        if (PointerPressed && _focus != 0 && !_focusBounds.Contains(pointer)) Blur();
    }

    /// <summary>Ends the frame: the tooltip paints last, over everything, and any clip a caller left
    /// open is unwound - an unbalanced stack faults the device with D2DERR_WRONG_STATE.</summary>
    public void EndFrame()
    {
        while (_clips.Count > 0) PopClip();
        while (_layerMasks.Count > 0) PopRoundedLayer();

        if (Hot != _lastHot)
        {
            _lastHot = Hot;
            _hotSince = Now;
        }
        DrawTip();

        if (_pointerReleased) Active = 0;
        _keys.Clear();
    }

    // ============================  PRIMITIVES  ============================

    public void FillRect(Rect rect, Rgba color) =>
        _target.FillRectangle(AnnotationRenderer.ToRect(rect), resources.Brush(color));

    /// <summary>A fill for a colour that will not recur: the brush is reused, not cached.</summary>
    public void FillRectScratch(Rect rect, Rgba color) =>
        _target.FillRectangle(AnnotationRenderer.ToRect(rect), resources.ScratchBrush(color));

    /// <summary>Fills a rectangle with a two-stop gradient running between two of its points.</summary>
    public void FillRectGradient(Rect rect, Rgba from, Rgba to, Point start, Point end) =>
        _target.FillRectangle(AnnotationRenderer.ToRect(rect), resources.GradientBrush(from, to, start, end));

    /// <summary>A rounded rectangle filled with a two-stop gradient, for rails that must keep their
    /// round ends.</summary>
    public void FillRoundedGradient(Rect rect, float radius, Rgba from, Rgba to, Point start, Point end) =>
        _target.FillRoundedRectangle(Rounded(rect, radius), resources.GradientBrush(from, to, start, end));

    /// <summary>A checkerboard under a rounded rectangle, for showing a colour's transparency.</summary>
    public void FillChecker(Rect rect, float radius) =>
        _target.FillRoundedRectangle(Rounded(rect, radius), resources.Checker((float)S(4)));

    /// <summary>Fills with the stage's dot grid, printed on <paramref name="pitch"/>-pixel centres.</summary>
    public void FillDots(Rect rect, Rgba dot, float pitch) =>
        _target.FillRectangle(AnnotationRenderer.ToRect(rect), resources.DotGrid(dot, pitch));

    public void FillRounded(Rect rect, float radius, Rgba color) =>
        _target.FillRoundedRectangle(Rounded(rect, radius), resources.Brush(color));

    /// <summary>The stroke sits inside the rectangle - a CSS inset border - so a bordered control is
    /// exactly as big as an unbordered one.</summary>
    public void StrokeRounded(Rect rect, float radius, Rgba color, float thickness = 1) =>
        _target.DrawRoundedRectangle(
            Rounded(rect.Deflate(thickness / 2), Math.Max(0, radius - thickness / 2)), resources.Brush(color), thickness);

    public void FillCircle(Point center, float radius, Rgba color) =>
        _target.FillEllipse(Ellipse(center, radius), resources.Brush(color));

    public void StrokeCircle(Point center, float radius, Rgba color, float thickness = 1) =>
        _target.DrawEllipse(Ellipse(center, radius), resources.Brush(color), thickness);

    public void Line(Point a, Point b, Rgba color, float thickness = 1) =>
        _target.DrawLine(
            AnnotationRenderer.ToPoint(a), AnnotationRenderer.ToPoint(b),
            resources.Brush(color), thickness, resources.RoundStroke);

    /// <summary>
    /// A soft drop shadow: a solid core, gradient edges and radial corners, falling from the colour
    /// half the blur inside the shape to nothing half outside it, as a blur spreads both ways.
    ///
    /// Gradients rather than stacked translucent rings: a ring's share of a faint shadow rounds to
    /// one 8-bit step or none, so rings quantise into visible bands. A gradient is one smooth ramp,
    /// and needs no effect graph or intermediate surface - which a window that resizes every frame
    /// cannot afford to keep reallocating. Edges land on whole pixels, or the pieces seam.
    /// </summary>
    public void Shadow(Rect rect, float radius, double blur, double offsetY, Rgba color)
    {
        var half = Math.Round(blur / 2);
        var shifted = new Rect(rect.X, rect.Y + offsetY, rect.Width, rect.Height);
        var outer = Rect.FromEdges(Math.Round(shifted.X - half), Math.Round(shifted.Y - half),
            Math.Round(shifted.Right + half), Math.Round(shifted.Bottom + half));
        var core = outer.Deflate(half * 2);
        var clear = color.WithAlpha(0);
        var reach = outer.X < core.X ? core.X - outer.X : 1;

        if (!core.IsEmpty) FillRect(core, color);

        FillRectGradient(new Rect(core.X, outer.Y, core.Width, core.Y - outer.Y), color, clear,
            new Point(core.X, core.Y), new Point(core.X, outer.Y));
        FillRectGradient(new Rect(core.X, core.Bottom, core.Width, outer.Bottom - core.Bottom), color, clear,
            new Point(core.X, core.Bottom), new Point(core.X, outer.Bottom));
        FillRectGradient(new Rect(outer.X, core.Y, core.X - outer.X, core.Height), color, clear,
            new Point(core.X, core.Y), new Point(outer.X, core.Y));
        FillRectGradient(new Rect(core.Right, core.Y, outer.Right - core.Right, core.Height), color, clear,
            new Point(core.Right, core.Y), new Point(outer.Right, core.Y));

        Corner(new Rect(outer.X, outer.Y, reach, reach), new Point(core.X, core.Y));
        Corner(new Rect(core.Right, outer.Y, reach, reach), new Point(core.Right, core.Y));
        Corner(new Rect(outer.X, core.Bottom, reach, reach), new Point(core.X, core.Bottom));
        Corner(new Rect(core.Right, core.Bottom, reach, reach), new Point(core.Right, core.Bottom));

        void Corner(Rect square, Point centre) =>
            _target.FillRectangle(AnnotationRenderer.ToRect(square), resources.RadialBrush(color, clear, centre, reach));
    }

    /// <summary>The two-layer float shadow every pill, popover and sheet shares.</summary>
    public void FloatShadow(Rect rect, float radius)
    {
        Shadow(rect, radius, S(22), S(10), Theme.Shadow);
        Shadow(rect, radius, S(5), S(2), Theme.Shadow.WithAlpha((byte)(Theme.Shadow.A / 2)));
    }

    /// <summary>
    /// Confines drawing to a rectangle. The pointer is clipped too: a widget scrolled out of view must
    /// not still be clickable where it used to be.
    /// </summary>
    public void PushClip(Rect bounds)
    {
        _clips.Push(bounds);
        _target.Object.PushAxisAlignedClip(
            AnnotationRenderer.ToRect(bounds), D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
    }

    public void PopClip()
    {
        if (_clips.Count == 0) return;
        _clips.Pop();
        _target.Object.PopAxisAlignedClip();
    }

    private readonly Stack<Rect> _clips = new();

    /// <summary>True when the pointer is inside every active clip. Walked by hand: this runs per widget
    /// per frame, and the LINQ form allocates.</summary>
    private bool PointerVisible
    {
        get
        {
            foreach (var clip in _clips) if (!clip.Contains(Pointer)) return false;
            return true;
        }
    }

    /// <summary>A bitmap drawn into <paramref name="destination"/>, clipped to a rounded
    /// <paramref name="bounds"/>: the rounded rectangle is filled with the bitmap as its brush. A layer
    /// per tile cost the software target an offscreen surface and a mask pass on every frame.</summary>
    public void DrawBitmapRounded(IComObject<ID2D1Bitmap> bitmap, Rect bounds, float radius, Rect destination)
    {
        using var brush = resources.ImageBrush(bitmap);
        var size = bitmap.Object.GetSize();
        brush.Object.SetTransform(
            D2D_MATRIX_3X2_F.Scale((float)(destination.Width / size.width), (float)(destination.Height / size.height))
            * D2D_MATRIX_3X2_F.Translation((float)destination.X, (float)destination.Y));
        _target.Object.FillRoundedRectangle(Rounded(bounds, radius), brush.Object);
    }

    /// <summary>
    /// Clips everything drawn until <see cref="PopRoundedLayer"/> to a rounded rectangle, in the
    /// target's current transform - so the editor can round the capture and everything painted on it
    /// in image space. A layer rather than per-draw geometry, because an annotation is many draws.
    /// </summary>
    public void PushRoundedLayer(Rect bounds, float radius)
    {
        using var context = _target.AsDeviceContext();
        if (context is null) return;

        using var mask = resources.Factory.CreateRoundedRectangleGeometry(Rounded(bounds, radius));

        // An AddRef'd raw pointer, as the AOT-generated layer struct wants; released after the pop.
        var maskPointer = ComObject.GetOrCreateComInstance(mask.Object);
        context.PushLayer(new D2D1_LAYER_PARAMETERS1
        {
            contentBounds = AnnotationRenderer.ToRect(bounds),
            geometricMask = maskPointer,
            maskAntialiasMode = D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,
            maskTransform = D2D_MATRIX_3X2_F.Identity(),
            opacity = 1,
            layerOptions = D2D1_LAYER_OPTIONS1.D2D1_LAYER_OPTIONS1_NONE,
        });
        _layerMasks.Push(maskPointer);
    }

    public void PopRoundedLayer()
    {
        if (_layerMasks.Count == 0) return;
        using var context = _target.AsDeviceContext();
        context?.PopLayer();
        var maskPointer = _layerMasks.Pop();
        if (maskPointer != 0) System.Runtime.InteropServices.Marshal.Release(maskPointer);
    }

    private readonly Stack<nint> _layerMasks = new();

    // ============================  TEXT & ICONS  ============================

    public string Family(Face face) => face switch
    {
        Face.Display => resources.Family(Metrics.DisplayFamily, Metrics.FontFallback),
        Face.Mono => resources.Family(Metrics.MonoFamily, Metrics.MonoFallback),
        _ => resources.Family(Metrics.FontFamily, Metrics.FontFallback),
    };

    /// <summary>Text in a box, with alignment. Returns nothing: chrome text is never hit-tested.</summary>
    public void Text(
        string text, Rect bounds, Rgba color, double size = Metrics.FontMd,
        Weight weight = Weight.Regular, TextAlign align = TextAlign.Left, bool middle = true,
        Face face = Face.Text, bool wrap = false)
    {
        if (string.IsNullOrEmpty(text)) return;

        var format = resources.TextFormat(
            Family(face), (float)size, (DWRITE_FONT_WEIGHT)weight, italic: false,
            alignment: align switch
            {
                TextAlign.Center => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER,
                TextAlign.Right => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING,
                _ => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING,
            },
            paragraphAlignment: middle
                ? DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER
                : DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_NEAR,
            wordWrapping: wrap
                ? DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_WRAP
                : DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP);

        ID2D1RenderTargetExtensions.DrawText(
            _target, text, format, AnnotationRenderer.ToRect(bounds), resources.Brush(color),
            D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_CLIP);
    }

    /// <summary>Text that is cut to fit with an ellipsis rather than clipped mid-glyph.</summary>
    public void TextFit(
        string text, Rect bounds, Rgba color, double size = Metrics.FontMd,
        Weight weight = Weight.Regular, Face face = Face.Text, TextAlign align = TextAlign.Left) =>
        Text(Ellipsize(text, bounds.Width, size, weight, face), bounds, color, size, weight, align, face: face);

    /// <summary>The longest prefix of <paramref name="text"/> that fits with an ellipsis. Measured,
    /// not estimated from a character count, so the cut lands at the edge.</summary>
    public string Ellipsize(string text, double width, double size, Weight weight = Weight.Regular, Face face = Face.Text)
    {
        if (MeasureText(text, size, weight, face) <= width) return text;

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (MeasureText(text[..mid] + "…", size, weight, face) <= width) low = mid;
            else high = mid - 1;
        }
        return low == 0 ? "…" : text[..low].TrimEnd() + "…";
    }

    public double MeasureText(string text, double size, Weight weight = Weight.Regular, Face face = Face.Text) =>
        resources.MeasureText(text, Family(face), (float)size, (DWRITE_FONT_WEIGHT)weight);

    /// <summary>An icon centred in <paramref name="bounds"/> at <paramref name="size"/> pixels across.
    /// The geometry is built once on the icon's own grid and scaled here by a transform, so a stroke
    /// keeps the grid's proportion at every size, exactly as an SVG does.</summary>
    public void Icon(Icon icon, Rect bounds, Rgba color, double size)
    {
        var (stroke, fill) = resources.IconGeometry(icon);
        var k = (float)(size / icon.Grid);
        var x = (float)(bounds.Center.X - size / 2);
        var y = (float)(bounds.Center.Y - size / 2);

        _target.Object.GetTransform(out var previous);
        _target.Object.SetTransform(
            D2D_MATRIX_3X2_F.Scale(k, k) * D2D_MATRIX_3X2_F.Translation(x, y) * previous);

        if (fill is not null)
            _target.Object.FillGeometry(fill.Object,
                resources.Brush(color.WithAlpha((byte)(color.A * icon.FillOpacity))).Object, null!);
        _target.Object.DrawGeometry(stroke.Object, resources.Brush(color).Object,
            (float)icon.StrokeWidth, resources.RoundStroke.Object);

        _target.Object.SetTransform(previous);
    }

    // ============================  INTERACTION  ============================

    /// <summary>
    /// The interaction core every widget shares: track hot/active and report a click. A click is
    /// press-then-release on the same widget - pressing and dragging off must not fire.
    /// </summary>
    public bool Interact(int id, Rect bounds)
    {
        if (Inert) return false;
        var inside = bounds.Contains(Pointer) && PointerVisible;
        if (inside && Active == 0) Hot = id;
        if (inside && PointerPressed) Active = id;

        var clicked = Active == id && _pointerReleased && inside;
        if (clicked) ClickedThisFrame = true;
        return clicked;
    }

    /// <summary>
    /// A stable widget id from a name. FNV-1a, written out rather than string.GetHashCode, which is
    /// randomised per process. Zero is reserved for "no widget", so a name hashing to it is nudged.
    /// </summary>
    public static int Id(string name)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var character in name) hash = (hash ^ character) * 16777619u;
            var id = (int)hash;
            return id == 0 ? 1 : id;
        }
    }

    /// <summary>A child id under <paramref name="owner"/>, for widgets that repeat over runtime data.
    /// Mixed rather than added: `owner + index` walks into the next owner's ids.</summary>
    public static int Id(int owner, int index)
    {
        unchecked
        {
            var hash = ((2166136261u ^ (uint)owner) * 16777619u ^ (uint)index) * 16777619u;
            var id = (int)hash;
            return id == 0 ? 1 : id;
        }
    }

    /// <summary>While set, widgets draw but ignore the pointer - for content under a modal sheet.</summary>
    public bool Inert { get; set; }

    public bool IsHot(int id) => Hot == id;
    public bool IsActive(int id) => Active == id;

    // ============================  MOTION  ============================

    private readonly record struct Motion(double From, double To, long Start, double Duration);
    private readonly Dictionary<int, Motion> _motions = [];

    /// <summary>
    /// Eases a value toward <paramref name="target"/> and returns where it is this frame. The first
    /// request lands immediately - nothing animates in from a value it never had - and a new target
    /// mid-flight starts from wherever the value currently is, so a quick double change never jumps.
    /// </summary>
    public double Animate(int id, double target, double duration = Metrics.Motion)
    {
        if (!_motions.TryGetValue(id, out var motion))
        {
            _motions[id] = new Motion(target, target, Now, 0);
            return target;
        }

        if (motion.To != target)
        {
            motion = new Motion(Evaluate(motion), target, Now, duration);
            _motions[id] = motion;
        }

        if (Now < motion.Start + motion.Duration) Animating = true;
        return Evaluate(motion);
    }

    /// <summary>The ease-out every motion shares: fast off the mark, settling gently.</summary>
    private double Evaluate(Motion motion)
    {
        if (motion.Duration <= 0) return motion.To;
        var t = Math.Clamp((Now - motion.Start) / motion.Duration, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3.2);
        return motion.From + (motion.To - motion.From) * eased;
    }

    /// <summary>Asks for another frame, for a caller animating something of its own.</summary>
    public void KeepAnimating() => Animating = true;

    // ============================  WIDGETS  ============================

    /// <summary>
    /// An icon button: a square with an icon that reads idle, hovered, pressed, on (an armed mode, in
    /// the accent) or soft (a quieter selected state). Every square button in the app is this one.
    /// </summary>
    public bool IconButton(
        int id, Rect bounds, Icon icon, string? tooltip = null, string? keycap = null,
        bool on = false, bool soft = false, bool enabled = true, double iconSize = 18,
        TipSide side = TipSide.Below, bool destructive = false)
    {
        var clicked = enabled && Interact(id, bounds);
        var hot = enabled && (IsHot(id) || IsActive(id));

        var fill = on ? Theme.Accent
            : soft ? Theme.AccentSoft
            : destructive && hot ? Theme.Danger.WithAlpha(40)
            : IsActive(id) && enabled ? Theme.SurfacePressed
            : hot ? Theme.SurfaceHover
            : default;
        if (fill.A > 0) FillRounded(bounds, (float)S(Metrics.RadiusSm), fill);

        var color = on ? Theme.TextOnAccent
            : soft ? Theme.AccentText
            : destructive && hot ? Theme.Danger
            : hot ? Theme.TextPrimary
            : Theme.TextSecondary;
        if (!enabled) color = color.WithAlpha((byte)(color.A * 0.35));
        Icon(icon, bounds, color, S(iconSize));

        if (tooltip is not null && enabled) Tip(id, bounds, tooltip, keycap, side);
        return clicked;
    }

    /// <summary>The resting fill of a button over a capture: dark in both themes, because the pixels
    /// beneath have no theme.</summary>
    public static readonly Rgba OverlayRest = new(20, 20, 24, 230);
    public static readonly Rgba OverlayBorder = new(255, 255, 255, 38);

    /// <summary>
    /// A button over captured pixels - the quick-access card's actions and the library tiles'. A faint
    /// wash disappears against a bright capture, so hover takes the accent; a destructive action
    /// (close, delete) turns red instead. Every such button in the app is this one.
    /// </summary>
    public bool OverlayButton(int id, Rect bounds, Icon icon, double iconSize, string? tooltip = null,
        bool on = false, bool destructive = false, bool round = false)
    {
        var clicked = Interact(id, bounds);
        var hot = IsHot(id) || IsActive(id);

        var fill = destructive && hot ? IsActive(id) ? Theme.Danger.Mix(Rgba.Black, 0.15) : Theme.Danger
            : IsActive(id) ? Theme.AccentPressed
            : hot || on ? Theme.Accent
            : OverlayRest;
        var ink = destructive && hot ? Rgba.White : hot || on ? Theme.TextOnAccent : Rgba.White;

        if (round)
        {
            var radius = (float)(bounds.Width / 2);
            FillCircle(bounds.Center, radius, fill);
            StrokeCircle(bounds.Center, radius - 0.5f, OverlayBorder);
        }
        else
        {
            var radius = (float)S(Metrics.RadiusSm);
            FillRounded(bounds, radius, fill);
            StrokeRounded(bounds, radius, OverlayBorder);
        }
        Icon(icon, bounds, ink, iconSize);

        if (tooltip is not null) Tip(id, bounds, tooltip);
        return clicked;
    }

    /// <summary>The width a button needs for its content, so buttons hug their labels.</summary>
    public double ButtonWidth(string label, Icon? icon = null, string? keycap = null, bool small = false)
    {
        var font = S(small ? Metrics.FontSm : Metrics.FontMd);
        var gap = S(8);
        var width = MeasureText(label, font, Weight.Semibold);
        if (icon is not null) width += S(small ? 15 : 16) + gap;
        if (keycap is not null) width += KeycapWidth(keycap) + gap;
        return Math.Ceiling(width + S(small ? 20 : 24));
    }

    /// <summary>A text button: ghost (transparent until hovered), primary (accent), outline, secondary
    /// (a raised surface, for a button that must read on a busy backdrop), danger (ghost that turns red
    /// on approach), or destructive (outline at rest, tinted red on approach - for the action that
    /// deletes). An optional leading icon and trailing keycap.</summary>
    public bool Button(
        int id, Rect bounds, string label, ButtonStyle style = ButtonStyle.Ghost,
        Icon? icon = null, string? keycap = null, bool small = false, bool enabled = true,
        string? tooltip = null)
    {
        var clicked = enabled && Interact(id, bounds);
        var hot = enabled && IsHot(id);
        var pressed = enabled && IsActive(id);

        Rgba fill, text, border = default;
        switch (style)
        {
            case ButtonStyle.Primary:
                fill = pressed ? Theme.AccentPressed : hot ? Theme.AccentHover : Theme.Accent;
                text = Theme.TextOnAccent;
                break;
            case ButtonStyle.Outline:
                fill = pressed ? Theme.SurfacePressed : hot ? Theme.SurfaceHover : default;
                border = hot || pressed ? Theme.StrokeStrong : Theme.StrokeDefault;
                text = Theme.TextPrimary;
                break;
            case ButtonStyle.Danger:
                fill = hot || pressed ? Theme.Danger.WithAlpha(40) : default;
                text = hot || pressed ? Theme.Danger : Theme.TextPrimary;
                break;
            case ButtonStyle.Secondary:
                fill = pressed ? Theme.SurfacePressed : hot ? Theme.SurfaceHover : Theme.SurfaceRaised;
                border = hot || pressed ? Theme.StrokeStrong : Theme.StrokeDefault;
                text = Theme.TextPrimary;
                break;
            case ButtonStyle.Destructive:
                fill = pressed ? Theme.Danger.WithAlpha(64) : hot ? Theme.Danger.WithAlpha(40) : default;
                border = hot || pressed ? Theme.Danger.WithAlpha(110) : Theme.StrokeDefault;
                text = hot || pressed ? Theme.Danger : Theme.TextPrimary;
                break;
            default:
                fill = pressed ? Theme.SurfacePressed : hot ? Theme.SurfaceHover : default;
                text = Theme.TextPrimary;
                break;
        }
        if (!enabled)
        {
            text = text.WithAlpha((byte)(text.A * 0.4));
            fill = fill.WithAlpha((byte)(fill.A * 0.4));
        }

        var radius = (float)S(Metrics.RadiusSm);
        if (fill.A > 0) FillRounded(bounds, radius, fill);
        if (border.A > 0) StrokeRounded(bounds, radius, border);

        var font = S(small ? Metrics.FontSm : Metrics.FontMd);
        var iconSize = S(small ? 15 : 16);
        var gap = S(8);

        // The content as one centred cluster, measured, so a wide button keeps it centred.
        var labelWidth = MeasureText(label, font, Weight.Semibold);
        var content = labelWidth
            + (icon is null ? 0 : iconSize + gap)
            + (keycap is null ? 0 : KeycapWidth(keycap) + gap);
        var x = bounds.X + (bounds.Width - content) / 2;

        if (icon is not null)
        {
            Icon(icon, new Rect(x, bounds.Y, iconSize, bounds.Height), text, iconSize);
            x += iconSize + gap;
        }

        Text(label, new Rect(x, bounds.Y, labelWidth + 1, bounds.Height), text, font, Weight.Semibold);
        x += labelWidth + gap;

        if (keycap is not null)
            Keycap(keycap, new Point(x, bounds.Center.Y), onAccent: style == ButtonStyle.Primary);

        if (tooltip is not null && enabled) Tip(id, bounds, tooltip, null, TipSide.Below);
        return clicked;
    }

    /// <summary>A confirmation pill centred on <paramref name="centerX"/> above <paramref name="bottom"/>:
    /// a raised surface of the theme with the accent's tick, rising into place as
    /// <paramref name="shown"/> reaches 1.</summary>
    public void Toast(string message, double centerX, double bottom, double shown)
    {
        Rgba Faded(Rgba color) => color.WithAlpha((byte)(color.A * shown));
        var font = S(Metrics.FontMd);
        var width = MeasureText(message, font, Weight.Semibold) + S(10 + 22 + 10 + 14);
        var pill = new Rect(centerX - width / 2, bottom - S(38) + S(10) * (1 - shown), width, S(38));
        var radius = (float)(pill.Height / 2);

        Shadow(pill, radius, S(20), S(8), Faded(Theme.Shadow));
        FillRounded(pill, radius, Faded(Theme.SurfaceRaised));
        StrokeRounded(pill, radius, Faded(Theme.StrokeStrong));
        var dot = new Point(pill.X + S(21), pill.Center.Y);
        FillCircle(dot, (float)S(11), Faded(Theme.Accent));
        Icon(Icons.Tick, new Rect(dot.X - S(8), dot.Y - S(8), S(16), S(16)), Faded(Theme.TextOnAccent), S(15));
        Text(message, new Rect(pill.X + S(42), pill.Y, width - S(52), pill.Height), Faded(Theme.TextPrimary),
            font, Weight.Semibold);
    }

    /// <summary>A determinate progress bar: the track, and the accent filled to <paramref name="fraction"/>.</summary>
    public void Progress(Rect bounds, double fraction)
    {
        var radius = (float)(bounds.Height / 2);
        FillRounded(bounds, radius, Theme.SurfaceHover);
        if (fraction <= 0) return;
        FillRounded(bounds with { Width = Math.Max(bounds.Height, bounds.Width * Math.Min(1, fraction)) }, radius, Theme.Accent);
    }

    public double KeycapWidth(string text) => Math.Ceiling(MeasureText(text, S(10.5), face: Face.Mono) + S(10));

    /// <summary>A keyboard shortcut, set in the mono face in a hairline box, vertically centred on
    /// <paramref name="leftCentre"/>'s Y. On an accent fill it takes the ink's tones.</summary>
    public void Keycap(string text, Point leftCentre, bool onAccent = false)
    {
        var box = new Rect(leftCentre.X, leftCentre.Y - S(9), KeycapWidth(text), S(18));
        var color = onAccent ? Theme.TextOnAccent.WithAlpha(178) : Theme.TextTertiary;
        var border = onAccent ? Theme.TextOnAccent.WithAlpha(64) : Theme.StrokeDefault;
        StrokeRounded(box, (float)S(Metrics.RadiusXs), border);
        Text(text, box, color, S(10.5), face: Face.Mono, align: TextAlign.Center);
    }

    /// <summary>The floating container every piece of on-canvas chrome sits in: a raised surface, a
    /// soft shadow and a hairline.</summary>
    public void Pill(Rect bounds, double radius = Metrics.RadiusLg)
    {
        var r = (float)S(radius);
        FloatShadow(bounds, r);
        FillRounded(bounds, r, Theme.SurfaceRaised);
        StrokeRounded(bounds, r, Theme.StrokeDefault);
    }

    /// <summary>A hairline between groups: vertical in a horizontal bar, horizontal in a rail.</summary>
    public void Separator(Point centre, bool vertical = true)
    {
        var length = S(18);
        FillRect(vertical
            ? new Rect(centre.X, centre.Y - length / 2, 1, length)
            : new Rect(centre.X - length / 2, centre.Y, length, 1), Theme.StrokeDefault);
    }

    /// <summary>The width of a segmented control's options: each sized to its label.</summary>
    public double SegmentedWidth(IReadOnlyList<string> options) =>
        options.Sum(option => MeasureText(option, S(Metrics.FontSm), Weight.Semibold) + S(24)) + S(6);

    /// <summary>
    /// A segmented control. The selection is a raised thumb that slides between options rather than
    /// each option lighting up in place, so a change reads as movement. Returns the clicked option.
    /// </summary>
    public int? Segmented(int id, Rect bounds, IReadOnlyList<string> options, int selected)
    {
        var radius = (float)S(Metrics.RadiusMd);
        FillRounded(bounds, radius, Theme.SurfacePane);
        StrokeRounded(bounds, radius, Theme.StrokeSubtle);

        var inner = bounds.Deflate(S(3));
        var widths = new double[options.Count];
        var natural = 0.0;
        for (var i = 0; i < options.Count; i++)
            natural += widths[i] = MeasureText(options[i], S(Metrics.FontSm), Weight.Semibold) + S(24);

        // Options share any width the caller gave beyond their natural size.
        var extra = Math.Max(0, (inner.Width - natural) / options.Count);

        int? clicked = null;
        var x = inner.X;
        double thumbX = 0, thumbWidth = 0;
        var slots = new Rect[options.Count];
        for (var i = 0; i < options.Count; i++)
        {
            var slot = new Rect(x, inner.Y, widths[i] + extra, inner.Height);
            slots[i] = slot;
            if (Interact(Id(id, i), slot) && i != selected) clicked = i;
            if (i == selected) (thumbX, thumbWidth) = (slot.X, slot.Width);
            x = slot.Right;
        }

        var left = Animate(Id(id, -1), thumbX);
        var width = Animate(Id(id, -2), thumbWidth);
        var thumb = new Rect(left, inner.Y, width, inner.Height);
        if (!Theme.IsDark) Shadow(thumb, (float)S(Metrics.RadiusSm), S(2), S(1), Theme.Shadow);
        FillRounded(thumb, (float)S(Metrics.RadiusSm), Theme.IsDark ? Theme.SurfacePressed : Theme.SurfaceRaised);

        for (var i = 0; i < options.Count; i++)
        {
            var color = i == selected || IsHot(Id(id, i)) ? Theme.TextPrimary : Theme.TextSecondary;
            Text(options[i], slots[i], color, S(Metrics.FontSm), Weight.Semibold, TextAlign.Center);
        }
        return clicked;
    }

    /// <summary>An on/off switch. The knob slides; the track takes the accent when on.</summary>
    public bool Toggle(int id, Rect bounds, bool on)
    {
        var track = new Rect(bounds.Right - S(36), bounds.Center.Y - S(10), S(36), S(20));
        var clicked = Interact(id, track);

        var t = Animate(id, on ? 1 : 0);
        FillRounded(track, (float)S(10), on ? Theme.Accent : IsHot(id) ? Theme.StrokeStrong : Theme.SurfacePressed);

        var knob = new Point(track.X + S(10) + S(16) * t, track.Center.Y);
        FillCircle(knob, (float)S(7), on ? Theme.TextOnAccent : Theme.TextSecondary);
        return clicked;
    }

    /// <summary>A horizontal slider: a thin track, the accent fill, and a white knob. Returns true while
    /// dragged, with the new value; a press on the track jumps there.</summary>
    public bool Slider(int id, Rect bounds, double min, double max, ref double value)
    {
        Interact(id, bounds);

        var track = new Rect(bounds.X, bounds.Center.Y - S(2), bounds.Width, S(4));
        FillRounded(track, (float)S(2), Theme.SurfacePressed);

        var range = Math.Max(0.0001, max - min);
        var t = Math.Clamp((value - min) / range, 0, 1);
        if (t > 0) FillRounded(new Rect(track.X, track.Y, track.Width * t, track.Height), (float)S(2), Theme.Accent);

        var knob = new Point(bounds.X + bounds.Width * t, bounds.Center.Y);
        var radius = (float)S(IsActive(id) ? 8 : 7);
        FillCircle(knob with { Y = knob.Y + S(1) }, radius + 1, Rgba.Black.WithAlpha(70));
        FillCircle(knob, radius, Rgba.White);

        if (!IsActive(id)) return false;

        var dragged = Math.Clamp((Pointer.X - bounds.X) / Math.Max(1, bounds.Width), 0, 1);
        var updated = min + dragged * range;
        if (Math.Abs(updated - value) < 0.0001) return false;
        value = updated;
        return true;
    }

    /// <summary>A colour dot. Selected draws a ring outside it, clear of the dot by a gap of the
    /// surface colour, so a white swatch still shows its selection.</summary>
    public bool Swatch(int id, Rect bounds, Rgba color, bool selected, Rgba surface)
    {
        var clicked = Interact(id, bounds);
        var center = bounds.Center;
        var radius = (float)(Math.Min(bounds.Width, bounds.Height) / 2 * Animate(id, IsHot(id) ? 1.15 : 1, Metrics.MotionFast));

        if (selected)
        {
            FillCircle(center, radius + (float)S(3.5), Theme.TextPrimary);
            FillCircle(center, radius + (float)S(2), surface);
        }
        FillCircle(center, radius, color);
        StrokeCircle(center, radius - 0.5f, color.A < 255 || Palette.IsLight(color)
            ? Rgba.Black.WithAlpha(50) : Rgba.White.WithAlpha(36));
        return clicked;
    }

    /// <summary>A number with minus and plus either side. Returns -1, 0 or +1.</summary>
    public int Stepper(int id, Rect bounds, string text)
    {
        var radius = (float)S(Metrics.RadiusSm);
        FillRounded(bounds, radius, Theme.SurfacePane);
        StrokeRounded(bounds, radius, Theme.StrokeDefault);

        // The two halves are icon buttons like any other, so they hover like any other.
        var minus = new Rect(bounds.X + S(2), bounds.Y + S(2), S(28), bounds.Height - S(4));
        var plus = new Rect(bounds.Right - S(30), bounds.Y + S(2), S(28), bounds.Height - S(4));
        var step = 0;
        if (IconButton(Id(id, 0), minus, Icons.Minus, iconSize: 15)) step = -1;
        if (IconButton(Id(id, 1), plus, Icons.Plus, iconSize: 15)) step = 1;
        Text(text, new Rect(minus.Right, bounds.Y, plus.X - minus.Right, bounds.Height),
            Theme.TextPrimary, S(Metrics.FontMd), Weight.Semibold, TextAlign.Center);
        return step;
    }

    /// <summary>A thin scroll indicator down the right edge of <paramref name="viewport"/>. Nothing is
    /// drawn when the content fits. Indicator only: the wheel scrolls.</summary>
    public void Scrollbar(Rect viewport, double content, double scroll)
    {
        var overflow = content - viewport.Height;
        if (overflow <= 0.5 || viewport.Height <= 0) return;

        var width = S(4);
        var length = Math.Max(Math.Min(S(24), viewport.Height), viewport.Height * (viewport.Height / content));
        var travel = viewport.Height - length;
        var progress = Math.Clamp(scroll / overflow, 0, 1);

        FillRounded(new Rect(viewport.Right - width - S(3), viewport.Y + travel * progress, width, length),
            (float)(width / 2), Theme.StrokeStrong);
    }

    // ============================  TEXT FIELDS  ============================

    /// <summary>The field that has the keyboard, the text as typed, and whether the next keystroke
    /// replaces it - a field selects all on focus, so typing overwrites rather than appends.</summary>
    private int _focus;
    private string _draft = "";
    private bool _replace;
    private Rect _focusBounds;

    /// <summary>Keys the window routed here since the last frame, consumed by the focused field.</summary>
    private readonly List<(char Char, VIRTUAL_KEY Key, bool Shift)> _keys = [];

    public bool HasKeyboardFocus => _focus != 0;

    /// <summary>True while a caret is showing. A blink needs a slow tick, not the display rate, so the
    /// window schedules it apart from <see cref="Animating"/>.</summary>
    public bool Blinking { get; private set; }

    /// <summary>Half a caret cycle, in milliseconds - the Windows default.</summary>
    public const long CaretBlink = 530;

    /// <summary>A typed character, for the focused field. The window sends WM_CHAR here while
    /// <see cref="HasKeyboardFocus"/>, so a letter typed into a box never reaches a tool shortcut.</summary>
    public void Char(char character) => _keys.Add((character, 0, false));

    /// <summary>An editing key - Backspace, Enter, Escape, the arrows - for the focused field.</summary>
    public void Key(VIRTUAL_KEY key, bool shift) => _keys.Add(('\0', key, shift));

    public void Blur()
    {
        _focus = 0;
        _draft = "";
    }

    /// <summary>
    /// A single-line field. Clicking focuses it with everything selected; typing edits a draft that is
    /// reported every time it changes, so the caller applies each valid step live and ignores the
    /// half-typed ones. Up and Down step a numeric field (Shift for tens). Enter and Escape let go of
    /// the keyboard, leaving whatever was applied - there is no pending edit to throw away.
    /// </summary>
    public FieldResult Field(
        int id, Rect bounds, string value, Func<char, bool> accept, int maxLength,
        string? prefix = null, string? suffix = null, TextAlign align = TextAlign.Left,
        Icon? leading = null, string? placeholder = null, Face face = Face.Mono)
    {
        if (Interact(id, bounds) && _focus != id)
        {
            _focus = id;
            _draft = value;
            _replace = true;
        }

        var focused = _focus == id;
        var changed = false;
        var step = 0;

        if (focused)
        {
            _focusBounds = bounds;
            foreach (var (character, key, shift) in _keys)
            {
                if (character != '\0')
                {
                    if (!accept(character)) continue;
                    if (_replace) _draft = "";
                    _replace = false;
                    if (_draft.Length >= maxLength) continue;
                    _draft += character;
                    changed = true;
                    continue;
                }

                switch (key)
                {
                    case VIRTUAL_KEY.VK_BACK:
                        _draft = _replace ? "" : _draft.Length > 0 ? _draft[..^1] : _draft;
                        _replace = false;
                        changed = true;
                        break;
                    case VIRTUAL_KEY.VK_UP:
                        step = shift ? 10 : 1;
                        break;
                    case VIRTUAL_KEY.VK_DOWN:
                        step = shift ? -10 : -1;
                        break;
                    case VIRTUAL_KEY.VK_RETURN:
                    case VIRTUAL_KEY.VK_ESCAPE:
                        Blur();
                        break;
                }
                if (_focus != id) break;
            }
            _keys.Clear();

            // A step rewrites the draft from the value the caller will now apply, next frame.
            if (step != 0) _replace = true;
        }

        focused = _focus == id;
        var radius = (float)S(Metrics.RadiusSm);
        FillRounded(bounds, radius, IsHot(id) && !focused ? Theme.SurfaceHover : Theme.SurfacePane);
        StrokeRounded(bounds, radius,
            focused ? Theme.Accent : IsHot(id) ? Theme.StrokeStrong : Theme.StrokeDefault,
            focused ? (float)S(1.5) : 1);

        var font = S(Metrics.FontSm);
        var x = bounds.X + S(8);
        var right = bounds.Right - S(8);

        if (leading is not null)
        {
            Icon(leading, new Rect(x, bounds.Y, S(15), bounds.Height), focused || IsHot(id) ? Theme.TextPrimary : Theme.TextSecondary, S(15));
            x += S(15) + S(8);
        }

        if (prefix is not null)
        {
            var w = MeasureText(prefix, S(10.5), Weight.Bold);
            Text(prefix, new Rect(x, bounds.Y, w + 1, bounds.Height), Theme.TextTertiary, S(10.5), Weight.Bold);
            x += w + S(5);
        }
        if (suffix is not null)
        {
            var w = MeasureText(suffix, S(10.5));
            Text(suffix, new Rect(right - w, bounds.Y, w + 1, bounds.Height), Theme.TextQuaternary, S(10.5));
            right -= w + S(3);
        }

        var shown = focused ? _draft : value;
        var textWidth = MeasureText(shown, font, face: face);
        var textX = align == TextAlign.Center ? x + (right - x - textWidth) / 2 : x;

        if (focused && _replace && shown.Length > 0)
            FillRect(new Rect(textX - S(1), bounds.Center.Y - S(8), textWidth + S(2), S(16)), Theme.AccentSoft);

        if (shown.Length == 0 && placeholder is not null)
            Text(placeholder, new Rect(textX + S(3), bounds.Y, Math.Max(1, right - textX), bounds.Height), Theme.TextTertiary, font);
        Text(shown, new Rect(textX, bounds.Y, Math.Max(1, right - textX), bounds.Height), Theme.TextPrimary, font,
            face: face);

        // Shown whenever the field is focused, so an empty focused box never looks dead.
        if (focused)
        {
            Blinking = true;
            if (Now / CaretBlink % 2 == 0)
                FillRect(new Rect(textX + textWidth + S(1), bounds.Center.Y - S(8), S(1.5), S(16)), Theme.TextPrimary);
        }

        return new FieldResult(changed, focused ? _draft : value, step);
    }

    // ============================  TOOLTIPS  ============================

    private (int Id, Rect Anchor, string Text, string? Keycap, TipSide Side)? _tip;
    private int _lastHot;
    private long _hotSince;

    /// <summary>The delay before a tooltip appears: long enough that sweeping across a bar does not
    /// strobe labels, short enough that a deliberate hover is answered.</summary>
    private const long TipDelay = 420;

    /// <summary>Asks for a tooltip on <paramref name="id"/>'s anchor. Shown after a delay, while the
    /// widget stays hot and nothing is pressed, and drawn at the end of the frame over everything.</summary>
    public void Tip(int id, Rect anchor, string text, string? keycap = null, TipSide side = TipSide.Below)
    {
        if (IsHot(id) && Active == 0) _tip = (id, anchor, text, keycap, side);
    }

    private void DrawTip()
    {
        if (_tip is not { } tip || tip.Id != Hot || PointerDown) return;

        var waited = Now - _hotSince;
        if (waited < TipDelay)
        {
            Animating = true;
            return;
        }
        var fade = Math.Clamp((waited - TipDelay) / Metrics.MotionFast, 0, 1);
        if (fade < 1) Animating = true;

        var font = S(Metrics.FontSm);
        var height = S(26);
        var textWidth = MeasureText(tip.Text, font, Weight.Semibold);
        var width = textWidth + S(16) + (tip.Keycap is null ? 0 : KeycapWidth(tip.Keycap) + S(8));

        var size = _area ?? _target.Object.GetSize();
        var margin = S(6);
        double x, y;
        if (tip.Side == TipSide.Right)
        {
            x = tip.Anchor.Right + S(10);
            y = tip.Anchor.Center.Y - height / 2;
        }
        else
        {
            x = tip.Anchor.Center.X - width / 2;
            y = tip.Anchor.Bottom + S(8);
            if (y + height > size.height - margin) y = tip.Anchor.Y - height - S(8);
        }
        x = Math.Clamp(x, margin, Math.Max(margin, size.width - width - margin));
        y += S(3) * (1 - fade);

        // Themed, with the strong stroke separating it from a surface of the same tone.
        var box = new Rect(x, y, width, height);
        Rgba Faded(Rgba color) => color.WithAlpha((byte)(color.A * fade));
        var radius = (float)S(Metrics.RadiusSm);
        Shadow(box, radius, S(10), S(4), Faded(Theme.Shadow));
        FillRounded(box, radius, Faded(Theme.SurfaceRaised));
        StrokeRounded(box, radius, Faded(Theme.StrokeStrong));
        Text(tip.Text, new Rect(x + S(8), y, textWidth + 1, height), Faded(Theme.TextPrimary), font, Weight.Semibold);

        if (tip.Keycap is null) return;
        var cap = new Rect(x + S(16) + textWidth, y + height / 2 - S(9), KeycapWidth(tip.Keycap), S(18));
        StrokeRounded(cap, (float)S(Metrics.RadiusXs), Faded(Theme.StrokeDefault));
        Text(tip.Keycap, cap, Faded(Theme.TextSecondary), S(10.5), face: Face.Mono, align: TextAlign.Center);
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

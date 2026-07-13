namespace NexusShot.Core;

/// <summary>
/// UI-independent editing session state, in image-pixel coordinates. The document owns the
/// annotation list, selection, undo/redo history and the pending crop. The view only supplies
/// pointer positions already mapped into image space.
/// </summary>
public sealed class EditorDocument
{
    private enum GestureKind { None, Draw, Move, Resize, CropMove, CropResize }

    private readonly List<Annotation> _annotations = [];
    private readonly Stack<IReadOnlyList<Annotation>> _undo = new();
    private readonly Stack<IReadOnlyList<Annotation>> _redo = new();
    private readonly Dictionary<Guid, Rect> _strokeBounds = [];

    private Annotation? _draft;
    private Point _dragOrigin;
    private GestureKind _gesture;
    private ResizeHandle _resizeHandle;
    private Rect _resizeOriginBounds;

    // Move and resize snapshot lazily, on the first pointer movement: pushing on the press alone
    // would fill the undo stack with identical states every time the user merely clicks a shape.
    private bool _gestureUndoPushed;
    private bool _eraserChanged;
    private readonly Dictionary<Annotation, EraserMask> _activeEraserMasks = [];
    private readonly HashSet<Annotation> _eraserDirtyAnnotations = [];

    public IReadOnlyList<Annotation> Annotations => _annotations;
    public EditorTool ActiveTool { get; set; } = EditorTool.Select;
    public string ColorHex { get; set; } = "#FF3B30";
    public double StrokeThickness { get; set; } = 4;
    public double BrushThickness { get; private set; } = 48;
    public double EraserThickness { get; private set; } = 48;

    /// <summary>What the size slider is currently editing. The counterpart to SetStrokeThickness:
    /// the two must route by tool identically, or the slider shows one value and writes another.</summary>
    public double ActiveThickness => ActiveTool switch
    {
        EditorTool.Brush => BrushThickness,
        EditorTool.Eraser => EraserThickness,
        EditorTool.Text => TextFontSize,
        _ => StrokeThickness,
    };

    /// <summary>Formatting applied to newly placed text annotations.</summary>
    public double TextFontSize { get; set; } = 20;
    public bool TextBold { get; set; }
    public bool TextItalic { get; set; }
    public bool TextUnderline { get; set; }
    public Annotation? Selected { get; private set; }
    public Rect? CropBounds { get; private set; }

    /// <summary>Image dimensions in pixels; the crop frame is clamped to them.</summary>
    public double ImageWidth { get; private set; }
    public double ImageHeight { get; private set; }

    /// <summary>
    /// The live crop frame while the crop tool is engaged. Nothing is applied until
    /// <see cref="CommitCrop"/>; cancelling leaves <see cref="CropBounds"/> untouched.
    /// </summary>
    public Rect? PendingCrop { get; private set; }

    public bool IsCropSessionActive => PendingCrop is not null;

    /// <summary>True while the active gesture is drawing a new annotation, not moving/resizing one.</summary>
    public bool IsDrawGestureActive => _gesture == GestureKind.Draw;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Raised whenever the annotation list, selection or crop changes. In the immediate-mode
    /// renderer this is only an invalidation signal: there is no retained visual tree to patch,
    /// so the view simply asks for a repaint and the next frame draws current state.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>True when the selected annotation is resized via box handles.
    /// Brush strokes (pen, blur, pixelate) have no meaningful box to resize.</summary>
    public static bool IsBoxResizable(Annotation annotation) => annotation.Tool
        is EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Highlight
        or EditorTool.Spotlight or EditorTool.Text;

    public void SetImageSize(double width, double height)
    {
        ImageWidth = width;
        ImageHeight = height;
    }

    // ============================  CROP SESSION  ============================
    // The crop tool never draws a fresh rectangle. Engaging it opens a session whose frame starts
    // at the current crop (or the whole image) and is then moved/resized via handles; the frame
    // becomes CropBounds only on commit.

    public void BeginCropSession()
    {
        if (ImageWidth <= 0 || ImageHeight <= 0) return;
        Selected = null;
        PendingCrop = CropBounds ?? new Rect(0, 0, ImageWidth, ImageHeight);
        Notify();
    }

    /// <summary>Applies the pending frame. A frame covering the whole image means "no crop".</summary>
    public void CommitCrop()
    {
        if (PendingCrop is not { } pending) return;
        var coversEverything = pending.X <= 0.5 && pending.Y <= 0.5
            && pending.Right >= ImageWidth - 0.5 && pending.Bottom >= ImageHeight - 0.5;
        CropBounds = coversEverything ? null : pending;
        PendingCrop = null;
        Notify();
    }

    public void CancelCropSession()
    {
        if (PendingCrop is null) return;
        PendingCrop = null;
        Notify();
    }

    /// <summary>The crop-frame handle under <paramref name="point"/>, if a session is active.</summary>
    public ResizeHandle? GetCropHandleAt(Point point, double tolerance)
    {
        if (PendingCrop is not { } crop) return null;
        return BoxGeometry.HitTestHandle(crop, point, tolerance);
    }

    /// <summary>Begins a pointer gesture. <paramref name="handleTolerance"/> is the grab radius in
    /// image pixels — the view scales it so handles stay grabbable when zoomed out.</summary>
    public void BeginGesture(Point point, double handleTolerance = 8)
    {
        _dragOrigin = point;
        _gestureUndoPushed = false;
        _eraserChanged = false;
        _activeEraserMasks.Clear();
        _eraserDirtyAnnotations.Clear();

        // An active crop session owns the pointer: handles resize, the interior moves, and
        // clicks outside the frame do nothing until the session is committed or cancelled.
        if (PendingCrop is { } pendingCrop)
        {
            if (GetCropHandleAt(point, handleTolerance) is { } cropHandle)
            {
                _gesture = GestureKind.CropResize;
                _resizeHandle = cropHandle;
                _resizeOriginBounds = pendingCrop;
            }
            else
            {
                _gesture = pendingCrop.Contains(point) ? GestureKind.CropMove : GestureKind.None;
            }
            return;
        }

        // The crop tool without a session (image not loaded yet) must not fall through and draw.
        if (ActiveTool == EditorTool.Crop)
        {
            _gesture = GestureKind.None;
            return;
        }

        // A selected shape's handles win over everything: resizing stays possible while a drawing
        // tool is active, so a shape can be adjusted right after it is placed.
        if (Selected is not null && GetResizeHandleAt(Selected, point, handleTolerance) is { } handle)
        {
            _gesture = GestureKind.Resize;
            _resizeHandle = handle;
            _resizeOriginBounds = Selected.Bounds;
            return;
        }

        if (ActiveTool == EditorTool.Select)
        {
            // Topmost annotation wins, matching paint order.
            Selected = _annotations.LastOrDefault(a => a.HitTest(point));
            _gesture = Selected is null ? GestureKind.None : GestureKind.Move;
            Notify();
            return;
        }

        Selected = null;
        PushUndo();
        _draft = new Annotation
        {
            Tool = ActiveTool,
            Start = point,
            End = point,
            ColorHex = ColorHex,
            StrokeThickness = ActiveThickness,
            CounterValue = ActiveTool == EditorTool.Counter ? NextCounterValue() : 0,
            FontSize = TextFontSize,
            IsBold = TextBold,
            IsItalic = TextItalic,
            IsUnderline = TextUnderline,
        };
        if (_draft.IsStrokeTool) _draft.Points.Add(point);
        if (ActiveTool != EditorTool.Eraser) _annotations.Add(_draft);
        _gesture = GestureKind.Draw;

        // The eraser bites on the press, not only on the drag: it mutates masks rather than being an
        // annotation, so without this a tap that never moves erases nothing.
        if (ActiveTool == EditorTool.Eraser) _eraserChanged |= ApplyEraserSegment(_draft);

        Notify();
    }

    /// <summary>Continues the active gesture.</summary>
    public void ContinueGesture(Point point) => ContinueGestureCore(point);

    /// <summary>
    /// Adds coalesced pointer samples as one document update. Freehand geometry keeps the input
    /// fidelity while the view repaints once for the batch.
    /// </summary>
    public void ContinueGesture(IReadOnlyList<Point> points)
    {
        if (points.Count == 0) return;

        if (_gesture == GestureKind.Draw && _draft is { Tool: EditorTool.Eraser } eraser)
        {
            ContinueEraserBatch(eraser, points);
            return;
        }
        foreach (var point in points) ContinueGestureCore(point);
        Notify();
    }

    private void ContinueEraserBatch(Annotation eraser, IReadOnlyList<Point> points)
    {
        var path = new List<Point>(points.Count + 1) { eraser.Points[^1] };
        foreach (var point in points)
        {
            eraser.End = point;
            var count = eraser.Points.Count;
            AppendStrokePoint(eraser, point);
            if (eraser.Points.Count > count) path.Add(eraser.Points[^1]);
        }
        if (path.Count == 1) path.Add(path[0]);
        _eraserChanged |= ApplyEraserPath(path, PaintStrokeGeometry.Radius(eraser.StrokeThickness));
        Notify();
    }

    private void ContinueGestureCore(Point point)
    {
        switch (_gesture)
        {
            case GestureKind.Move when Selected is not null:
                EnsureGestureUndo();
                var (moveX, moveY) = ClampDeltaToImage(Selected, point.X - _dragOrigin.X, point.Y - _dragOrigin.Y);
                Selected.Translate(moveX, moveY);
                if (_strokeBounds.TryGetValue(Selected.Id, out var cachedBounds))
                    _strokeBounds[Selected.Id] = new Rect(
                        cachedBounds.X + moveX, cachedBounds.Y + moveY,
                        cachedBounds.Width, cachedBounds.Height);
                _dragOrigin = point;
                break;

            case GestureKind.Resize when Selected is not null:
                EnsureGestureUndo();
                ApplyResize(Selected, point);
                break;

            case GestureKind.Draw when _draft is not null:
                _draft.End = point;
                if (_draft.IsStrokeTool) AppendStrokePoint(_draft, point);
                if (_draft.Tool == EditorTool.Eraser)
                    _eraserChanged |= ApplyEraserSegment(_draft);
                break;

            case GestureKind.CropMove when PendingCrop is { } crop:
                var dx = Math.Clamp(point.X - _dragOrigin.X, -crop.X, ImageWidth - crop.Right);
                var dy = Math.Clamp(point.Y - _dragOrigin.Y, -crop.Y, ImageHeight - crop.Bottom);
                PendingCrop = new Rect(crop.X + dx, crop.Y + dy, crop.Width, crop.Height);
                _dragOrigin = point;
                break;

            case GestureKind.CropResize when PendingCrop is not null:
                PendingCrop = ResizeCropFrame(point);
                break;
        }
    }

    /// <summary>Clamps a move delta so the annotation's bounds stay inside the image; oversized
    /// shapes can only move back toward the inside.</summary>
    private (double Dx, double Dy) ClampDeltaToImage(Annotation annotation, double dx, double dy)
    {
        if (ImageWidth <= 0 || ImageHeight <= 0) return (dx, dy);
        var bounds = annotation.Bounds;
        var translated = BoxGeometry.Translate(bounds, dx, dy, new Rect(0, 0, ImageWidth, ImageHeight));
        return (translated.X - bounds.X, translated.Y - bounds.Y);
    }

    /// <summary>Resizes the crop frame from its origin bounds, normalised and clamped to the image.</summary>
    private Rect ResizeCropFrame(Point point) => BoxGeometry.Resize(
        _resizeOriginBounds,
        _resizeHandle,
        point,
        new Rect(0, 0, ImageWidth, ImageHeight),
        new Size(8, 8));

    /// <summary>Ends the active gesture, discarding degenerate shapes.</summary>
    public void EndGesture(Point point)
    {
        if (_gesture is GestureKind.CropMove or GestureKind.CropResize)
        {
            _gesture = GestureKind.None;
            Notify();
            return;
        }

        if (_gesture is GestureKind.Move or GestureKind.Resize)
        {
            _gesture = GestureKind.None;
            Notify();
            return;
        }

        if (_gesture != GestureKind.Draw || _draft is null)
        {
            _gesture = GestureKind.None;
            return;
        }

        _gesture = GestureKind.None;
        _draft.End = point;

        if (_draft.Tool == EditorTool.Eraser)
        {
            if (!_eraserChanged) _undo.TryPop(out _);
            _eraserChanged = false;
            _draft = null;
            Notify();
            return;
        }

        // A click with a shape tool produces a zero-area shape; drop it and the undo entry with it.
        var bounds = _draft.Bounds;
        var isDegenerate = _draft.Tool switch
        {
            EditorTool.Text => false,
            EditorTool.Counter => false,
            // A single paint sample is a valid round dab whose diameter is StrokeThickness.
            _ when _draft.IsStrokeTool => _draft.Points.Count == 0,
            _ => bounds.Width < 3 && bounds.Height < 3,
        };

        if (isDegenerate)
        {
            _annotations.Remove(_draft);
            _undo.TryPop(out _);
        }
        else
        {
            // A text annotation gets a workable editing box even when merely clicked into place.
            if (_draft.Tool == EditorTool.Text) NormalizeTextBounds(_draft);

            // Shapes stay selected so their handles are grabbable straight after placement.
            // Brush strokes do not: a finished stroke should leave only its effect visible,
            // never a bounding box, unless the user explicitly selects it later.
            Selected = _draft.IsStrokeTool ? null : _draft;
        }

        _draft = null;
        Notify();
    }

    /// <summary>The handle under <paramref name="point"/> on the selected annotation, if any.</summary>
    public ResizeHandle? GetResizeHandleAt(Annotation annotation, Point point, double tolerance)
    {
        if (annotation.IsLinear)
        {
            if (point.DistanceTo(annotation.Start) <= tolerance) return ResizeHandle.LineStart;
            if (point.DistanceTo(annotation.End) <= tolerance) return ResizeHandle.LineEnd;
            return null;
        }

        if (!IsBoxResizable(annotation)) return null;

        var bounds = annotation.Bounds;
        foreach (var (handle, position) in BoxHandlePositions(bounds))
        {
            if (point.DistanceTo(position) <= tolerance) return handle;
        }
        return null;
    }

    /// <summary>The eight box handle positions, for hit testing and for the view's adorners.</summary>
    public static IEnumerable<(ResizeHandle Handle, Point Position)> BoxHandlePositions(Rect bounds)
    {
        foreach (var handle in BoxGeometry.Handles)
            yield return (handle, BoxGeometry.HandlePosition(bounds, handle));
    }

    private void ApplyResize(Annotation annotation, Point point)
    {
        if (annotation.IsLinear)
        {
            if (_resizeHandle == ResizeHandle.LineStart) annotation.Start = point;
            else annotation.End = point;
            return;
        }

        // The origin bounds anchor the sides the handle does not move; crossing an anchored side
        // simply normalises through Start/End ordering.
        var resized = BoxGeometry.Resize(
            _resizeOriginBounds,
            _resizeHandle,
            point,
            new Rect(0, 0, ImageWidth, ImageHeight));
        annotation.Start = new Point(resized.Left, resized.Top);
        annotation.End = new Point(resized.Right, resized.Bottom);
    }

    public void SetColor(string colorHex)
    {
        ColorHex = colorHex;
        if (Selected is null || Selected.ColorHex == colorHex) return;
        PushUndo();
        Selected.ColorHex = colorHex;
        Notify();
    }

    /// <summary>
    /// Applies the size slider to whatever the active tool sizes: the brush and eraser footprints,
    /// the text's font, or a stroke's width. One writer, routed by tool.
    ///
    /// <paramref name="isAdjusting"/> is true for the continuous ticks of a slider drag, which must
    /// not each push an undo entry.
    /// </summary>
    public void SetStrokeThickness(double thickness, bool isAdjusting = false)
    {
        switch (ActiveTool)
        {
            case EditorTool.Brush: BrushThickness = thickness; return;
            case EditorTool.Eraser: EraserThickness = thickness; return;
            case EditorTool.Text: SetFontSize(thickness, isAdjusting); return;
        }

        StrokeThickness = thickness;
        if (Selected is null || Selected.StrokeThickness == thickness) return;
        if (!isAdjusting) PushUndo();
        Selected.StrokeThickness = thickness;
        Notify();
    }

    /// <summary>The font size for new text, and for the text being edited or selected. The box grows
    /// with the font, or the larger glyphs are clipped by bounds sized for the old one.</summary>
    private void SetFontSize(double size, bool isAdjusting)
    {
        TextFontSize = size;

        var target = Selected is { Tool: EditorTool.Text } selected ? selected : null;
        if (target is null || target.FontSize == size) return;

        if (!isAdjusting) PushUndo();
        target.FontSize = size;
        NormalizeTextBounds(target);
        Notify();
    }

    /// <summary>Commits an inline editor's text and final box (clamped to the image) as one undo
    /// step — the single write-back for a text edit.</summary>
    public void SetTextContent(Annotation annotation, string text, Rect bounds)
    {
        var clamped = ClampTextBounds(bounds);
        var boundsChanged = clamped != annotation.Bounds;
        if (annotation.Text == text && !boundsChanged) return;

        PushUndo();
        annotation.Text = text;
        annotation.Start = new Point(clamped.X, clamped.Y);
        annotation.End = new Point(clamped.Right, clamped.Bottom);
        Notify();
    }

    private Rect ClampTextBounds(Rect bounds)
    {
        var width = Math.Max(1, bounds.Width);
        var height = Math.Max(1, bounds.Height);
        if (ImageWidth > 0 && ImageHeight > 0)
        {
            width = Math.Min(width, ImageWidth);
            height = Math.Min(height, ImageHeight);
        }
        var x = ImageWidth > 0 ? Math.Clamp(bounds.X, 0, ImageWidth - width) : bounds.X;
        var y = ImageHeight > 0 ? Math.Clamp(bounds.Y, 0, ImageHeight - height) : bounds.Y;
        return new Rect(x, y, width, height);
    }

    /// <summary>Applies a formatting change to the defaults and, when a text annotation is
    /// selected, to that annotation as one undo step.</summary>
    public void SetTextFormat(Action<EditorDocument> setDefault, Action<Annotation> apply)
    {
        setDefault(this);
        if (Selected is not { Tool: EditorTool.Text } text) return;
        PushUndo();
        apply(text);
        Notify();
    }

    /// <summary>
    /// Removes a just-created annotation together with the undo entry its creation pushed, as if
    /// it was never placed. Used when text entry is dismissed empty.
    /// </summary>
    public void CancelAnnotation(Annotation annotation)
    {
        if (!_annotations.Remove(annotation)) return;
        _undo.TryPop(out _);
        if (Selected == annotation) Selected = null;
        Notify();
    }

    /// <summary>Selects an annotation programmatically, without a pointer gesture.</summary>
    public void SelectAnnotation(Annotation? annotation)
    {
        Selected = annotation;
        Notify();
    }

    public void DeleteSelected()
    {
        if (Selected is null) return;
        PushUndo();
        _annotations.Remove(Selected);
        Selected = null;
        Notify();
    }

    public void Undo()
    {
        if (!_undo.TryPop(out var previous)) return;
        _redo.Push(Snapshot());
        Restore(previous);
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var next)) return;
        _undo.Push(Snapshot());
        Restore(next);
    }

    public void ClearCrop()
    {
        CropBounds = null;
        Notify();
    }

    /// <summary>Pristine state over a freshly saved image: annotations, crop and history are
    /// baked into the file now; tool/colour/formatting defaults survive.</summary>
    public void ResetAfterSave()
    {
        _annotations.Clear();
        _undo.Clear();
        _redo.Clear();
        _strokeBounds.Clear();
        _draft = null;
        _gesture = GestureKind.None;
        Selected = null;
        CropBounds = null;
        PendingCrop = null;
        Notify();
    }

    private bool ApplyEraserSegment(Annotation eraser)
    {
        if (eraser.Points.Count == 0) return false;
        var end = eraser.Points[^1];
        var start = eraser.Points.Count > 1 ? eraser.Points[^2] : end;
        return ApplyEraserPath([start, end], PaintStrokeGeometry.Radius(eraser.StrokeThickness));
    }

    private bool ApplyEraserPath(IReadOnlyList<Point> path, double radius)
    {
        var changed = false;
        var hitNow = new HashSet<Annotation>();

        foreach (var stroke in _annotations.Where(a => a.Tool is EditorTool.Pen or EditorTool.Brush))
        {
            var bounds = GetStrokeBounds(stroke);
            if (!PathMayTouchBounds(path, bounds, radius + stroke.StrokeThickness / 2)) continue;
            hitNow.Add(stroke);
            _eraserDirtyAnnotations.Add(stroke);
            if (!_activeEraserMasks.TryGetValue(stroke, out var mask))
            {
                mask = new EraserMask { Radius = radius, Points = [.. path] };
                stroke.Erasures.Add(mask);
                _activeEraserMasks.Add(stroke, mask);
            }
            else
            {
                var first = mask.Points.Count > 0 && mask.Points[^1] == path[0] ? 1 : 0;
                for (var i = first; i < path.Count; i++) mask.Points.Add(path[i]);
            }
            changed = true;
        }

        // A later re-entry starts a new mask rather than drawing an erasing bridge across an
        // area where this stroke was not under the cursor.
        foreach (var stroke in _activeEraserMasks.Keys.Where(stroke => !hitNow.Contains(stroke)).ToArray())
            _activeEraserMasks.Remove(stroke);
        return changed;
    }

    private Rect GetStrokeBounds(Annotation stroke)
    {
        if (_strokeBounds.TryGetValue(stroke.Id, out var bounds)) return bounds;
        bounds = stroke.Bounds;
        _strokeBounds[stroke.Id] = bounds;
        return bounds;
    }

    private static bool PathMayTouchBounds(IReadOnlyList<Point> path, Rect bounds, double reach)
    {
        for (var i = 1; i < path.Count; i++)
        {
            var a = path[i - 1];
            var b = path[i];
            if (Math.Max(a.X, b.X) + reach >= bounds.Left
                && Math.Min(a.X, b.X) - reach <= bounds.Right
                && Math.Max(a.Y, b.Y) + reach >= bounds.Top
                && Math.Min(a.Y, b.Y) - reach <= bounds.Bottom) return true;
        }
        return false;
    }

    private static void AppendStrokePoint(Annotation stroke, Point point)
    {
        if (stroke.Points.Count == 0)
        {
            stroke.Points.Add(point);
            return;
        }

        var previous = stroke.Points[^1];
        var dx = point.X - previous.X;
        var dy = point.Y - previous.Y;
        // Hardware can report several samples inside the same subpixel. They add model size and
        // render work without changing the visible path.
        if (dx * dx + dy * dy >= 0.25) stroke.Points.Add(point);
    }

    /// <summary>
    /// Gives a text annotation a minimum editable box, clamped inside the image: a bare click
    /// places a default-sized box, a drag keeps whatever area the user framed.
    /// </summary>
    private void NormalizeTextBounds(Annotation annotation)
    {
        var bounds = annotation.Bounds;
        var fontSize = Math.Max(12, annotation.FontSize);
        var width = Math.Max(bounds.Width, fontSize * 9);
        var height = Math.Max(bounds.Height, fontSize * 1.8);

        var x = bounds.X;
        var y = bounds.Y;
        if (ImageWidth > 0 && ImageHeight > 0)
        {
            width = Math.Min(width, ImageWidth);
            height = Math.Min(height, ImageHeight);
            x = Math.Clamp(x, 0, ImageWidth - width);
            y = Math.Clamp(y, 0, ImageHeight - height);
        }

        annotation.Start = new Point(x, y);
        annotation.End = new Point(x + width, y + height);
    }

    private int NextCounterValue() => _annotations.Count(a => a.Tool == EditorTool.Counter) + 1;

    /// <summary>Pushes the undo snapshot for a move/resize on its first actual movement.</summary>
    private void EnsureGestureUndo()
    {
        if (_gestureUndoPushed) return;
        _gestureUndoPushed = true;
        PushUndo();
    }

    private void PushUndo()
    {
        _undo.Push(Snapshot());
        _redo.Clear();
    }

    /// <summary>Deep-copies the annotation list so undo restores values, not shared references.</summary>
    private IReadOnlyList<Annotation> Snapshot() => _annotations.Select(a => a.Clone()).ToList();

    private void Restore(IReadOnlyList<Annotation> snapshot)
    {
        var selectedId = Selected?.Id;
        _annotations.Clear();
        _annotations.AddRange(snapshot);
        _strokeBounds.Clear();
        Selected = selectedId is null ? null : _annotations.FirstOrDefault(a => a.Id == selectedId);
        _draft = null;
        _gesture = GestureKind.None;
        Notify();
    }

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
}

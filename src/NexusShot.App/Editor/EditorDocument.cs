using NexusShot.App.Enums;
using NexusShot.App.Models;
using Windows.Foundation;

namespace NexusShot.App.Editor;

/// <summary>A grab point on the selected annotation: box corners and edges, or line endpoints.</summary>
public enum ResizeHandle
{
    TopLeft, Top, TopRight,
    Left, Right,
    BottomLeft, Bottom, BottomRight,
    LineStart, LineEnd,
}

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

    private Annotation? _draft;
    private Point _dragOrigin;
    private GestureKind _gesture;
    private ResizeHandle _resizeHandle;
    private Rect _resizeOriginBounds;

    // Move and resize snapshot lazily, on the first pointer movement: pushing on the press alone
    // would fill the undo stack with identical states every time the user merely clicks a shape.
    private bool _gestureUndoPushed;
    private bool _eraserChanged;

    public IReadOnlyList<Annotation> Annotations => _annotations;
    public EditorTool ActiveTool { get; set; } = EditorTool.Select;
    public string ColorHex { get; set; } = "#FF3B30";
    public double StrokeThickness { get; set; } = 4;
    public double BrushThickness { get; private set; } = 48;
    public double EraserThickness { get; private set; } = 40;

    public double ActiveThickness => ActiveTool switch
    {
        EditorTool.Brush => BrushThickness,
        EditorTool.Eraser => EraserThickness,
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

    /// <summary>Raised whenever the annotation list, selection or crop changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised at pointer-move rate while a single annotation is being drawn, moved or resized.
    /// Separate from <see cref="Changed"/> so the view can update just that annotation's visuals
    /// instead of rebuilding the whole canvas on every pointer event.
    /// </summary>
    public event EventHandler<Annotation>? ActiveAnnotationChanged;

    /// <summary>Raised at pointer-move rate while the crop frame is dragged, so the view can
    /// refresh just the crop adorner instead of rebuilding every annotation visual.</summary>
    public event EventHandler? PendingCropChanged;

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
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies the pending frame. A frame covering the whole image means "no crop".</summary>
    public void CommitCrop()
    {
        if (PendingCrop is not { } pending) return;
        var coversEverything = pending.X <= 0.5 && pending.Y <= 0.5
            && pending.Right >= ImageWidth - 0.5 && pending.Bottom >= ImageHeight - 0.5;
        CropBounds = coversEverything ? null : pending;
        PendingCrop = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void CancelCropSession()
    {
        if (PendingCrop is null) return;
        PendingCrop = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The crop-frame handle under <paramref name="point"/>, if a session is active.</summary>
    public ResizeHandle? GetCropHandleAt(Point point, double tolerance)
    {
        if (PendingCrop is not { } crop) return null;
        foreach (var (handle, position) in BoxHandlePositions(crop))
        {
            if (Distance(point, position) <= tolerance) return handle;
        }
        return null;
    }

    /// <summary>Begins a pointer gesture. <paramref name="handleTolerance"/> is the grab radius in
    /// image pixels — the view scales it so handles stay grabbable when zoomed out.</summary>
    public void BeginGesture(Point point, double handleTolerance = 8)
    {
        _dragOrigin = point;
        _gestureUndoPushed = false;
        _eraserChanged = false;

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
            Changed?.Invoke(this, EventArgs.Empty);
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
        if (IsStroke(ActiveTool)) _draft.Points.Add(point);
        if (ActiveTool != EditorTool.Eraser) _annotations.Add(_draft);
        _gesture = GestureKind.Draw;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Continues the active gesture.</summary>
    public void ContinueGesture(Point point)
    {
        switch (_gesture)
        {
            case GestureKind.Move when Selected is not null:
                EnsureGestureUndo();
                var (moveX, moveY) = ClampDeltaToImage(Selected, point.X - _dragOrigin.X, point.Y - _dragOrigin.Y);
                Selected.Translate(moveX, moveY);
                _dragOrigin = point;
                ActiveAnnotationChanged?.Invoke(this, Selected);
                break;

            case GestureKind.Resize when Selected is not null:
                EnsureGestureUndo();
                ApplyResize(Selected, point);
                ActiveAnnotationChanged?.Invoke(this, Selected);
                break;

            case GestureKind.Draw when _draft is not null:
                _draft.End = point;
                if (IsStroke(_draft.Tool)) _draft.Points.Add(point);
                if (_draft.Tool == EditorTool.Eraser)
                {
                    _eraserChanged |= ApplyEraserSegment(_draft);
                    Changed?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    ActiveAnnotationChanged?.Invoke(this, _draft);
                }
                break;

            case GestureKind.CropMove when PendingCrop is { } crop:
                var dx = Math.Clamp(point.X - _dragOrigin.X, -crop.X, ImageWidth - crop.Right);
                var dy = Math.Clamp(point.Y - _dragOrigin.Y, -crop.Y, ImageHeight - crop.Bottom);
                PendingCrop = new Rect(crop.X + dx, crop.Y + dy, crop.Width, crop.Height);
                _dragOrigin = point;
                PendingCropChanged?.Invoke(this, EventArgs.Empty);
                break;

            case GestureKind.CropResize when PendingCrop is not null:
                PendingCrop = ResizeCropFrame(point);
                PendingCropChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    /// <summary>Clamps a move delta so the annotation's bounds stay inside the image; oversized
    /// shapes can only move back toward the inside.</summary>
    private (double Dx, double Dy) ClampDeltaToImage(Annotation annotation, double dx, double dy)
    {
        if (ImageWidth <= 0 || ImageHeight <= 0) return (dx, dy);
        var bounds = annotation.Bounds;
        return (
            Math.Clamp(dx, Math.Min(0, -bounds.Left), Math.Max(0, ImageWidth - bounds.Right)),
            Math.Clamp(dy, Math.Min(0, -bounds.Top), Math.Max(0, ImageHeight - bounds.Bottom)));
    }

    /// <summary>Resizes the crop frame from its origin bounds, normalised and clamped to the image.</summary>
    private Rect ResizeCropFrame(Point point)
    {
        var origin = _resizeOriginBounds;
        var (left, top, right, bottom) = _resizeHandle switch
        {
            ResizeHandle.TopLeft => (point.X, point.Y, origin.Right, origin.Bottom),
            ResizeHandle.Top => (origin.Left, point.Y, origin.Right, origin.Bottom),
            ResizeHandle.TopRight => (origin.Left, point.Y, point.X, origin.Bottom),
            ResizeHandle.Left => (point.X, origin.Top, origin.Right, origin.Bottom),
            ResizeHandle.Right => (origin.Left, origin.Top, point.X, origin.Bottom),
            ResizeHandle.BottomLeft => (point.X, origin.Top, origin.Right, point.Y),
            ResizeHandle.Bottom => (origin.Left, origin.Top, origin.Right, point.Y),
            _ => (origin.Left, origin.Top, point.X, point.Y),
        };

        var x1 = Math.Clamp(Math.Min(left, right), 0, ImageWidth);
        var x2 = Math.Clamp(Math.Max(left, right), 0, ImageWidth);
        var y1 = Math.Clamp(Math.Min(top, bottom), 0, ImageHeight);
        var y2 = Math.Clamp(Math.Max(top, bottom), 0, ImageHeight);

        // Enforce a minimum frame by growing back into the image, never past its edge.
        var minWidth = Math.Min(8, ImageWidth);
        var minHeight = Math.Min(8, ImageHeight);
        if (x2 - x1 < minWidth)
        {
            x2 = Math.Min(ImageWidth, x1 + minWidth);
            x1 = Math.Max(0, x2 - minWidth);
        }
        if (y2 - y1 < minHeight)
        {
            y2 = Math.Min(ImageHeight, y1 + minHeight);
            y1 = Math.Max(0, y2 - minHeight);
        }
        return new Rect(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>Ends the active gesture, discarding degenerate shapes.</summary>
    public void EndGesture(Point point)
    {
        if (_gesture is GestureKind.CropMove or GestureKind.CropResize)
        {
            _gesture = GestureKind.None;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_gesture is GestureKind.Move or GestureKind.Resize)
        {
            _gesture = GestureKind.None;
            // Restores paint order: the dragged annotation rendered on top while active.
            Changed?.Invoke(this, EventArgs.Empty);
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
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        // A click with a shape tool produces a zero-area shape; drop it and the undo entry with it.
        var bounds = _draft.Bounds;
        var isDegenerate = _draft.Tool switch
        {
            EditorTool.Text => false,
            EditorTool.Counter => false,
            _ when IsStroke(_draft.Tool) => _draft.Points.Count < 2,
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
            Selected = IsStroke(_draft.Tool) ? null : _draft;
        }

        _draft = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The handle under <paramref name="point"/> on the selected annotation, if any.</summary>
    public ResizeHandle? GetResizeHandleAt(Annotation annotation, Point point, double tolerance)
    {
        if (annotation.IsLinear)
        {
            if (Distance(point, annotation.Start) <= tolerance) return ResizeHandle.LineStart;
            if (Distance(point, annotation.End) <= tolerance) return ResizeHandle.LineEnd;
            return null;
        }

        if (!IsBoxResizable(annotation)) return null;

        var bounds = annotation.Bounds;
        foreach (var (handle, position) in BoxHandlePositions(bounds))
        {
            if (Distance(point, position) <= tolerance) return handle;
        }
        return null;
    }

    /// <summary>The eight box handle positions, for hit testing and for the view's adorners.</summary>
    public static IEnumerable<(ResizeHandle Handle, Point Position)> BoxHandlePositions(Rect bounds)
    {
        var centerX = bounds.Left + bounds.Width / 2;
        var centerY = bounds.Top + bounds.Height / 2;
        yield return (ResizeHandle.TopLeft, new Point(bounds.Left, bounds.Top));
        yield return (ResizeHandle.Top, new Point(centerX, bounds.Top));
        yield return (ResizeHandle.TopRight, new Point(bounds.Right, bounds.Top));
        yield return (ResizeHandle.Left, new Point(bounds.Left, centerY));
        yield return (ResizeHandle.Right, new Point(bounds.Right, centerY));
        yield return (ResizeHandle.BottomLeft, new Point(bounds.Left, bounds.Bottom));
        yield return (ResizeHandle.Bottom, new Point(centerX, bounds.Bottom));
        yield return (ResizeHandle.BottomRight, new Point(bounds.Right, bounds.Bottom));
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
        var origin = _resizeOriginBounds;
        var (left, top, right, bottom) = _resizeHandle switch
        {
            ResizeHandle.TopLeft => (point.X, point.Y, origin.Right, origin.Bottom),
            ResizeHandle.Top => (origin.Left, point.Y, origin.Right, origin.Bottom),
            ResizeHandle.TopRight => (origin.Left, point.Y, point.X, origin.Bottom),
            ResizeHandle.Left => (point.X, origin.Top, origin.Right, origin.Bottom),
            ResizeHandle.Right => (origin.Left, origin.Top, point.X, origin.Bottom),
            ResizeHandle.BottomLeft => (point.X, origin.Top, origin.Right, point.Y),
            ResizeHandle.Bottom => (origin.Left, origin.Top, origin.Right, point.Y),
            _ => (origin.Left, origin.Top, point.X, point.Y),
        };

        annotation.Start = new Point(left, top);
        annotation.End = new Point(right, bottom);
    }

    public void SetColor(string colorHex)
    {
        ColorHex = colorHex;
        if (Selected is null || Selected.ColorHex == colorHex) return;
        PushUndo();
        Selected.ColorHex = colorHex;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Applies a thickness to the selection. <paramref name="isAdjusting"/> is true for the
    /// continuous ticks of a slider drag, which must not each push an undo entry.
    /// </summary>
    public void SetStrokeThickness(double thickness, bool isAdjusting = false)
    {
        if (ActiveTool == EditorTool.Brush) BrushThickness = thickness;
        else if (ActiveTool == EditorTool.Eraser) EraserThickness = thickness;
        else StrokeThickness = thickness;
        if (ActiveTool is EditorTool.Brush or EditorTool.Eraser) return;
        if (Selected is null || Selected.StrokeThickness == thickness) return;
        if (!isAdjusting) PushUndo();
        Selected.StrokeThickness = thickness;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces an annotation's text as one undo step.</summary>
    public void SetText(Annotation annotation, string text)
    {
        if (annotation.Text == text) return;
        PushUndo();
        annotation.Text = text;
        Changed?.Invoke(this, EventArgs.Empty);
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
        Changed?.Invoke(this, EventArgs.Empty);
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
        Changed?.Invoke(this, EventArgs.Empty);
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
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects an annotation programmatically, without a pointer gesture.</summary>
    public void SelectAnnotation(Annotation? annotation)
    {
        Selected = annotation;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteSelected()
    {
        if (Selected is null) return;
        PushUndo();
        _annotations.Remove(Selected);
        Selected = null;
        Changed?.Invoke(this, EventArgs.Empty);
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
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Pristine state over a freshly saved image: annotations, crop and history are
    /// baked into the file now; tool/colour/formatting defaults survive.</summary>
    public void ResetAfterSave()
    {
        _annotations.Clear();
        _undo.Clear();
        _redo.Clear();
        _draft = null;
        _gesture = GestureKind.None;
        Selected = null;
        CropBounds = null;
        PendingCrop = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Tools drawn as a freehand path rather than a two-point shape.</summary>
    private static bool IsStroke(EditorTool tool) =>
        tool is EditorTool.Pen or EditorTool.Brush or EditorTool.Eraser
            or EditorTool.Blur or EditorTool.Pixelate;

    private bool ApplyEraserSegment(Annotation eraser)
    {
        if (eraser.Points.Count == 0) return false;
        var end = eraser.Points[^1];
        var start = eraser.Points.Count > 1 ? eraser.Points[^2] : end;
        // Erasing uses the same round-dab geometry as painting: the stored thickness is the
        // diameter and the pointer samples form its centreline.
        var radius = PaintStrokeGeometry.Radius(eraser.StrokeThickness);
        var changed = false;

        foreach (var stroke in _annotations.Where(a => a.Tool is EditorTool.Pen or EditorTool.Brush))
        {
            var reach = radius + stroke.StrokeThickness / 2;
            var hitArea = new Rect(
                Math.Min(start.X, end.X) - reach,
                Math.Min(start.Y, end.Y) - reach,
                Math.Abs(end.X - start.X) + reach * 2,
                Math.Abs(end.Y - start.Y) + reach * 2);
            var bounds = stroke.Bounds;
            if (hitArea.Right < bounds.Left || hitArea.Left > bounds.Right
                || hitArea.Bottom < bounds.Top || hitArea.Top > bounds.Bottom) continue;
            stroke.Erasures.Add(new EraserMask { Radius = radius, Points = [start, end] });
            changed = true;
        }
        return changed;
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

    private int NextCounterValue() =>
        _annotations.Count(a => a.Tool == EditorTool.Counter) + 1;

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
    private IReadOnlyList<Annotation> Snapshot() =>
        _annotations.Select(a => new Annotation
        {
            Id = a.Id,
            Tool = a.Tool,
            Start = a.Start,
            End = a.End,
            Points = [.. a.Points],
            Erasures = a.Erasures.Select(mask => new EraserMask
            {
                Radius = mask.Radius,
                Points = [.. mask.Points],
            }).ToList(),
            Text = a.Text,
            CounterValue = a.CounterValue,
            ColorHex = a.ColorHex,
            StrokeThickness = a.StrokeThickness,
            FontSize = a.FontSize,
            IsBold = a.IsBold,
            IsItalic = a.IsItalic,
            IsUnderline = a.IsUnderline,
        }).ToList();

    private void Restore(IReadOnlyList<Annotation> snapshot)
    {
        var selectedId = Selected?.Id;
        _annotations.Clear();
        _annotations.AddRange(snapshot);
        Selected = selectedId is null ? null : _annotations.FirstOrDefault(a => a.Id == selectedId);
        _draft = null;
        _gesture = GestureKind.None;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

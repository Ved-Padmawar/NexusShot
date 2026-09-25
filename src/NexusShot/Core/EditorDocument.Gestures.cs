namespace NexusShot.Core;

/// <summary>Pointer gestures: press, drag and release over the image - drawing, moving, resizing,
/// and the eraser's hit testing.</summary>
public sealed partial class EditorDocument
{
    /// <summary>Begins a pointer gesture. <paramref name="handleTolerance"/> is the grab radius in
    /// image pixels — the view scales it so handles stay grabbable when zoomed out.</summary>
    public void BeginGesture(Point point, double handleTolerance = 8)
    {
        EndAdjustment();
        _dragOrigin = point;
        _gestureUndoPushed = false;
        _eraserChanged = false;
        _activeEraserMasks.Clear();

        // A crop session owns the pointer until it is committed or cancelled.
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

        // An annotation under the pointer is grabbed, never drawn over.
        if (GrabExisting(point, handleTolerance)) return;

        Selected = null;
        var history = (_undo.ToArray(), _redo.ToArray());
        PushUndo();
        _creationHistory = history;
        _draft = new Annotation
        {
            Tool = ActiveTool,
            Start = point,
            End = point,
            ColorHex = ColorHex,
            StrokeThickness = ActiveThickness,
            CounterValue = ActiveTool == EditorTool.Counter ? NextCounter : 0,
            CounterRun = _counterRun,
            Fill = ShapeFill,
            FontSize = TextFontSize,
            Style = TextStyle,
        };
        if (_draft.IsStrokeTool) _draft.Points.Add(point);
        if (ActiveTool != EditorTool.Eraser)
        {
            AddAnnotation(_draft);
            _createdUndoOwner = _draft;
        }
        _gesture = GestureKind.Draw;

        // Erase on the press too, or a tap that never moves erases nothing.
        if (ActiveTool == EditorTool.Eraser) _eraserChanged |= ApplyEraserSegment(_draft);

        Notify();
    }

    /// <summary>
    /// Starts a move or resize on an existing object, and reports whether it took the gesture:
    /// handles resize, the interior moves, whatever tool is active.
    ///
    /// Only the select tool reaches an object that is not already selected. A creation tool must
    /// leave the canvas under an existing object drawable, so it grabs one only once it is the
    /// selection - that is what stops a drag on a text box from laying a second box over it.
    /// </summary>
    private bool GrabExisting(Point point, double handleTolerance)
    {
        if (Selected is { } selected && _annotations.Contains(selected))
        {
            if (GetResizeHandleAt(selected, point, handleTolerance) is { } handle)
            {
                _gesture = GestureKind.Resize;
                _resizeHandle = handle;
                _resizeOriginBounds = selected.Bounds;
                return true;
            }

            if (selected.HitTest(point))
            {
                _gesture = GestureKind.Move;
                return true;
            }
        }

        if (ActiveTool != EditorTool.Select) return false;

        Selected = HitTestTopmost(point);
        _gesture = Selected is null ? GestureKind.None : GestureKind.Move;
        Notify();
        return true;
    }

    /// <summary>The frontmost annotation under a point: paint order is list order, so the last
    /// match is the one on top.</summary>
    public Annotation? HitTestTopmost(Point point) => _annotations.LastOrDefault(a => a.HitTest(point));

    /// <summary>Continues the active gesture.</summary>
    public void ContinueGesture(Point point)
    {
        switch (_gesture)
        {
            case GestureKind.Move when Selected is not null:
                var (moveX, moveY) = ClampDeltaToImage(Selected, point.X - _dragOrigin.X, point.Y - _dragOrigin.Y);
                _dragOrigin = point;
                if (moveX == 0 && moveY == 0) break;
                EnsureGestureUndo();
                Selected.Translate(moveX, moveY);
                break;

            case GestureKind.Resize when Selected is not null:
                if (Selected.IsLinear
                    ? point == (_resizeHandle == ResizeHandle.LineStart ? Selected.Start : Selected.End)
                    : ResizedBounds(point) == Selected.Bounds) break;
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
        // Button-up can carry a newer position than the last mouse-move message.
        if (_gesture != GestureKind.Draw || _draft?.End != point) ContinueGesture(point);
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
            if (!_eraserChanged) RestoreCreationHistory();
            else _creationHistory = null;
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
            RemoveAnnotation(_draft);
            if (_createdUndoOwner == _draft) RestoreCreationHistory();
            _createdUndoOwner = null;
        }
        else
        {
            // A text annotation gets a workable editing box even when merely clicked into place.
            if (_draft.Tool == EditorTool.Text) NormalizeTextBounds(_draft);

            // Shapes stay selected for their handles; a finished stroke leaves no box.
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
        foreach (var handle in BoxGeometry.Handles) yield return (handle, BoxGeometry.HandlePosition(bounds, handle));
    }

    private void ApplyResize(Annotation annotation, Point point)
    {
        if (annotation.IsLinear)
        {
            if (_resizeHandle == ResizeHandle.LineStart) annotation.Start = point;
            else annotation.End = point;
            return;
        }

        // Crossing an anchored side normalises through Start/End ordering.
        var resized = ResizedBounds(point);
        annotation.Start = new Point(resized.Left, resized.Top);
        annotation.End = new Point(resized.Right, resized.Bottom);
        annotation.InvalidateGeometry();
    }

    private Rect ResizedBounds(Point point) => BoxGeometry.Resize(
            _resizeOriginBounds,
            _resizeHandle,
            point,
            new Rect(0, 0, ImageWidth, ImageHeight));

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

        // Indexed, not LINQ: this runs on every eraser sample, and the closures allocate per drag move.
        foreach (var stroke in _annotations)
        {
            if (stroke.Tool is not (EditorTool.Pen or EditorTool.Brush)) continue;

            var bounds = stroke.Bounds;
            if (!PathMayTouchBounds(path, bounds, radius + stroke.StrokeThickness / 2)) continue;
            hitNow.Add(stroke);
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

        // Strokes the eraser has left end their mask; collected first, as the dictionary cannot change mid-walk.
        List<Annotation>? left = null;
        foreach (var stroke in _activeEraserMasks.Keys)
            if (!hitNow.Contains(stroke)) (left ??= []).Add(stroke);

        if (left is not null)
            foreach (var stroke in left) _activeEraserMasks.Remove(stroke);

        return changed;
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
                && Math.Min(a.Y, b.Y) - reach <= bounds.Bottom)
                return true;
        }
        return false;
    }

    private static void AppendStrokePoint(Annotation stroke, Point point)
    {
        if (stroke.Points.Count == 0)
        {
            stroke.Points.Add(point);
            stroke.InvalidateGeometry();
            return;
        }

        var previous = stroke.Points[^1];
        var dx = point.X - previous.X;
        var dy = point.Y - previous.Y;
        // Samples inside the same subpixel cost size and render work for nothing.
        if (dx * dx + dy * dy < 0.25) return;

        stroke.Points.Add(point);
        stroke.InvalidateGeometry();
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

    /// <summary>The number the next counter will carry: one past the highest in the current run.
    /// Derived from the annotations rather than kept as a running count, so undo and delete give a
    /// number back instead of skipping it.</summary>
    public int NextCounter
    {
        get
        {
            var max = 0;
            foreach (var a in _annotations)
                if (a.Tool == EditorTool.Counter && a.CounterRun == _counterRun && a.CounterValue > max)
                    max = a.CounterValue;
            return max + 1;
        }
    }

    private int _counterRun;

    /// <summary>Starts numbering again from 1. Counters already placed keep their numbers.</summary>
    public void ResetCounter()
    {
        _counterRun++;
        Notify();
    }
}

namespace NexusShot.Core;

/// <summary>
/// UI-independent editing session state, in image-pixel coordinates. The document owns the
/// annotation list, selection, undo/redo history and the pending crop. The view only supplies
/// pointer positions already mapped into image space.
/// </summary>
public sealed partial class EditorDocument
{
    private enum GestureKind { None, Draw, Move, Resize, CropMove, CropResize }

    private readonly List<Annotation> _annotations = [];
    private sealed record DocumentSnapshot(IReadOnlyList<Annotation> Annotations, Rect? CropBounds, Rect? PendingCrop, long Revision);
    private readonly Stack<DocumentSnapshot> _undo = new();
    private readonly Stack<DocumentSnapshot> _redo = new();
    private const int MaxUndo = 100;

    private Annotation? _draft;
    private Point _dragOrigin;
    private GestureKind _gesture;
    private ResizeHandle _resizeHandle;
    private Rect _resizeOriginBounds;

    // Move and resize snapshot lazily, on the first pointer movement: pushing on the press alone
    // would fill the undo stack with identical states every time the user merely clicks a shape.
    private bool _gestureUndoPushed;
    private bool _eraserChanged;

    /// <summary>The annotation whose creation pushed the newest undo entry, while that entry is
    /// still the newest. Lets a cancelled creation retract its own entry and no other.</summary>
    private Annotation? _createdUndoOwner;
    // A provisional draw may be discarded. Keep stack references (not another deep copy of the
    // document) so cancellation restores redo and the oldest entry at the history limit too.
    private (DocumentSnapshot[] Undo, DocumentSnapshot[] Redo, long Revision)? _creationHistory;
    private bool _adjusting;
    private readonly Dictionary<Annotation, EraserMask> _activeEraserMasks = [];

    public IReadOnlyList<Annotation> Annotations => _annotations;

    /// <summary>
    /// Changes whenever the set of annotations does - added, removed, or replaced wholesale by undo.
    /// Render-side caches key their prune off this, so they only rebuild when an annotation could
    /// actually have gone away.
    ///
    /// Not a count: undo swaps every annotation for a fresh clone with new identities while leaving
    /// the count untouched, and a cache keyed on count would keep the dead geometry.
    /// </summary>
    public long AnnotationGeneration { get; private set; }

    // The only writers of _annotations. Every shape change goes through one of these, so the
    // generation cannot be left behind by a caller that adds or removes the list directly.

    private void AddAnnotation(Annotation annotation)
    {
        _annotations.Add(annotation);
        AnnotationGeneration++;
    }

    private bool RemoveAnnotation(Annotation annotation)
    {
        if (!_annotations.Remove(annotation)) return false;
        AnnotationGeneration++;
        return true;
    }

    private void MoveAnnotation(Annotation annotation, int index)
    {
        _annotations.Remove(annotation);
        _annotations.Insert(index, annotation);
        AnnotationGeneration++;
    }

    private void ReplaceAnnotations(IReadOnlyList<Annotation>? annotations)
    {
        _annotations.Clear();
        if (annotations is not null) _annotations.AddRange(annotations);
        AnnotationGeneration++;
    }

    public EditorTool ActiveTool { get; set; } = EditorTool.Select;
    public string ColorHex { get; set; } = "#FF3B30";
    public double StrokeThickness { get; set; } = 4;
    public double BrushThickness { get; private set; } = 48;
    public double EraserThickness { get; private set; } = 48;

    /// <summary>The tool whose size the size control edits: the selection's, when there is one - the
    /// style bar edits what is selected - and otherwise the active tool's.</summary>
    public EditorTool SizingTool => Selected?.Tool ?? ActiveTool;

    /// <summary>What the size control shows. The counterpart to SetStrokeThickness: the two must route
    /// identically, or the control shows one value and writes another.</summary>
    public double ActiveThickness => Selected switch
    {
        { Tool: EditorTool.Text } text => TextRuns.SizeAt(text.Format, text.Runs, text.Text.Length, 0, text.Text.Length),
        { } selected => selected.StrokeThickness,
        _ => ActiveTool switch
        {
            EditorTool.Brush => BrushThickness,
            EditorTool.Eraser => EraserThickness,
            EditorTool.Text => TextFontSize,
            _ => StrokeThickness,
        },
    };

    /// <summary>Formatting applied to newly placed text annotations.</summary>
    public double TextFontSize { get; set; } = 20;

    /// <summary>The fill new rectangles and ellipses are drawn with.</summary>
    public ShapeFill ShapeFill { get; private set; }

    /// <summary>Whether new lines, arrows and outlines are dashed.</summary>
    public bool DashedLines { get; private set; }
    public TextStyle TextStyle { get; set; }
    /// <summary>The selected annotation. Changing it closes any editor on a different annotation,
    /// so the two cannot drift apart.</summary>
    public Annotation? Selected
    {
        get;
        private set
        {
            if (ReferenceEquals(field, value)) return;
            EndAdjustment();
            field = value;
            if (!ReferenceEquals(EditingText, value)) EditingText = null;
        }
    }

    /// <summary>
    /// The text annotation whose inline editor is open, or null - always either null or the
    /// selection. The view used to own this alone, so render, input and the cursor each reached
    /// their own conclusion about whether a box was open.
    /// </summary>
    public Annotation? EditingText { get; private set; }

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
    /// Names the content state: every undoable edit moves to a fresh revision, and undo and redo
    /// return to the revision their snapshot recorded. Saving remembers one, so undoing back to the
    /// saved state reads as clean, and an edit made while a save was writing still reads as dirty.
    /// </summary>
    public long Revision { get; private set; }
    private long _nextRevision = 1;
    private long _savedRevision;

    /// <summary>A crop frame dragged away from the committed crop is an edit too: Save applies it.</summary>
    public bool HasUnsavedChanges => Revision != _savedRevision
        || (PendingCrop is { } crop && crop != (CropBounds ?? new Rect(0, 0, ImageWidth, ImageHeight)));

    /// <summary>A worker owns this independent copy. No UI selection, events or undo history
    /// crosses the thread boundary. Ends any adjustment, so an edit made after the snapshot - a key
    /// nudge has no mouse-up to end it - gets its own revision and cannot pass as already saved.</summary>
    public EditorDocument CreateExportSnapshot()
    {
        EndAdjustment();
        var copy = new EditorDocument();
        copy.SetImageSize(ImageWidth, ImageHeight);
        copy.ReplaceAnnotations(_annotations.Select(annotation => annotation.Clone()).ToArray());
        copy.CropBounds = PendingCrop ?? CropBounds;
        return copy;
    }

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
        or EditorTool.Spotlight or EditorTool.Text or EditorTool.Redact;

    public void SetImageSize(double width, double height)
    {
        ImageWidth = width;
        ImageHeight = height;
    }

    /// <summary>The colour for new annotations and the selection. <paramref name="isAdjusting"/> marks
    /// the continuous steps of a drag through the picker, which share one undo entry.</summary>
    public void SetColor(string colorHex, bool isAdjusting = false)
    {
        if (!isAdjusting) EndAdjustment();
        ColorHex = colorHex;
        if (Selected is null || Selected.ColorHex == colorHex) return;
        PrepareAdjustUndo(isAdjusting);
        Selected.ColorHex = colorHex;
        Notify();
    }

    /// <summary>The fill for new shapes and, when a rectangle or ellipse is selected, for that shape
    /// as one undo step.</summary>
    public void SetFill(ShapeFill fill)
    {
        ShapeFill = fill;
        if (Selected is not { IsFillable: true } shape || shape.Fill == fill) return;
        PushUndo();
        shape.Fill = fill;
        Notify();
    }

    /// <summary>Dashed or solid, for new lines and outlines and, when one is selected, for it as one
    /// undo step.</summary>
    public void SetDashed(bool dashed)
    {
        DashedLines = dashed;
        if (Selected is not { IsDashable: true } shape || shape.Dashed == dashed) return;
        PushUndo();
        shape.Dashed = dashed;
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
        if (!isAdjusting) EndAdjustment();
        switch (SizingTool)
        {
            case EditorTool.Brush:
                BrushThickness = thickness;
                break;
            case EditorTool.Eraser:
                EraserThickness = thickness;
                return;
            case EditorTool.Text:
                SetFontSize(thickness, isAdjusting);
                return;
            default:
                StrokeThickness = thickness;
                break;
        }

        if (Selected is null || Selected.StrokeThickness == thickness) return;
        PrepareAdjustUndo(isAdjusting);
        Selected.StrokeThickness = thickness;
        Notify();
    }

    /// <summary>The font size for new text, and for all of the selected box. The box grows with the
    /// font, or the larger glyphs are clipped by bounds sized for the old one.</summary>
    private void SetFontSize(double size, bool isAdjusting)
    {
        TextFontSize = size;
        if (Selected is not { Tool: EditorTool.Text } target) return;

        var length = target.Text.Length;
        var (format, runs) = TextRuns.Resize(target.Format, target.Runs, length, 0, length, size);
        if (format == target.Format && runs.AsSpan().SequenceEqual(target.Runs)) return;

        PrepareAdjustUndo(isAdjusting);
        (target.Format, target.Runs) = (format, runs);
        NormalizeTextBounds(target);
        Notify();
    }

    private void PrepareAdjustUndo(bool isAdjusting)
    {
        if (!_adjusting) PushUndo();
        _adjusting = isAdjusting;
    }

    /// <summary>Mouse-up ends one continuous edit - a slider or picker drag; the next gets its own undo
    /// snapshot.</summary>
    public void EndAdjustment() => _adjusting = false;

    /// <summary>Commits an inline editor's text, formatting and final box (clamped to the image) as
    /// one undo step - the single write-back for a text edit. Without <paramref name="format"/> the
    /// box keeps its base format, and its runs only while the length they cover is unchanged. The box
    /// grows to fit the largest size in it.</summary>
    public void SetTextContent(Annotation annotation, string text, Rect bounds, (TextFormat Format, TextRun[] Runs)? format = null)
    {
        var (baseFormat, runs) = format ?? (annotation.Format, text.Length == annotation.Text.Length ? annotation.Runs : []);
        var clamped = ClampTextBounds(bounds);
        var unchanged = annotation.Text == text && clamped == annotation.Bounds
            && annotation.Format == baseFormat && annotation.Runs.AsSpan().SequenceEqual(runs);
        if (unchanged) return;

        PushUndo();
        annotation.Text = text;
        annotation.Format = baseFormat;
        annotation.Runs = runs;
        annotation.Start = new Point(clamped.X, clamped.Y);
        annotation.End = new Point(clamped.Right, clamped.Bottom);
        NormalizeTextBounds(annotation);
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

    /// <summary>Sets or clears a style on the defaults for new text and, when a text box is selected,
    /// over all of its text as one undo step.</summary>
    public void SetTextStyle(TextStyle flag, bool on)
    {
        TextStyle = on ? TextStyle | flag : TextStyle & ~flag;
        if (Selected is not { Tool: EditorTool.Text } text) return;
        PushUndo();
        (text.Format, text.Runs) = TextRuns.Apply(text.Format, text.Runs, text.Text.Length, 0, text.Text.Length, flag, on);
        Notify();
    }

    /// <summary>
    /// Removes a just-created annotation as if it was never placed. Used when text entry is
    /// dismissed empty.
    ///
    /// The creation's undo entry goes with it only when nothing has been pushed since: once another
    /// edit is on the stack, the newest entry belongs to that edit, and popping it would discard
    /// unrelated history.
    /// </summary>
    public void CancelAnnotation(Annotation annotation)
    {
        if (!RemoveAnnotation(annotation)) return;
        if (_createdUndoOwner == annotation) RestoreCreationHistory();
        _creationHistory = null;
        _createdUndoOwner = null;
        if (ReferenceEquals(EditingText, annotation)) EditingText = null;
        if (Selected == annotation) Selected = null;
        Notify();
    }

    /// <summary>Selects an annotation programmatically, without a pointer gesture.</summary>
    public void SelectAnnotation(Annotation? annotation)
    {
        Selected = annotation;
        Notify();
    }

    /// <summary>Opens a text annotation for editing, selecting it: an open box is always the
    /// selection, so its grips stay live while it is typed into.</summary>
    public void BeginTextEdit(Annotation annotation)
    {
        if (annotation.Tool != EditorTool.Text || !_annotations.Contains(annotation)) return;
        Selected = annotation;
        EditingText = annotation;
        Notify();
    }

    /// <summary>Closes the open editor, leaving the annotation selected.</summary>
    public void EndTextEdit()
    {
        if (EditingText is null) return;
        EditingText = null;
        Notify();
    }

    /// <summary>Covers each area with a redaction, as one undo step: what Find sensitive text found
    /// is accepted or undone together.</summary>
    public void AddRedactions(IReadOnlyList<Rect> areas)
    {
        var image = new Rect(0, 0, ImageWidth, ImageHeight);
        var boxes = areas.Select(area => area.Intersect(image)).Where(box => !box.IsEmpty).ToList();
        if (boxes.Count == 0) return;
        PushUndo();
        foreach (var box in boxes)
        {
            AddAnnotation(new Annotation
            {
                Tool = EditorTool.Redact,
                Start = new Point(box.X, box.Y),
                End = new Point(box.Right, box.Bottom),
                ColorHex = Annotation.RedactColor,
            });
        }
        Notify();
    }

    /// <summary>A copy of the selection, offset so it is seen to appear, and selected in its place.</summary>
    public void DuplicateSelected()
    {
        if (Selected is not { } original || EditingText is not null) return;
        PushUndo();
        var copy = original.Duplicate();
        var (dx, dy) = ClampDeltaToImage(copy, DuplicateOffset, DuplicateOffset);
        copy.Translate(dx, dy);
        AddAnnotation(copy);
        Selected = copy;
        Notify();
    }

    private const double DuplicateOffset = 16;

    /// <summary>Moves the selection by a key press, kept inside the image. A run of nudges is one undo
    /// step, ended as any adjustment is.</summary>
    public void NudgeSelected(double dx, double dy)
    {
        if (Selected is not { } shape || EditingText is not null) return;
        var (x, y) = ClampDeltaToImage(shape, dx, dy);
        if (x == 0 && y == 0) return;
        PrepareAdjustUndo(isAdjusting: true);
        shape.Translate(x, y);
        Notify();
    }

    /// <summary>Whether the selection shares pixels with another annotation: the only time its place
    /// in the paint order shows.</summary>
    public bool SelectionOverlaps => Selected is { } shape
        && _annotations.Any(other => !ReferenceEquals(other, shape) && !other.Bounds.Intersect(shape.Bounds).IsEmpty);

    /// <summary>Moves the selection up or down the paint order, which is the list order.</summary>
    public void Reorder(LayerMove move)
    {
        if (Selected is not { } shape) return;
        var index = _annotations.IndexOf(shape);
        var target = move switch
        {
            LayerMove.Forward => index + 1,
            LayerMove.Backward => index - 1,
            LayerMove.Front => _annotations.Count - 1,
            _ => 0,
        };
        target = Math.Clamp(target, 0, _annotations.Count - 1);
        if (target == index) return;
        PushUndo();
        MoveAnnotation(shape, target);
        Notify();
    }

    public void DeleteSelected()
    {
        if (Selected is null) return;
        PushUndo();
        RemoveAnnotation(Selected);
        Selected = null;
        Notify();
    }

    /// <summary>Records <paramref name="revision"/> - the one the export was taken from - as what is on
    /// disk. Annotations, crop and history stay: the editor keeps the original pixels, so a save can
    /// be undone and saved again.</summary>
    public void MarkSaved(long revision)
    {
        _savedRevision = revision;
        Notify();
    }

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
}

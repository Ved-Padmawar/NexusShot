using ToolCursor = DirectN.Extensions.Utilities.Cursor;
using NexusShot.Core;
using NexusShot.Render;
using NexusShot.Platform;

namespace NexusShot.Views;

/// <summary>
/// The editor's message handling: pointer, keyboard, and the text-box lifecycle they drive.
///
/// Every path here mutates the document and invalidates. The gesture state that decides which tool
/// a drag belongs to lives in Core, so this only routes.
/// </summary>
public sealed partial class EditorWindow
{
    //
    // Input mutates the document and invalidates. There is no per-event render, no buffering of
    // samples, and no frame-batching machinery: WM_PAINT is already coalesced to the display rate,
    // so a burst of pointer messages collapses into one frame on its own.

    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmKeyDown = 0x0100;
    private const uint WmChar = 0x0102;
    private const uint WmSetCursor = 0x0020;
    private const uint WmTimer = 0x0113;

    /// <summary>Drives the caret blink and the toast fade.</summary>
    private const nuint AnimationTimerId = 1;
    /// <summary>Paced to the caret's 530 ms blink, not to the frame: nothing here animates at
    /// display rate.</summary>
    private const uint AnimationIntervalMs = 120;
    private bool _animating;

    /// <summary>Starts or stops the repaint tick. Idempotent - called every frame.</summary>
    private void SetAnimating(bool animating)
    {
        if (animating == _animating) return;
        _animating = animating;

        if (animating)
            WindowInterop.SetTimer(Handle, AnimationTimerId, AnimationIntervalMs, IntPtr.Zero);
        else WindowInterop.KillTimer(Handle, AnimationTimerId);
    }

    /// <summary>Runs <paramref name="work"/> once the frame has finished - the chrome reports presses
    /// during Render, and resizing a render target mid-frame fails with D2DERR_WRONG_STATE.</summary>
    private void Post(Action work) => _dispatch.Post(work);

    private UiThreadDispatch _dispatch = null!;

    /// <summary>The WM_SETCURSOR hit-test that means the pointer is over the client area - the part
    /// the editor owns. Anything else is frame or caption, and belongs to DefWindowProc.</summary>
    private const int HTCLIENT = 1;

    private static readonly LRESULT Handled = new() { Value = 0 };

    protected override LRESULT? WindowProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case UiThreadDispatch.Message:
                _dispatch.Drain();
                return Handled;

            case WmTimer when (nuint)wParam.Value == AnimationTimerId:
                Invalidate();
                return Handled;

            case WmLButtonDown:
                OnPointerPressed(ClientPoint(lParam));
                return Handled;

            case WmMouseMove:
                OnPointerMoved(ClientPoint(lParam), ((ulong)wParam.Value & 0x0001) != 0);
                return Handled;

            case WmLButtonUp:
                OnPointerReleased(ClientPoint(lParam));
                return Handled;

            case WmSetCursor:
                // Only the client area is ours; DefWindowProc owns the frame's resize arrows.
                if ((lParam.Value.ToInt64() & 0xFFFF) != HTCLIENT) break;

                // The live pointer, not _clientPointer: WM_SETCURSOR arrives ahead of the
                // WM_MOUSEMOVE that would refresh it, so on re-entry from the toolbar the field
                // still holds the old outside-the-canvas coordinate.
                if (SetToolCursor(PointerNow())) return new LRESULT { Value = 1 };
                break;

            case WmKeyDown:
                if (OnKeyDown((VIRTUAL_KEY)(ulong)wParam.Value)) return Handled;
                break;

            case WmChar:
                // The typed character, already mapped through the keyboard layout - which is what a
                // text box wants, rather than a raw virtual key code.
                if (OnChar((char)(ulong)wParam.Value)) return Handled;
                break;

            case SystemTheme.WM_SETTINGCHANGE:
                if (SystemTheme.IsColorSetChange(msg, (IntPtr)lParam.Value.ToInt64())
                    && _theme == AppTheme.System)
                    SetTheme(_theme);
                break;
        }
        return base.WindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Windows draws the cursor, so it never trails the pointer the way an app-drawn one does.
    ///
    /// Inside the client area this always sets a cursor and reports true: falling through would let
    /// DefWindowProc install the class arrow over the tool's own cursor.
    /// </summary>
    private bool SetToolCursor(Point client)
    {
        if (_image is null) return false;

        if (!InCanvas(client))
        {
            Functions.SetCursor(new HCURSOR { Value = ToolCursors.Arrow });
            return true;
        }

        // A grip under the pointer says what dragging it will do, whatever tool is active.
        if (_hoverHandle is { } handle)
        {
            Functions.SetCursor(new HCURSOR { Value = ToolCursors.Resize(handle) });
            return true;
        }

        var image = ToImage((int)client.X, (int)client.Y);

        // A crop session owns the pointer, so the cursor answers only to the frame: the interior
        // moves it, and everything outside is inert.
        if (_document.PendingCrop is { } crop)
        {
            Functions.SetCursor(new HCURSOR
            {
                Value = crop.Contains(image) ? ToolCursors.Move : ToolCursors.Arrow,
            });
            return true;
        }

        // The cursor reads off the same split the press uses, so it cannot promise a move the
        // input path will not perform.
        if (_text.Editor is { } editing && editing.Annotation.HitTest(image))
        {
            Functions.SetCursor(new HCURSOR
            {
                Value = TextInterior(editing.Annotation).Contains(image)
                    ? ToolCursors.Text
                    : ToolCursors.Move,
            });
            return true;
        }

        // The selection's interior drags it, and its grips have already been answered above.
        if (_document.Selected is { } selected && selected.HitTest(image))
        {
            Functions.SetCursor(new HCURSOR { Value = ToolCursors.Move });
            return true;
        }

        var cursor = _document.ActiveTool switch
        {
            EditorTool.Select => ToolCursors.Arrow,
            EditorTool.Pen => ToolCursors.Pencil(),

            // The brush and eraser show their true footprint at its on-screen size. The brush is
            // filled with the paint colour, so what you see is what the stroke will lay down; the
            // eraser stays faint, because it removes rather than adds.
            EditorTool.Brush => ToolCursors.Circle(
                PaintStrokeGeometry.Diameter(_document.ActiveThickness) * _scale,
                Palette.Parse(_document.ColorHex)),

            EditorTool.Eraser => ToolCursors.Circle(
                PaintStrokeGeometry.Diameter(_document.ActiveThickness) * _scale,
                Rgba.White.WithAlpha(28)),

            _ => ToolCursors.Cross,
        };

        Functions.SetCursor(new HCURSOR { Value = cursor });
        return true;
    }

    private static (int X, int Y) ClientPoint(LPARAM lParam)
    {
        var value = lParam.Value.ToInt64();
        return ((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }

    /// <summary>Where a press inside an open box types rather than grabbing the box.</summary>
    private Rect TextInterior(Annotation annotation) =>
        BoxGeometry.Interior(annotation.Bounds, HandleTolerance);

    /// <summary>True when a press takes hold of the box itself - a grip, or the move band - rather
    /// than reaching the text inside it.</summary>
    private bool GrabsBox(Annotation annotation, Point point) =>
        BoxGeometry.GrabsBox(annotation.Bounds, point, HandleTolerance);

    private void OnPointerPressed((int X, int Y) client)
    {
        _clientPointer = new Point(client.X, client.Y);
        _pointerDown = true;

        if (_image is null)
        { Invalidate(); return; }

        // A click inside the open box moves the caret rather than committing and reopening, which
        // would reselect the whole string. Grips are excluded: they sit on the bounds, so testing
        // the box alone swallowed every resize.
        if (_text.Editor is { } open
            && InCanvas(_clientPointer)
            && !(_ui?.WantsPointer ?? false)
            && _document.GetResizeHandleAt(open.Annotation, ToImage(client.X, client.Y), HandleTolerance) is null
            && TextInterior(open.Annotation).Contains(ToImage(client.X, client.Y)))
        {
            PlaceCaret(open, ToImage(client.X, client.Y));
            _caretDragging = true;
            Functions.SetCapture(Handle);
            Invalidate();
            return;
        }

        // The chrome gets first refusal, and leaves an open text box alone: reaching for the font
        // slider is an adjustment to the box, not a click away from it, and committing here would
        // cancel an empty one outright and delete the annotation.
        if (!InCanvas(_clientPointer)
            || (_ui?.WantsPointer ?? false)
            || (_chrome?.PopupOpen ?? false))
        {
            Invalidate();
            return;
        }

        var point = ToImage(client.X, client.Y);

        // A press that grabs the open box keeps it, so the gesture below moves or resizes it. Only
        // a press away from it ends the edit and discards a box that was never typed into.
        var wasEditing = _text.Annotation;
        _text.End(commit: wasEditing is not null && GrabsBox(wasEditing, point));

        // Typing opens on a press inside a box that is already selected and not being grabbed. The
        // press that merely selects a box stays free to drag it.
        if (_document.Selected is { Tool: EditorTool.Text } text
            && !ReferenceEquals(text, wasEditing)
            && !GrabsBox(text, point)
            && text.HitTest(point))
        {
            BeginTextEdit(text);
            Invalidate();
            return;
        }

        // The text tool reaches an unselected box by selecting it first, so the press after this
        // one edits that box rather than drawing another over it.
        if (_document.ActiveTool == EditorTool.Text
            && _document.Selected is null
            && _document.HitTestTopmost(point) is { Tool: EditorTool.Text } unselected)
        {
            _document.SelectAnnotation(unselected);
        }

        Functions.SetCapture(Handle);
        _dragging = true;
        _document.BeginGesture(point, HandleTolerance);
        Invalidate();
    }

    private void OnPointerMoved((int X, int Y) client, bool leftDown)
    {
        _clientPointer = new Point(client.X, client.Y);
        _pointerDown = leftDown;

        if (_image is null)
        { Invalidate(); return; }
        var point = ToImage(client.X, client.Y);

        if (_caretDragging && leftDown && _text.Editor is { } selecting)
        {
            PlaceCaret(selecting, point, extend: true);
            Invalidate();
            return;
        }

        // A drag mutates the document and asks for a repaint. That is the whole hot path: no
        // element scans, no sample buffering, no manual frame batching. WM_PAINT is already
        // coalesced to the display rate, so a burst of moves collapses into one frame by itself.
        if (_dragging && leftDown)
        {
            _document.ContinueGesture(point);
        }
        else
        {
            // Hover feedback only when not dragging: mid-drag the handle is already committed.
            _hoverHandle = !InCanvas(_clientPointer) ? null
                : _document.PendingCrop is not null
                    ? _document.GetCropHandleAt(point, HandleTolerance)
                    : _document.Selected is { } selected
                        ? _document.GetResizeHandleAt(selected, point, HandleTolerance)
                        : null;
        }


        // The chrome is immediate: hover states only update when something repaints, so every move
        // invalidates. A frame is ~1 ms, and Windows coalesces WM_PAINT, so this is not a hot loop.
        Invalidate();
    }

    private void OnPointerReleased((int X, int Y) client)
    {
        _clientPointer = new Point(client.X, client.Y);
        _pointerDown = false;

        if (_caretDragging)
        {
            _caretDragging = false;
            Functions.ReleaseCapture();
            Invalidate();
            return;
        }

        if (_dragging && _image is not null)
        {
            _dragging = false;
            Functions.ReleaseCapture();

            // Read before EndGesture clears the draft: only a brand-new box opens for typing, or
            // ending a move would reopen the editor over the box just dragged.
            var created = _document.IsDrawGestureActive;
            _document.EndGesture(ToImage(client.X, client.Y));

            if (created && _document.Selected is { Tool: EditorTool.Text } placed && placed.Text.Length == 0)
                BeginTextEdit(placed);
        }
        Invalidate();
    }

    // ============================  TEXT  ============================

    /// <summary>Undo, wherever the user is: an open box unwinds its own typing first, then undo goes
    /// on to the document and takes the box with it.</summary>
    private void Undo()
    {
        if (!_text.Undo())
        {
            // Dropped, not committed: it is about to be undone away.
            _text.Abandon();
            _document.Undo();
        }
        Invalidate();
    }

    private void Redo()
    {
        if (!_text.Redo()) _document.Redo();
        Invalidate();
    }

    /// <summary>Drops the caret at an image-space point, extending the selection while dragging.</summary>
    private void PlaceCaret(TextEditor editor, Point point, bool extend = false)
    {
        if (_renderer is null) return;
        editor.MoveTo(_renderer.HitTestCaret(editor.Annotation, editor.Text, point), extend);
    }

    /// <summary>Opens the inline box over an annotation.</summary>
    private void BeginTextEdit(Annotation annotation)
    {
        if (_image is null) return;
        _text.Begin(annotation);
        Invalidate();
    }

    /// <summary>A printable character while a box is open. Control characters arrive here too, and
    /// are the business of WM_KEYDOWN.</summary>
    private bool OnChar(char character)
    {
        if (_chrome is { TextFieldFocused: true })
        {
            _chrome.HandleKey(_document, character, backspace: false, enter: false, escape: false);
            Invalidate();
            return true;
        }

        if (!_text.HandleChar(character)) return false;
        Invalidate();
        return true;
    }

    /// <summary>Editing keys for an open box, routed to the box; undo and redo come back here
    /// because they reach past it into the document.</summary>
    private bool OnTextKey(VIRTUAL_KEY key)
    {
        var control = (Functions.GetKeyState((int)VIRTUAL_KEY.VK_CONTROL) & 0x8000) != 0;
        var shift = (Functions.GetKeyState((int)VIRTUAL_KEY.VK_SHIFT) & 0x8000) != 0;

        switch (_text.HandleKey(key, control, shift))
        {
            case TextKeyResult.Undo:
                Undo();
                return true;
            case TextKeyResult.Redo:
                Redo();
                return true;
            case TextKeyResult.Handled:
                Invalidate();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Writes the box's text back and closes it, discarding one that was never typed into.</summary>
    private void CommitText() => _text.End(commit: false);

    /// <summary>True inside the image well - the region the canvas owns, between the bars.</summary>
    private bool InCanvas(Point client) => CanvasWell().Contains(client);

    private bool OnKeyDown(VIRTUAL_KEY key)
    {
        // A focused colour box owns the keyboard: the printable keys arrive as WM_CHAR, and only the
        // editing keys are handled here. Otherwise typing a hex digit would drive the toolbar.
        if (_chrome is not null && _chrome.TextFieldFocused)
        {
            var handled = _chrome.HandleKey(
                _document, '\0',
                backspace: key == VIRTUAL_KEY.VK_BACK,
                enter: key == VIRTUAL_KEY.VK_RETURN,
                escape: key == VIRTUAL_KEY.VK_ESCAPE);

            if (handled) Invalidate();
            return handled;
        }

        // An open text box owns the keyboard: its keystrokes are text, not shortcuts, or typing
        // "rectangle" would switch tools eight times.
        if (_text.IsOpen && OnTextKey(key)) return true;

        var control = (Functions.GetKeyState((int)VIRTUAL_KEY.VK_CONTROL) & 0x8000) != 0;

        if (control)
        {
            switch (key)
            {
                case VIRTUAL_KEY.VK_Z:
                    Undo();
                    return true;
                case VIRTUAL_KEY.VK_Y:
                    Redo();
                    return true;
            }
            return false;
        }

        switch (key)
        {
            case VIRTUAL_KEY.VK_DELETE:
            case VIRTUAL_KEY.VK_BACK:
                _document.DeleteSelected();
                return true;

            case VIRTUAL_KEY.VK_ESCAPE:
                if (_text.IsOpen) CommitText();
                else if (_document.IsCropSessionActive) _document.CancelCropSession();
                else _document.SelectAnnotation(null);
                Invalidate();
                return true;

            case VIRTUAL_KEY.VK_RETURN:
                // Enter applies the crop frame without writing the file, so it can be adjusted
                // against the cropped result before saving.
                if (_document.IsCropSessionActive)
                {
                    _document.CommitCrop();
                    SelectTool(EditorTool.Select);
                    return true;
                }
                break;

            // B is Blur, not Brush; P is Pixelate, not Pen.
            case VIRTUAL_KEY.VK_V:
                return SelectTool(EditorTool.Select);
            case VIRTUAL_KEY.VK_R:
                return SelectTool(EditorTool.Rectangle);
            case VIRTUAL_KEY.VK_E:
                return SelectTool(EditorTool.Ellipse);
            case VIRTUAL_KEY.VK_A:
                return SelectTool(EditorTool.Arrow);
            case VIRTUAL_KEY.VK_L:
                return SelectTool(EditorTool.Line);
            case VIRTUAL_KEY.VK_D:
                return SelectTool(EditorTool.Pen);
            case VIRTUAL_KEY.VK_M:
                return SelectTool(EditorTool.Brush);
            case VIRTUAL_KEY.VK_X:
                return SelectTool(EditorTool.Eraser);
            case VIRTUAL_KEY.VK_T:
                return SelectTool(EditorTool.Text);
            case VIRTUAL_KEY.VK_N:
                return SelectTool(EditorTool.Counter);
            case VIRTUAL_KEY.VK_H:
                return SelectTool(EditorTool.Highlight);
            case VIRTUAL_KEY.VK_B:
                return SelectTool(EditorTool.Blur);
            case VIRTUAL_KEY.VK_P:
                return SelectTool(EditorTool.Pixelate);
            case VIRTUAL_KEY.VK_S:
                return SelectTool(EditorTool.Spotlight);
            case VIRTUAL_KEY.VK_C:
                return SelectTool(EditorTool.Crop);

            case VIRTUAL_KEY.VK_1:
                _fitToViewport = !_fitToViewport;
                Invalidate();
                return true;
        }
        return false;
    }

    private bool SelectTool(EditorTool tool)
    {
        // Leaving the tool ends the edit, or the box would stay open under a tool that cannot type.
        CommitText();
        if (tool != EditorTool.Crop) _document.CancelCropSession();
        _document.ActiveTool = tool;
        if (tool == EditorTool.Crop && _image is not null) _document.BeginCropSession();
        RefreshCursor();
        Invalidate();
        return true;
    }

    /// <summary>WM_SETCURSOR only arrives on a mouse move, so a size or colour change on the toolbar
    /// would otherwise leave the ring at its old diameter until you jiggled the mouse.</summary>
    private void RefreshCursor()
    {
        var pointer = PointerNow();
        if (InCanvas(pointer)) SetToolCursor(pointer);
    }

    /// <summary>The pointer's current position in client pixels, straight from Windows.</summary>
    private Point PointerNow()
    {
        if (!Functions.GetCursorPos(out var point)) return _clientPointer;
        if (!Functions.ScreenToClient(new HWND { Value = Handle }, ref point)) return _clientPointer;
        return new Point(point.x, point.y);
    }

    protected override void Dispose(bool disposing)
    {
        // Idempotent: OnDestroyed already released these if the window was closed normally.
        ReleaseResources();
        base.Dispose(disposing);
    }
}

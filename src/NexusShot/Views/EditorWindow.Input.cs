using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The editor's message handling: pointer, wheel, keyboard, and the text-box lifecycle they drive.
///
/// Every path here mutates the document and invalidates. The gesture state that decides which tool
/// a drag belongs to lives in Core, so this only routes.
/// </summary>
public sealed partial class EditorWindow
{
    // Input mutates the document and invalidates. There is no per-event render and no buffering of
    // samples: WM_PAINT is already coalesced to the display rate, so a burst of pointer messages
    // collapses into one frame on its own.

    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmMouseHWheel = 0x020E;
    private const uint WmKeyDown = 0x0100;
    private const uint WmChar = 0x0102;
    private const uint WmSetCursor = 0x0020;

    /// <summary>One wheel notch; touchpads send fractions of it, applied as they arrive.</summary>
    private const double WheelDelta = 120;

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
        if (msg == 0x0010 && !RequestClose()) return Handled; // WM_CLOSE
        // Repaints and caption messages remain responsive; editing waits for the save result.
        if ((_fileBusy || _confirmingClose)
            && msg is WmLButtonDown or WmLButtonUp or WmMouseMove or WmKeyDown or WmChar or WmMouseWheel)
            return Handled;
        switch (msg)
        {
            case UiThreadDispatch.Message:
                _dispatch.Drain();
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

            case WmMouseWheel:
            case WmMouseHWheel:
                OnWheel((short)(((ulong)wParam.Value >> 16) & 0xFFFF), horizontal: msg == WmMouseHWheel,
                    control: ((ulong)wParam.Value & 0x0008) != 0, shift: ((ulong)wParam.Value & 0x0004) != 0);
                return Handled;

            case WmSetCursor:
                // Only the client area is ours; DefWindowProc owns the frame's resize arrows.
                if ((lParam.Value.ToInt64() & 0xFFFF) != HTCLIENT) break;

                // The live pointer: WM_SETCURSOR arrives before the WM_MOUSEMOVE that updates it.
                if (SetToolCursor(PointerNow() ?? _clientPointer)) return new LRESULT { Value = 1 };
                break;

            case WmKeyDown:
                if (OnKeyDown((VIRTUAL_KEY)(ulong)wParam.Value)) return Handled;
                break;

            case WmChar:
                // The typed character, already mapped through the keyboard layout.
                if (OnChar((char)(ulong)wParam.Value)) return Handled;
                break;

            case SystemTheme.WM_SETTINGCHANGE:
                if (SystemTheme.IsColorSetChange(msg, (IntPtr)lParam.Value.ToInt64())
                    && _settings.Theme == AppTheme.System)
                    Retheme();
                break;
        }
        return base.WindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Ctrl+wheel zooms about the pointer; the wheel alone scrolls an image larger than the stage,
    /// Shift turning it sideways. A fitted image does not move, so the wheel does nothing then.
    /// </summary>
    private void OnWheel(short delta, bool horizontal, bool control, bool shift)
    {
        if (_image is null) return;

        if (control)
        {
            ZoomBy(Math.Pow(Viewport.Step, delta / WheelDelta), PointerNow() ?? _clientPointer);
            return;
        }

        var distance = delta / WheelDelta * 60 * DpiScale;
        if (horizontal) _viewport.PanBy(-distance, 0);
        else if (shift) _viewport.PanBy(distance, 0);
        else _viewport.PanBy(0, distance);
        Invalidate();
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
            Functions.SetCursor(new HCURSOR { Value = SystemCursor(_ui?.CursorAt(client) ?? PointerCursor.Arrow) });
            return true;
        }

        // A grip under the pointer says what dragging it will do, whatever tool is active.
        if (_hoverHandle is { } handle)
        {
            Functions.SetCursor(new HCURSOR { Value = ToolCursors.Resize(handle) });
            return true;
        }

        var image = ToImage((int)client.X, (int)client.Y);

        // A crop session owns the pointer, so only the frame sets the cursor.
        if (_document.PendingCrop is { } crop)
        {
            Functions.SetCursor(new HCURSOR
            {
                Value = crop.Contains(image) ? ToolCursors.Move : ToolCursors.Arrow,
            });
            return true;
        }

        // The same split the press uses, so the cursor never promises a move input will not make.
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

            // The true on-screen footprint: the brush in its paint colour, the eraser faint.
            EditorTool.Brush => ToolCursors.Circle(
                PaintStrokeGeometry.Diameter(_document.BrushThickness) * _scale,
                Palette.Parse(_document.ColorHex)),

            EditorTool.Eraser => ToolCursors.Circle(
                PaintStrokeGeometry.Diameter(_document.EraserThickness) * _scale,
                Rgba.White.WithAlpha(28)),

            EditorTool.Blur or EditorTool.Pixelate => ToolCursors.Circle(
                PaintStrokeGeometry.EffectRadius(_document.ActiveThickness) * 2 * _scale,
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

    /// <summary>True where the canvas owns the pointer: the stage, anywhere the chrome is not.</summary>
    private bool InCanvas(Point client) => !(_chrome?.Covers(client) ?? true);

    private void OnPointerPressed((int X, int Y) client)
    {
        _clientPointer = new Point(client.X, client.Y);
        _pointerDown = true;

        if (_image is null)
        { Invalidate(); return; }

        // A click inside the open box moves the caret; grips are excluded so resizing still works.
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

        // The chrome gets first refusal; an open picker swallows the press that closes it.
        if (!InCanvas(_clientPointer)
            || (_ui?.WantsPointer ?? false)
            || (_chrome?.PickerOpen ?? false))
        {
            // Captured, so a slider or picker drag released outside the window still ends.
            Functions.SetCapture(Handle);
            _chromeCaptured = true;
            Invalidate();
            return;
        }

        var point = ToImage(client.X, client.Y);

        // Grabbing the open box keeps it for the gesture; pressing away ends the edit.
        var wasEditing = _text.Annotation;
        _text.End(commit: wasEditing is not null && GrabsBox(wasEditing, point));

        // A press inside an already-selected box types; the press that selects it only drags.
        if (_document.Selected is { Tool: EditorTool.Text } text
            && !ReferenceEquals(text, wasEditing)
            && !GrabsBox(text, point)
            && text.HitTest(point))
        {
            BeginTextEdit(text);
            Invalidate();
            return;
        }

        // The text tool selects an unselected box first, so the next press edits it.
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
        // A move never starts a press: the eyedropper closes on its press, leaving the button down.
        _pointerDown &= leftDown;

        if (_image is null)
        { Invalidate(); return; }
        var point = ToImage(client.X, client.Y);

        if (_caretDragging && leftDown && _text.Editor is { } selecting)
        {
            PlaceCaret(selecting, point, extend: true);
            Invalidate();
            return;
        }

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

        // Hover is immediate-mode, so every move repaints; WM_PAINT is coalesced.
        Invalidate();
    }

    private void OnPointerReleased((int X, int Y) client)
    {
        _clientPointer = new Point(client.X, client.Y);
        _pointerDown = false;

        // A slider or picker drag is one undo step, ended by the release.
        _document.EndAdjustment();
        if (_chromeCaptured)
        {
            _chromeCaptured = false;
            Functions.ReleaseCapture();
        }

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

            // Read before EndGesture clears the draft: only a new box opens for typing.
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
        editor.MoveTo(_renderer.HitTestCaret(editor.Annotation, editor.Text, editor.Style, editor.Runs, point), extend);
    }

    /// <summary>Opens the inline box over an annotation.</summary>
    private void BeginTextEdit(Annotation annotation)
    {
        if (_image is null) return;
        _text.Begin(annotation);
        Invalidate();
    }

    /// <summary>A printable character: the focused chrome field's, then an open text box's.</summary>
    private bool OnChar(char character)
    {
        if (_ui is { HasKeyboardFocus: true })
        {
            if (!char.IsControl(character)) _ui.Char(character);
            Invalidate();
            return true;
        }

        if (!_text.HandleChar(character)) return false;
        Invalidate();
        return true;
    }

    /// <summary>Editing keys for an open box, routed to the box; undo, redo and formatting come back
    /// here because they reach past it into the document.</summary>
    private bool OnTextKey(VIRTUAL_KEY key, bool control, bool shift)
    {
        if (control && ToggleTextFormat(key)) return true;

        // Up and Down need the drawn layout, which the box itself does not have.
        if (key is VIRTUAL_KEY.VK_UP or VIRTUAL_KEY.VK_DOWN && _text.Editor is { } editor && _renderer is { } renderer)
        {
            editor.MoveLine(key == VIRTUAL_KEY.VK_UP ? -1 : 1, shift,
                index => renderer.CaretBounds(editor.Annotation, editor.Text, editor.Style, editor.Runs, index),
                point => renderer.HitTestCaret(editor.Annotation, editor.Text, editor.Style, editor.Runs, point));
            Invalidate();
            return true;
        }

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

    /// <summary>Ctrl+B, I and U.</summary>
    private bool ToggleTextFormat(VIRTUAL_KEY key)
    {
        switch (key)
        {
            case VIRTUAL_KEY.VK_B: ToggleTextStyle(TextStyle.Bold); return true;
            case VIRTUAL_KEY.VK_I: ToggleTextStyle(TextStyle.Italic); return true;
            case VIRTUAL_KEY.VK_U: ToggleTextStyle(TextStyle.Underline); return true;
            default: return false;
        }
    }

    /// <summary>What Bold, Italic and Underline show as on: the open box's selection (or all of it),
    /// else the selected box, else the defaults for new text.</summary>
    private TextStyle ActiveTextStyle => _text.Editor is { } editor ? editor.ActiveStyle
        : _document.Selected is { Tool: EditorTool.Text } text
            ? TextRuns.Common(text.Style, text.Runs, text.Text.Length, 0, text.Text.Length)
            : _document.TextStyle;

    /// <summary>The one writer for text formatting. An open box formats its selection, or all of it,
    /// in its own undo; otherwise the selected box and the defaults change together.</summary>
    private void ToggleTextStyle(TextStyle flag)
    {
        if (_text.Editor is { } editor) editor.Toggle(flag);
        else _document.SetTextStyle(flag, !ActiveTextStyle.HasFlag(flag));
        Invalidate();
    }

    /// <summary>Writes the box's text back and closes it, discarding one that was never typed into.</summary>
    private void CommitText() => _text.End(commit: false);

    private bool OnKeyDown(VIRTUAL_KEY key)
    {
        var control = KeyDown(VIRTUAL_KEY.VK_CONTROL);
        var shift = KeyDown(VIRTUAL_KEY.VK_SHIFT);

        // A focused field owns the keyboard, so a hex digit typed into the picker never switches tools.
        if (_ui is { HasKeyboardFocus: true } ui)
        {
            if (control && key == VIRTUAL_KEY.VK_V && ClipboardText.Paste() is { } pasted)
                foreach (var character in pasted.Trim()) ui.Char(character);
            else ui.Key(key, shift, control);
            Invalidate();
            return true;
        }

        if (_chrome is { PickerOpen: true } chrome && !control)
        {
            if (key == VIRTUAL_KEY.VK_ESCAPE)
            {
                chrome.PickerClosed(_settings);
                if (chrome.SettingsChanged) { chrome.SettingsChanged = false; _settingsChanged(); }
                Invalidate();
                return true;
            }
            if (key == VIRTUAL_KEY.VK_I)
            {
                Post(PickFromScreen);
                return true;
            }
            // Letters while the picker is open belong to it, not to the tool shortcuts.
            return true;
        }

        // An open text box owns the keyboard: its keystrokes are text, not shortcuts.
        if (_text.IsOpen && OnTextKey(key, control, shift)) return true;

        if (control)
        {
            switch (key)
            {
                case VIRTUAL_KEY.VK_Z:
                    if (shift) Redo(); else Undo();
                    return true;
                case VIRTUAL_KEY.VK_Y:
                    Redo();
                    return true;
                case VIRTUAL_KEY.VK_S:
                    Post(() => RunFileAction(Save));
                    return true;
                case VIRTUAL_KEY.VK_C:
                    Post(() => RunFileAction(CopyToClipboard));
                    return true;
                case VIRTUAL_KEY.VK_OEM_PLUS:
                case VIRTUAL_KEY.VK_ADD:
                    ZoomBy(Viewport.Step, null);
                    return true;
                case VIRTUAL_KEY.VK_OEM_MINUS:
                case VIRTUAL_KEY.VK_SUBTRACT:
                    ZoomBy(1 / Viewport.Step, null);
                    return true;
                case VIRTUAL_KEY.VK_0:
                case VIRTUAL_KEY.VK_NUMPAD0:
                    ZoomActual();
                    return true;
                case VIRTUAL_KEY.VK_9:
                case VIRTUAL_KEY.VK_NUMPAD9:
                    ZoomFit();
                    return true;
            }
            return ToggleTextFormat(key);
        }

        switch (key)
        {
            case VIRTUAL_KEY.VK_DELETE:
            case VIRTUAL_KEY.VK_BACK:
                _document.DeleteSelected();
                return true;

            case VIRTUAL_KEY.VK_ESCAPE:
                if (_text.IsOpen) CommitText();
                else if (_document.IsCropSessionActive) SelectTool(EditorTool.Select);
                else if (_document.Selected is not null) _document.SelectAnnotation(null);
                else if (_document.ActiveTool != EditorTool.Select) SelectTool(EditorTool.Select);
                Invalidate();
                return true;

            case VIRTUAL_KEY.VK_RETURN:
                // Enter applies the frame without saving, so it can still be adjusted.
                if (_document.IsCropSessionActive)
                {
                    _document.CommitCrop();
                    SelectTool(EditorTool.Select);
                    return true;
                }
                break;
        }

        // A letter key's virtual-key code is its uppercase ASCII.
        return ToolShortcuts.ToolFor((char)key) is { } tool && SelectTool(tool);
    }

    /// <summary>
    /// Switches tools. Picking the active tool again returns to Select, so a tool is a toggle; leaving
    /// a tool ends an open text edit and a crop session, which a different tool cannot continue.
    /// </summary>
    private bool SelectTool(EditorTool tool)
    {
        if (tool == _document.ActiveTool && tool != EditorTool.Select && !_document.IsCropSessionActive)
            tool = EditorTool.Select;

        CommitText();
        _chrome?.ClosePicker();
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
        var pointer = PointerNow() ?? _clientPointer;
        if (InCanvas(pointer)) SetToolCursor(pointer);
    }


    private static bool KeyDown(VIRTUAL_KEY key) => (Functions.GetKeyState((int)key) & 0x8000) != 0;

    protected override void Dispose(bool disposing)
    {
        // Idempotent: OnDestroyed already released these if the window was closed normally.
        ReleaseResources();
        base.Dispose(disposing);
    }
}

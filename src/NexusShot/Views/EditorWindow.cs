using ToolCursor = DirectN.Extensions.Utilities.Cursor;
using NexusShot.Core;
using NexusShot.Render;
using NexusShot.Platform;

namespace NexusShot.Views;

/// <summary>
/// The markup editor. Input mutates the document and invalidates; a frame is one allocation-free
/// pass over the annotation list, and WM_PAINT is already coalesced to the display rate.
/// </summary>
public sealed class EditorWindow : D2DRenderWindow
{
    private readonly EditorDocument _document = new();

    /// <summary>The file being edited. Save As moves it.</summary>
    private string _path;

    private D2DResources? _resources;
    private AnnotationRenderer? _renderer;
    private ImageSurface? _image;
    private PixelEffectSource? _effects;
    private Ui? _ui;
    private EditorChrome? _chrome;

    private bool _fitToViewport = true;

    /// <summary>Client-space pointer, kept for the chrome (which works in client pixels, not image
    /// pixels) and for hit-testing the toolbar before the canvas sees the event.</summary>
    private Point _clientPointer;
    private bool _pointerDown;

    /// <summary>Where the image sits on screen, in client pixels; the mapping between screen and
    /// image space. Recomputed on resize and zoom, never per input event.</summary>
    private double _scale = 1;
    private double _offsetX;
    private double _offsetY;

    /// <summary>The live brush/eraser footprint under the cursor, in image pixels.</summary>
    private Point? _brushCursor;
    private bool _dragging;

    /// <summary>The grip under the pointer, if any. Drives the cursor shape.</summary>
    private ResizeHandle? _hoverHandle;

    /// <summary>The inline text box, while one is open.</summary>
    private TextEditor? _textEditor;

    private readonly AppTheme _theme;

    public EditorWindow(string path, AppTheme theme = AppTheme.System) : base("NexusShot")
    {
        _path = path;
        _theme = theme;
    }

    /// <summary>Raised when the window goes away, so the host can drop its reference and refresh a
    /// thumbnail whose file may have just been re-saved.</summary>
    public event Action? Closed;

    /// <summary>Raised when Save As writes a new file, so the shell can add it to the history.</summary>
    public event Action<string>? SavedAs;

    protected override void OnCreated(object? sender, EventArgs e)
    {
        base.OnCreated(sender, e);
        AppIcon.Apply(Handle);
        SystemTheme.ApplyTitleBar(Handle, SystemTheme.Resolve(_theme).IsDark);
        _document.Changed += (_, _) => Invalidate();
    }

    /// <summary>Destroying the HWND does not release the D2D device or the bitmap - they are COM
    /// objects this class owns, not window state - so they go here rather than waiting for a
    /// Dispose the host may never call.</summary>
    protected override void OnDestroyed(object? sender, EventArgs e)
    {
        ReleaseResources();
        Closed?.Invoke();
        base.OnDestroyed(sender, e);
    }

    private void ReleaseResources()
    {
        _textEditor?.Dispose();
        _effects?.Dispose();
        _image?.Dispose();
        _resources?.Dispose();

        _textEditor = null;
        _effects = null;
        _image = null;
        _resources = null;
        _renderer = null;
        _ui = null;
        _chrome = null;
    }

    /// <summary>Everything here belongs to the render target: D2D refuses to use resources from one
    /// factory with a target from another.</summary>
    private void EnsureResources(IComObject<ID2D1RenderTarget> target)
    {
        if (_resources is not null) return;

        _resources = new D2DResources(target);
        _renderer = new AnnotationRenderer(_resources);
        _ui = new Ui(_resources) { Theme = SystemTheme.Resolve(_theme) };
        _chrome = new EditorChrome(_ui);

        // Effects need a device context; without one the renderer falls back to its placeholder.
        using var context = target.AsDeviceContext();
        if (context is null) return;

        _image = ImageSurface.Load(_path, context, keepPixels: true);
        _document.SetImageSize(_image.Width, _image.Height);
        _effects = new PixelEffectSource(_image, _resources);
    }

    // ============================  VIEW TRANSFORM  ============================

    /// <summary>
    /// Fit uses the space in both directions; 100% means one image pixel to one *physical* pixel,
    /// not one DIP - a DIP-based 1:1 would resample the image and soften it.
    ///
    /// Recomputed per frame from the rect being drawn into, so the transform cannot lag the window.
    /// </summary>
    private void Layout()
    {
        if (_image is null) return;
        var well = CanvasWell();
        var margin = 24 * EditorChrome.Scale;

        var available = new Size(
            Math.Max(1, well.Width - margin * 2),
            Math.Max(1, well.Height - margin * 2));

        // Fit never enlarges past 1:1: upscaling would soften the image and inflate every stroke
        // width and adorner drawn in image space.
        _scale = _fitToViewport
            ? Math.Min(1, Math.Min(available.Width / _image.Width, available.Height / _image.Height))
            : 1;

        _offsetX = Math.Round(well.X + (well.Width - _image.Width * _scale) / 2);
        _offsetY = Math.Round(well.Y + (well.Height - _image.Height * _scale) / 2);
    }

    /// <summary>The sunken area the image sits in: everything between the chrome and the footer.</summary>
    private Rect CanvasWell()
    {
        var client = ClientRect;
        return new Rect(
            0,
            EditorChrome.ChromeTop,
            Math.Max(1, client.Width),
            Math.Max(1, client.Height - EditorChrome.ChromeTop - EditorChrome.FooterHeight));
    }

    /// <summary>Inverse display scale: adorners are drawn in image space but must keep a constant
    /// on-screen size however far the image is zoomed out.</summary>
    private double AdornerScale => 1 / _scale;

    /// <summary>Grab radius in image pixels, so handles stay grabbable when zoomed out.</summary>
    private double HandleTolerance => 9 * AdornerScale;

    private Point ToImage(int clientX, int clientY)
    {
        if (_image is null) return Point.Zero;
        return new Point(
            Math.Clamp((clientX - _offsetX) / _scale, 0, _image.Width),
            Math.Clamp((clientY - _offsetY) / _scale, 0, _image.Height));
    }

    protected override bool OnResized(WindowResizedType type, SIZE size)
    {
        var handled = base.OnResized(type, size);
        Layout();
        Invalidate();
        return handled;
    }

    // ============================  RENDER  ============================

    protected override void Render(IComObject<ID2D1HwndRenderTarget> renderTarget)
    {
        using var target = renderTarget.AsRenderTarget();

        // Pin the target to 96 DPI so a unit is a physical pixel: ClientRect, WM_MOUSEMOVE and the
        // image are all already physical, and letting D2D scale on top of that double-scales
        // everything. The chrome then scales itself; the canvas deliberately does not.
        target.Object.SetDpi(96, 96);
        EditorChrome.Scale = Functions.GetDpiForWindow(Handle) / 96.0;

        EnsureResources(target);
        if (_ui is null || _chrome is null || _renderer is null) return;

        var client = ClientRect;
        renderTarget.Clear(D2DResources.ToD3D(_ui.Theme.SurfaceSunken));
        if (_image is null) return;

        Layout();

        // ---- canvas, in image space ----
        // The world transform carries the zoom and centring, so annotation geometry is written in
        // image pixels here exactly as the exporter writes it, with no second coordinate system.
        renderTarget.Object.SetTransform(
            D2D_MATRIX_3X2_F.Scale((float)_scale, (float)_scale)
            * D2D_MATRIX_3X2_F.Translation((float)_offsetX, (float)_offsetY));

        renderTarget.DrawBitmap(
            _image.Bitmap, 1f,
            // Linear filtering on the GPU: the image is scaled from full resolution every frame,
            // never from a pre-scaled copy. This is what keeps the preview sharp at any zoom.
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
            new D2D_RECT_F(0, 0, _image.Width, _image.Height));

        // While a text box is open it *is* the annotation: drawing the annotation too would show it
        // doubled behind the box.
        _renderer.DrawAnnotations(target, _document, _effects, skip: _textEditor?.Annotation);
        if (_textEditor is null) _renderer.DrawAdorners(target, _document, AdornerScale);

        // The brush footprint is the *cursor*, not a drawn ring: Windows composites the cursor, so it
        // tracks the pointer exactly, where anything the app paints arrives a frame late and trails.

        renderTarget.Object.SetTransform(D2D_MATRIX_3X2_F.Identity());

        // ---- chrome, in client space ----
        var toast = DateTime.UtcNow < _toastUntil ? _toast : null;

        _ui.BeginFrame(target, _clientPointer, _pointerDown);
        _chrome.Draw(_document, client.Width, client.Height,
            Path.GetFileName(_path), _fitToViewport, toast);
        _ui.EndFrame();

        ApplyChrome();

        // Keep repainting while a toast is up, so it disappears on time rather than on the next
        // stray mouse move.
        if (toast is not null) Invalidate();
    }

    /// <summary>Applies what the chrome asked for. The chrome reports intent; the window owns the
    /// document, so there is only ever one writer.</summary>
    private void ApplyChrome()
    {
        if (_chrome is null) return;

        if (_chrome.ToolPicked is { } tool) SelectTool(tool);
        if (_chrome.UndoPressed) _document.Undo();
        if (_chrome.RedoPressed) _document.Redo();
        if (_chrome.DeletePressed) _document.DeleteSelected();
        if (_chrome.SavePressed) Save();
        if (_chrome.SaveAsPressed) SaveAs();
        if (_chrome.CopyPressed) CopyToClipboard();

        if (_chrome.FitPicked is { } fit && fit != _fitToViewport)
        {
            _fitToViewport = fit;
            Invalidate();
        }

        // The slider and the swatches change the brush's size and colour, and the cursor *is* the
        // brush footprint.
        RefreshCursor();
    }

    /// <summary>Writes the flattened image over the original. A crop frame the user is still
    /// dragging is applied too: the footer says "Save to apply", so Save applies it.</summary>
    private void Save()
    {
        if (_image is null) return;

        CommitText();
        _document.CommitCrop();

        Exporter.SavePng(_document, _path, _path);

        // The annotations and the crop are baked into the file now, so the session starts clean over
        // the new pixels.
        _document.ResetAfterSave();
        ReloadImage();
        ShowToast("Saved");
    }

    /// <summary>Writes the flattened image somewhere new and continues editing it there.</summary>
    private void SaveAs()
    {
        if (_image is null) return;

        CommitText();
        _document.CommitCrop();

        var suggested = Path.GetFileName(_path);
        if (FilePicker.SavePng(Handle, suggested, Path.GetDirectoryName(_path)) is not { } destination)
            return;

        Exporter.SavePng(_document, _path, destination);

        // The editor follows the file: further edits belong to the copy, not the original.
        _path = destination;
        _document.ResetAfterSave();
        ReloadImage();
        SavedAs?.Invoke(destination);
        ShowToast("Saved");
    }

    private void CopyToClipboard()
    {
        if (_image is null) return;

        CommitText();

        var temporary = Path.Combine(Path.GetTempPath(), $"nexusshot-{Guid.NewGuid():N}.png");

        // The clipboard gets the crop the user is looking at, without committing it to the document -
        // copying is not saving, and a copy must not silently discard the uncropped original.
        Exporter.SavePng(_document, _path, temporary, _document.PendingCrop);

        ClipboardImage.Copy(temporary);
        try { File.Delete(temporary); } catch (IOException) { /* the clipboard may still hold it */ }

        ShowToast("Copied");
    }

    /// <summary>A brief confirmation in the footer, so an action that changes nothing visible still
    /// says it happened.</summary>
    private void ShowToast(string message)
    {
        _toast = message;
        _toastUntil = DateTime.UtcNow.AddSeconds(2);
        Invalidate();
    }

    private string? _toast;
    private DateTime _toastUntil;

    /// <summary>Re-decodes the file after a save, so the editor is now working over the flattened
    /// pixels rather than the original plus a document that no longer exists.</summary>
    private void ReloadImage()
    {
        if (_resources is null || RenderTarget is null) return;
        using var target = RenderTarget.AsRenderTarget();
        using var context = target.AsDeviceContext();
        if (context is null) return;

        _effects?.Dispose();
        _image?.Dispose();

        _image = ImageSurface.Load(_path, context, keepPixels: true);
        _effects = new PixelEffectSource(_image, _resources);
        _document.SetImageSize(_image.Width, _image.Height);
        Invalidate();
    }

    private static bool IsPaintTool(EditorTool tool) =>
        tool is EditorTool.Brush or EditorTool.Eraser or EditorTool.Pen
            or EditorTool.Blur or EditorTool.Pixelate;

    // ============================  INPUT  ============================
    //
    // Input mutates the document and invalidates. There is no per-event render, no buffering of
    // samples, and no frame-batching machinery: WM_PAINT is already coalesced to the display rate,
    // so a burst of pointer messages collapses into one frame on its own.

    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmMouseLeave = 0x02A3;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSetCursor = 0x0020;
    private const uint WmCtlColorEdit = 0x0133;

    private static readonly LRESULT Handled = new() { Value = 0 };

    protected override LRESULT? WindowProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case WmLButtonDown:
                OnPointerPressed(ClientPoint(lParam));
                return Handled;

            case WmMouseMove:
                OnPointerMoved(ClientPoint(lParam), ((ulong)wParam.Value & 0x0001) != 0);
                return Handled;

            case WmLButtonUp:
                OnPointerReleased(ClientPoint(lParam));
                return Handled;

            case WmMouseLeave:
                if (_brushCursor is not null)
                {
                    _brushCursor = null;
                    Invalidate();
                }
                break;

            case WmSetCursor:
                // Own the cursor, or Windows keeps restoring the class arrow.
                if (SetToolCursor()) return new LRESULT { Value = 1 };
                break;

            case WmCtlColorEdit:
                // The child EDIT asks what colours to use; answering makes it read as part of the
                // canvas rather than a control dropped on top.
                if (_textEditor is { } editor)
                {
                    var brush = editor.OnCtlColor(
                        (IntPtr)(long)wParam.Value, (IntPtr)(long)lParam.Value, BackdropUnderText(editor));
                    if (brush != IntPtr.Zero) return new LRESULT { Value = brush };
                }
                break;

            case WmKeyDown:
                if (OnKeyDown((VIRTUAL_KEY)(ulong)wParam.Value)) return Handled;
                break;
        }
        return base.WindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Windows draws the cursor, so it never trails the pointer the way an app-drawn one
    /// does. False outside the image, so the frame keeps its own resize cursors.</summary>
    private bool SetToolCursor()
    {
        if (_image is null) return false;

        if (!InCanvas(_clientPointer))
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

        var cursor = _document.ActiveTool switch
        {
            EditorTool.Select => ToolCursors.Arrow,
            EditorTool.Pen => ToolCursors.Pencil(),

            // The brush and eraser show their true footprint, at its on-screen size. The eraser's is
            // a faint fill rather than the paint colour: it removes, it does not add.
            EditorTool.Brush => ToolCursors.Circle(
                PaintStrokeGeometry.Diameter(_document.ActiveThickness) * _scale,
                Palette.Parse(_document.ColorHex).WithAlpha(70)),

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

    private void OnPointerPressed((int X, int Y) client)
    {
        _clientPointer = new Point(client.X, client.Y);
        _pointerDown = true;

        if (_image is null) { Invalidate(); return; }

        // Clicking anywhere commits an open text box first, like every canvas editor.
        CommitText();

        // The chrome gets first refusal - a press on the toolbar, or on a popup floating over the
        // canvas, must not also start a gesture underneath it.
        if (!InCanvas(_clientPointer)
            || (_ui?.WantsPointer ?? false)
            || (_chrome?.PopupOpen ?? false))
        {
            Invalidate();
            return;
        }

        var point = ToImage(client.X, client.Y);

        // An existing text annotation reopens for editing rather than being redrawn.
        if (_document.ActiveTool is EditorTool.Select or EditorTool.Text
            && _document.Annotations.LastOrDefault(a => a.Tool == EditorTool.Text && a.HitTest(point))
                is { } existing)
        {
            _document.SelectAnnotation(existing);
            BeginTextEdit(existing);
            Invalidate();
            return;
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

        if (_image is null) { Invalidate(); return; }
        var point = ToImage(client.X, client.Y);

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

        _brushCursor = IsPaintTool(_document.ActiveTool) && InCanvas(_clientPointer) ? point : null;


        // The chrome is immediate: hover states only update when something repaints, so every move
        // invalidates. A frame is ~1 ms, and Windows coalesces WM_PAINT, so this is not a hot loop.
        Invalidate();
    }

    private void OnPointerReleased((int X, int Y) client)
    {
        _clientPointer = new Point(client.X, client.Y);
        _pointerDown = false;

        if (_dragging && _image is not null)
        {
            _dragging = false;
            Functions.ReleaseCapture();
            _document.EndGesture(ToImage(client.X, client.Y));

            // A freshly placed text box opens for typing immediately - placing one and then having
            // to click it again would be a step nobody wants.
            if (_document.Selected is { Tool: EditorTool.Text } placed && placed.Text.Length == 0)
                BeginTextEdit(placed);
        }
        Invalidate();
    }

    // ============================  TEXT  ============================

    /// <summary>
    /// The image's own colour under the text box, so the EDIT's background disappears into the
    /// screenshot instead of punching a white hole in it. Sampled from the decoded pixels at the
    /// annotation's top-left; a screenshot is overwhelmingly flat where text gets placed, so one
    /// sample is enough and costs nothing.
    /// </summary>
    private Rgba BackdropUnderText(TextEditor editor)
    {
        // The editor loads with keepPixels, so this is present; white is a safe fallback if a future
        // caller ever loads without them.
        if (_image?.Pixels is not { } pixels) return Rgba.White;

        var bounds = editor.Annotation.Bounds;
        var x = (int)Math.Clamp(bounds.X + 2, 0, _image.Width - 1);
        var y = (int)Math.Clamp(bounds.Y + 2, 0, _image.Height - 1);

        var offset = y * _image.Stride + x * 4;
        if (offset + 3 >= pixels.Length) return Rgba.White;

        // Premultiplied BGRA over white, since the box is opaque.
        var alpha = pixels[offset + 3];
        var inverse = 255 - alpha;
        return new Rgba(
            (byte)Math.Min(255, pixels[offset + 2] + inverse),
            (byte)Math.Min(255, pixels[offset + 1] + inverse),
            (byte)Math.Min(255, pixels[offset] + inverse));
    }

    /// <summary>Opens the inline box over an annotation, sized and styled like the text will be.</summary>
    private void BeginTextEdit(Annotation annotation)
    {
        CommitText();
        if (_image is null) return;

        var bounds = annotation.Bounds;
        var client = new Rect(
            _offsetX + bounds.X * _scale,
            _offsetY + bounds.Y * _scale,
            Math.Max(24, bounds.Width * _scale),
            Math.Max(24, bounds.Height * _scale));

        _textEditor = TextEditor.Open(Handle, annotation, client, _scale);
        Invalidate();
    }

    /// <summary>
    /// Writes the box's text back and closes it. An empty box for a brand-new annotation is
    /// cancelled outright, so a stray click with the text tool leaves nothing behind - and takes its
    /// undo entry with it, exactly as the XAML build's CancelAnnotation did.
    /// </summary>
    private void CommitText()
    {
        if (_textEditor is not { } editor) return;
        _textEditor = null;

        var annotation = editor.Annotation;
        var text = editor.Text;
        editor.Dispose();

        if (string.IsNullOrWhiteSpace(text))
        {
            _document.CancelAnnotation(annotation);
            return;
        }
        _document.SetTextContent(annotation, text, annotation.Bounds);
    }

    /// <summary>True inside the image well - the region the canvas owns, between the bars.</summary>
    private bool InCanvas(Point client) => CanvasWell().Contains(client);

    private bool OnKeyDown(VIRTUAL_KEY key)
    {
        // While the text box has focus its keystrokes are text, not shortcuts - otherwise typing
        // "rectangle" would switch tools eight times. Escape still closes it.
        if (_textEditor is not null && key != VIRTUAL_KEY.VK_ESCAPE) return false;

        var control = (Functions.GetKeyState((int)VIRTUAL_KEY.VK_CONTROL) & 0x8000) != 0;

        if (control)
        {
            switch (key)
            {
                case VIRTUAL_KEY.VK_Z: _document.Undo(); return true;
                case VIRTUAL_KEY.VK_Y: _document.Redo(); return true;
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
                if (_textEditor is not null) CommitText();
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
            case VIRTUAL_KEY.VK_V: return SelectTool(EditorTool.Select);
            case VIRTUAL_KEY.VK_R: return SelectTool(EditorTool.Rectangle);
            case VIRTUAL_KEY.VK_E: return SelectTool(EditorTool.Ellipse);
            case VIRTUAL_KEY.VK_A: return SelectTool(EditorTool.Arrow);
            case VIRTUAL_KEY.VK_L: return SelectTool(EditorTool.Line);
            case VIRTUAL_KEY.VK_D: return SelectTool(EditorTool.Pen);
            case VIRTUAL_KEY.VK_M: return SelectTool(EditorTool.Brush);
            case VIRTUAL_KEY.VK_X: return SelectTool(EditorTool.Eraser);
            case VIRTUAL_KEY.VK_T: return SelectTool(EditorTool.Text);
            case VIRTUAL_KEY.VK_N: return SelectTool(EditorTool.Counter);
            case VIRTUAL_KEY.VK_H: return SelectTool(EditorTool.Highlight);
            case VIRTUAL_KEY.VK_B: return SelectTool(EditorTool.Blur);
            case VIRTUAL_KEY.VK_P: return SelectTool(EditorTool.Pixelate);
            case VIRTUAL_KEY.VK_S: return SelectTool(EditorTool.Spotlight);
            case VIRTUAL_KEY.VK_C: return SelectTool(EditorTool.Crop);

            case VIRTUAL_KEY.VK_1:
                _fitToViewport = !_fitToViewport;
                Invalidate();
                return true;
        }
        return false;
    }

    private bool SelectTool(EditorTool tool)
    {
        if (tool != EditorTool.Crop) _document.CancelCropSession();
        _document.ActiveTool = tool;
        if (tool == EditorTool.Crop && _image is not null) _document.BeginCropSession();
        _brushCursor = null;
        RefreshCursor();
        Invalidate();
        return true;
    }

    /// <summary>WM_SETCURSOR only arrives on a mouse move, so a size change on the slider would
    /// otherwise leave the ring at its old diameter until you jiggled the mouse.</summary>
    private void RefreshCursor()
    {
        if (InCanvas(_clientPointer)) SetToolCursor();
    }

    protected override void Dispose(bool disposing)
    {
        // Idempotent: OnDestroyed already released these if the window was closed normally.
        ReleaseResources();
        base.Dispose(disposing);
    }
}

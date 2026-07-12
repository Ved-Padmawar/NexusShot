using ToolCursor = DirectN.Extensions.Utilities.Cursor;
using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The markup editor: a Win32 window that draws the image and its annotations with Direct2D.
///
/// Why this is fast where the XAML build was not. There, every pointer move mutated a retained
/// visual tree, which meant scanning children to find an annotation's elements, patching them,
/// and letting layout re-run — work proportional to the scene, on the UI thread, per input event.
/// The old code fought that with hand-rolled frame batching (buffer the samples, hook
/// CompositionTarget.Rendering, flush once per frame). Here, input only mutates the document and
/// asks for a repaint; Windows coalesces WM_PAINT to the display rate for free, and a frame is one
/// pass over the annotation list with no allocation. A fast drag and a stationary pointer cost the
/// same, which is the bug this rewrite exists to kill.
/// </summary>
public sealed class EditorWindow : D2DRenderWindow
{
    private readonly EditorDocument _document = new();
    private readonly string _path;

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

    public EditorWindow(string path) : base("NexusShot") => _path = path;

    /// <summary>Raised when the window goes away, so the host can drop its reference and refresh a
    /// thumbnail whose file may have just been re-saved.</summary>
    public event Action? Closed;

    protected override void OnCreated(object? sender, EventArgs e)
    {
        base.OnCreated(sender, e);
        _document.Changed += (_, _) => Invalidate();
    }

    /// <summary>
    /// Releases the device resources when the window goes away.
    ///
    /// Destroying the HWND does not release the D2D device, the full-resolution bitmap or the
    /// effects - they are COM objects this class owns, not window state. Waiting for Dispose leaves
    /// them alive for as long as the host holds a reference, so opening and closing a few captures
    /// walks the process into the hundreds of megabytes.
    /// </summary>
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

    /// <summary>
    /// Uploads the image and builds the device resources.
    ///
    /// Everything here belongs to the window's render target: D2D refuses to use resources from one
    /// factory with a target from another, and the bitmap is a device resource besides. So the
    /// target is the single source of both, and this reruns whenever the target is recreated.
    /// </summary>
    private void EnsureResources(IComObject<ID2D1RenderTarget> target)
    {
        if (_resources is not null) return;

        _resources = new D2DResources(target);
        _renderer = new AnnotationRenderer(_resources);
        _ui = new Ui(_resources) { Theme = Theme.Dark };
        _chrome = new EditorChrome(_ui);

        // Effects (blur, pixelate) need a device context. An ID2D1HwndRenderTarget exposes one only
        // if it QIs across; when it does not, the renderer falls back to its frosted placeholder
        // rather than failing, so the editor still works.
        using var context = target.AsDeviceContext();
        if (context is null) return;

        _image = ImageSurface.Load(_path, context, keepPixels: true);
        _document.SetImageSize(_image.Width, _image.Height);
        _effects = new PixelEffectSource(_image, _resources);
    }

    // ============================  VIEW TRANSFORM  ============================

    /// <summary>
    /// Fits the image to the viewport, or shows it at physical 1:1.
    ///
    /// At 100% one image pixel maps to one *physical* display pixel, not one DIP. On a 150% display
    /// a DIP-based 1:1 would resample the image and soften it; this is the same rule the XAML build
    /// arrived at, and it is why the preview is sharp.
    /// Recomputed at the top of every frame rather than cached on resize: it is a few arithmetic
    /// operations, and deriving it from the client rect we are actually drawing into means the
    /// transform can never lag the window (which it did when computed at creation, before the
    /// window had been sized).
    /// </summary>
    private void Layout()
    {
        if (_image is null) return;
        var well = CanvasWell();
        const int margin = 24;

        var available = new Size(
            Math.Max(1, well.Width - margin * 2),
            Math.Max(1, well.Height - margin * 2));

        _scale = _fitToViewport
            ? Math.Min(1, Math.Min(available.Width / _image.Width, available.Height / _image.Height))
            : 1;

        _offsetX = Math.Round(well.X + (well.Width - _image.Width * _scale) / 2);
        _offsetY = Math.Round(well.Y + (well.Height - _image.Height * _scale) / 2);
    }

    /// <summary>The sunken area the image sits in: everything between the toolbar and the footer.</summary>
    private Rect CanvasWell()
    {
        var client = ClientRect;
        return new Rect(
            0,
            EditorChrome.ToolbarHeight,
            Math.Max(1, client.Width),
            Math.Max(1, client.Height - EditorChrome.ToolbarHeight - EditorChrome.FooterHeight));
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

        // Work in physical pixels.
        //
        // The render target defaults to the system DPI, so on a 200% display D2D would scale every
        // coordinate by 2 - while ClientRect, WM_MOUSEMOVE and the image are all already in physical
        // pixels. Laying out in one space and drawing through a transform in another is how you get
        // a toolbar at double size and a pointer that lands in the wrong place.
        //
        // Setting the target to 96 DPI means one unit is one pixel, and the app scales its own
        // chrome by DpiScale. That is also what makes "100% zoom" mean one image pixel to one
        // physical pixel, which is the rule that keeps a screenshot pin-sharp.
        target.Object.SetDpi(96, 96);

        // The chrome sizes itself against the display; the canvas deliberately does not, so one
        // image pixel stays one physical pixel at 100%.
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

        if (_brushCursor is { } cursor && IsPaintTool(_document.ActiveTool) && !_ui.WantsPointer)
            _renderer.DrawBrushCursor(target, cursor, _document.ActiveThickness, AdornerScale);

        renderTarget.Object.SetTransform(D2D_MATRIX_3X2_F.Identity());

        // ---- chrome, in client space ----
        _ui.BeginFrame(target, _clientPointer, _pointerDown);
        _chrome.Draw(_document, client.Width, client.Height, Path.GetFileName(_path));
        _ui.EndFrame();

        ApplyChrome();
    }

    /// <summary>Applies what the toolbar asked for. The chrome reports intent; the window owns the
    /// document, so there is only ever one writer.</summary>
    private void ApplyChrome()
    {
        if (_chrome is null) return;

        if (_chrome.ToolPicked is { } tool) SelectTool(tool);
        if (_chrome.UndoPressed) _document.Undo();
        if (_chrome.RedoPressed) _document.Redo();
        if (_chrome.SavePressed) Save();
        if (_chrome.CopyPressed) CopyToClipboard();
    }

    private void Save()
    {
        if (_image is null) return;
        Exporter.SavePng(_document, _path, _path);

        // The annotations are baked into the file now, so the session starts clean over the new
        // pixels - the same contract ResetAfterSave had in the XAML build.
        _document.ResetAfterSave();
        ReloadImage();
    }

    private void CopyToClipboard()
    {
        if (_image is null) return;
        var temporary = Path.Combine(Path.GetTempPath(), $"nexusshot-{Guid.NewGuid():N}.png");
        Exporter.SavePng(_document, _path, temporary);
        ClipboardImage.Copy(temporary);
        try { File.Delete(temporary); } catch (IOException) { /* the clipboard may still hold it */ }
    }

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
                // Own the cursor, or Windows keeps restoring the class arrow. The paint tools draw
                // their own footprint ring in the scene, so the system cursor is hidden for them;
                // everything else gets a crosshair. This is the WM_SETCURSOR contract the XAML
                // build had to reach through ProtectedCursor to approximate.
                if (SetToolCursor()) return new LRESULT { Value = 1 };
                break;

            case WmCtlColorEdit:
                // The child EDIT asks us what colours to paint with. Answering makes the box read as
                // part of the canvas rather than a system control dropped on top of it.
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

    /// <summary>
    /// Owns the cursor for the canvas. Windows draws it, so it never lags behind the pointer the
    /// way an app-drawn cursor does - that regression cost the XAML build two releases.
    /// Returns false outside the image so the frame keeps its normal resize cursors.
    /// </summary>
    private bool SetToolCursor()
    {
        if (_image is null) return false;

        // Over the chrome: an arrow, and never the hidden paint cursor.
        if (!InCanvas(_clientPointer))
        {
            ToolCursor.Set(ToolCursor.Arrow);
            return true;
        }

        // Hovering a grip: show what dragging it will do.
        if (_hoverHandle is { } handle)
        {
            ToolCursor.Set(handle switch
            {
                ResizeHandle.TopLeft or ResizeHandle.BottomRight => ToolCursor.SizeNWSE,
                ResizeHandle.TopRight or ResizeHandle.BottomLeft => ToolCursor.SizeNESW,
                ResizeHandle.Top or ResizeHandle.Bottom => ToolCursor.SizeNS,
                ResizeHandle.Left or ResizeHandle.Right => ToolCursor.SizeWE,
                _ => ToolCursor.SizeAll,
            });
            return true;
        }

        // Paint tools draw their true footprint as a ring in the scene. A system cursor on top of
        // that would be a second, smaller, lying indicator - so hide it.
        if (IsPaintTool(_document.ActiveTool))
        {
            ToolCursor.Set(null);
            return true;
        }

        ToolCursor.Set(_document.ActiveTool == EditorTool.Select ? ToolCursor.Arrow : ToolCursor.Cross);
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

        // The chrome gets first refusal. It reports what it wants during the frame it draws, so a
        // press inside the toolbar must not also start a gesture on the canvas underneath.
        if (!InCanvas(_clientPointer) || (_ui?.WantsPointer ?? false))
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

            // Tool shortcuts, matching CleanShot X.
            case VIRTUAL_KEY.VK_V: return SelectTool(EditorTool.Select);
            case VIRTUAL_KEY.VK_R: return SelectTool(EditorTool.Rectangle);
            case VIRTUAL_KEY.VK_O: return SelectTool(EditorTool.Ellipse);
            case VIRTUAL_KEY.VK_L: return SelectTool(EditorTool.Line);
            case VIRTUAL_KEY.VK_A: return SelectTool(EditorTool.Arrow);
            case VIRTUAL_KEY.VK_P: return SelectTool(EditorTool.Pen);
            case VIRTUAL_KEY.VK_B: return SelectTool(EditorTool.Brush);
            case VIRTUAL_KEY.VK_E: return SelectTool(EditorTool.Eraser);
            case VIRTUAL_KEY.VK_T: return SelectTool(EditorTool.Text);
            case VIRTUAL_KEY.VK_H: return SelectTool(EditorTool.Highlight);
            case VIRTUAL_KEY.VK_U: return SelectTool(EditorTool.Blur);
            case VIRTUAL_KEY.VK_X: return SelectTool(EditorTool.Pixelate);
            case VIRTUAL_KEY.VK_N: return SelectTool(EditorTool.Counter);
            case VIRTUAL_KEY.VK_S: return SelectTool(EditorTool.Spotlight);
            case VIRTUAL_KEY.VK_C: return SelectTool(EditorTool.Crop);

            case VIRTUAL_KEY.VK_1:
                _fitToViewport = !_fitToViewport;
                Layout();
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
        Invalidate();
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        // Idempotent: OnDestroyed already released these if the window was closed normally.
        ReleaseResources();
        base.Dispose(disposing);
    }
}

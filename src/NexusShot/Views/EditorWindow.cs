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

    private bool _fitToViewport = true;

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

    public EditorWindow(string path) : base("NexusShot") => _path = path;

    protected override void OnCreated(object? sender, EventArgs e)
    {
        base.OnCreated(sender, e);
        _document.Changed += (_, _) => Invalidate();
        LoadImage();
    }

    /// <summary>
    /// Uploads the image. Effects (blur, pixelate) are an ID2D1DeviceContext feature; an
    /// ID2D1HwndRenderTarget only exposes them if it QIs across. When it does not, the renderer
    /// falls back to the frosted placeholder rather than failing, so the editor still works.
    /// </summary>
    private void LoadImage()
    {
        if (RenderTarget?.Object is not ID2D1DeviceContext raw) return;
        using var context = new ComObject<ID2D1DeviceContext>(raw, releaseOnDispose: false);

        _image = ImageSurface.Load(_path, context);
        _document.SetImageSize(_image.Width, _image.Height);
        _effects = new PixelEffectSource(_image);

        Layout();
        Invalidate();
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
        var client = ClientRect;
        const int margin = 32;

        var available = new Size(
            Math.Max(1, client.Width - margin * 2),
            Math.Max(1, client.Height - margin * 2));

        _scale = _fitToViewport
            ? Math.Min(1, Math.Min(available.Width / _image.Width, available.Height / _image.Height))
            : 1;

        _offsetX = Math.Round((client.Width - _image.Width * _scale) / 2);
        _offsetY = Math.Round((client.Height - _image.Height * _scale) / 2);
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
        using var target = new ComObject<ID2D1RenderTarget>(renderTarget.Object, releaseOnDispose: false);
        _resources ??= new D2DResources(target);
        _renderer ??= new AnnotationRenderer(_resources);

        renderTarget.Clear(new D3DCOLORVALUE(1f, 0.10f, 0.10f, 0.11f));
        if (_image is null || _renderer is null) return;

        Layout();

        // Draw everything in image space: the world transform carries the zoom and centring, so
        // annotation geometry is written in image pixels here exactly as the exporter writes it.
        renderTarget.Object.SetTransform(
            D2D_MATRIX_3X2_F.Scale((float)_scale, (float)_scale)
            * D2D_MATRIX_3X2_F.Translation((float)_offsetX, (float)_offsetY));

        var imageRect = new D2D_RECT_F(0, 0, _image.Width, _image.Height);
        renderTarget.DrawBitmap(
            _image.Bitmap, 1f,
            // Linear filtering on the GPU: the image is scaled from full resolution every frame,
            // never from a pre-scaled copy. This is what keeps the preview sharp at any zoom.
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
            imageRect);

        _renderer.DrawAnnotations(target, _document, _effects);
        _renderer.DrawAdorners(target, _document, AdornerScale);

        if (_brushCursor is { } cursor && IsPaintTool(_document.ActiveTool))
            _renderer.DrawBrushCursor(target, cursor, _document.ActiveThickness, AdornerScale);

        renderTarget.Object.SetTransform(D2D_MATRIX_3X2_F.Identity());
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
        if (_image is null) return;

        Functions.SetCapture(Handle);
        _dragging = true;
        _document.BeginGesture(ToImage(client.X, client.Y), HandleTolerance);
    }

    private void OnPointerMoved((int X, int Y) client, bool leftDown)
    {
        if (_image is null) return;
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
            _hoverHandle = _document.PendingCrop is not null
                ? _document.GetCropHandleAt(point, HandleTolerance)
                : _document.Selected is { } selected
                    ? _document.GetResizeHandleAt(selected, point, HandleTolerance)
                    : null;
        }

        if (IsPaintTool(_document.ActiveTool))
        {
            _brushCursor = point;
            Invalidate();
        }
        else if (_brushCursor is not null)
        {
            _brushCursor = null;
            Invalidate();
        }
    }

    private void OnPointerReleased((int X, int Y) client)
    {
        if (!_dragging || _image is null) return;
        _dragging = false;
        Functions.ReleaseCapture();
        _document.EndGesture(ToImage(client.X, client.Y));
    }

    private bool OnKeyDown(VIRTUAL_KEY key)
    {
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
                if (_document.IsCropSessionActive) _document.CancelCropSession();
                else _document.SelectAnnotation(null);
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
        _effects?.Dispose();
        _image?.Dispose();
        _resources?.Dispose();
        base.Dispose(disposing);
    }
}

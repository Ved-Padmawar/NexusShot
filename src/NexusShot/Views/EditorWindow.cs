using ToolCursor = DirectN.Extensions.Utilities.Cursor;
using NexusShot.Core;
using NexusShot.Render;
using NexusShot.Platform;

namespace NexusShot.Views;

/// <summary>
/// The markup editor. Input mutates the document and invalidates; a frame is one allocation-free
/// pass over the annotation list, and WM_PAINT is already coalesced to the display rate.
/// </summary>
public sealed partial class EditorWindow : CaptionWindow
{
    private readonly EditorDocument _document = new();
    internal EditorDocument Document => _document;

    /// <summary>Save, Save As and Copy, and the destination they share.</summary>
    private readonly EditorFiles _files;

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

    private bool _dragging;

    /// <summary>The grip under the pointer, if any. Drives the cursor shape.</summary>
    private ResizeHandle? _hoverHandle;

    /// <summary>The inline text box: its lifecycle, keys and write-back.</summary>
    private readonly TextBoxController _text;

    /// <summary>True while a drag inside the box is selecting text.</summary>
    private bool _caretDragging;

    private AppTheme _theme;
    private string? _loadError;

    public EditorWindow(string path, AppTheme theme = AppTheme.System) : base("NexusShot")
    {
        _theme = theme;
        _text = new TextBoxController(_document);
        _files = new EditorFiles(_document);
        _files.OpenedAt(path);

        // The open box lives in this window's controller, so a write has to ask rather than do it.
        _files.Committing += CommitText;
    }

    /// <summary>Follows the shell's theme. The Ui's theme is read per frame, so this only has to
    /// retint the titlebar DWM owns and ask for a repaint.</summary>
    public void SetTheme(AppTheme theme)
    {
        _theme = theme;
        var resolved = SystemTheme.Resolve(theme);
        SystemTheme.ApplyFrame(Handle, resolved);
        if (_ui is not null) _ui.Theme = resolved;
        Invalidate();
    }

    /// <summary>The caption strip above the toolbar drags, save for where the buttons are.</summary>
    protected override bool IsDragRegion(Point client) =>
        client.X < ClientRect.Width - CaptionButtonsWidth;

    /// <summary>Raised when the window goes away, so the host can drop its reference and refresh a
    /// thumbnail whose file may have just been re-saved.</summary>
    public event Action? Closed;

    /// <summary>Raised when Save writes over the file being edited.</summary>
    public event Action<string>? Saved;

    /// <summary>Raised when Save As writes a new file, so the shell can add it to the history.</summary>
    public event Action<string>? SavedAs;

    protected override void OnCreated(object? sender, EventArgs e)
    {
        base.OnCreated(sender, e);

        // The chrome carries the filename, so the caption shows no icon or title of its own.
        AppIcon.ApplyLargeOnly(Handle);
        AppIcon.ClearCaption(Handle);
        SystemTheme.ApplyFrame(Handle, SystemTheme.Resolve(_theme));

        _document.Changed += (_, _) => Invalidate();

        _dispatch = new UiThreadDispatch(Handle);
    }

    /// <summary>Destroying the HWND does not release the D2D device or the bitmap - they are COM
    /// objects this class owns, not window state - so they go here rather than waiting for a
    /// Dispose the host may never call.</summary>
    protected override void OnDestroyed(object? sender, EventArgs e)
    {
        // Anything still queued outlived the window it was going to draw into.
        _dispatch.Clear();
        SetAnimating(false);

        ReleaseResources();
        Closed?.Invoke();
        base.OnDestroyed(sender, e);
    }

    private void ReleaseResources()
    {
        _renderer?.Dispose();
        _effects?.Dispose();
        _image?.Dispose();
        _resources?.Dispose();

        _text.Abandon();
        _effects = null;
        _image = null;
        _resources = null;
        _renderer = null;
        _ui = null;
        _chrome = null;

        // Keep explicit teardown idempotent, whether invoked by destruction or disposal.
        RenderTarget?.Dispose();
        RenderTarget = null;
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

        try { _image = ImageSurface.Load(_files.Path, context); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            Log.Error("editor.decode", exception, _files.Path);
            _loadError = "Could not open this image. Check that the file is readable and valid.";
            return;
        }
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
        var margin = 24 * DpiScale;

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
            CaptionHeight + 46 * DpiScale,
            Math.Max(1, client.Width),
            Math.Max(1, client.Height - CaptionHeight - 86 * DpiScale));
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

        EnsureResources(target);
        if (_ui is null || _chrome is null || _renderer is null) return;
        _chrome.Scale = DpiScale;
        _chrome.CaptionHeight = CaptionHeight;

        // Read per frame, so a theme change can never leave the canvas painted in the old colours.
        _ui.Theme = SystemTheme.Resolve(_theme);

        var client = ClientRect;
        renderTarget.Clear(D2DResources.ToD3D(_ui.Theme.SurfaceSunken));
        if (_image is null)
        {
            _ui.BeginFrame(target, _clientPointer, _pointerDown);
            _ui.Text(_loadError ?? "Could not open this image", CanvasWell(), _ui.Theme.TextPrimary,
                (float)(14 * DpiScale), align: TextAlign.Center);
            DrawCaptionButtons(_ui, client.Width);
            _ui.EndFrame();
            if (_ui.ClickedThisFrame) Invalidate();
            return;
        }

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
        _renderer.DrawAnnotations(target, _document, _effects, skip: _text.Annotation);

        // Editing is a sub-state of selection, so an open box keeps the grips that resize it.
        _renderer.DrawAdorners(target, _document, AdornerScale);

        if (_text.Editor is { } editing)
        {
            _renderer.DrawTextEditor(
                target, editing.Annotation, editing.Text,
                editing.Caret, editing.SelectionStart, editing.SelectionEnd, editing.CaretVisible,
                AdornerScale, Palette.Selection.WithAlpha(90));
        }

        // The brush footprint is the *cursor*, not a drawn ring: Windows composites the cursor, so it
        // tracks the pointer exactly, where anything the app paints arrives a frame late and trails.

        renderTarget.Object.SetTransform(D2D_MATRIX_3X2_F.Identity());

        // ---- chrome, in client space ----
        var now = DateTime.UtcNow;
        var toast = now < _toastUntil ? _toast : null;
        var copied = _copied.Progress(Environment.TickCount64);

        _ui.BeginFrame(target, _clientPointer, _pointerDown);
        _chrome.Draw(_document, client.Width, client.Height,
            _files.FileName, _fitToViewport, toast, copied);
        DrawCaptionButtons(_ui, client.Width);
        _ui.EndFrame();

        ApplyChrome();

        if (_ui.ClickedThisFrame) Invalidate();

        // A toast clears itself and a caret blinks, neither driven by input. Re-invalidating from
        // inside the frame would repaint at display rate to animate one glyph and one fade.
        SetAnimating(toast is not null || _copied.IsRunning || _text.IsOpen);
    }

    /// <summary>Applies what the chrome asked for. The chrome reports intent; the window owns the
    /// document, so there is only ever one writer.</summary>
    private void ApplyChrome()
    {
        if (_chrome is null) return;

        if (_chrome.ToolPicked is { } tool) SelectTool(tool);
        if (_chrome.UndoPressed) Undo();
        if (_chrome.RedoPressed) Redo();
        if (_chrome.DeletePressed) _document.DeleteSelected();
        // All three run outside the frame; see Post.
        if (_chrome.SavePressed) Post(() => RunFileAction(Save));
        if (_chrome.SaveAsPressed) Post(() => RunFileAction(SaveAs));
        if (_chrome.CopyPressed) Post(() => RunFileAction(CopyToClipboard));

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

        _files.Save();
        ReloadImage();
        Saved?.Invoke(_files.Path);
        ShowToast("Saved");
    }

    private void RunFileAction(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            Log.Error("editor.file_action", exception, _files.Path);
            ShowToast("Could not complete action. Check the file or clipboard and retry.");
        }
    }

    /// <summary>Writes the flattened image somewhere new and continues editing it there.</summary>
    private void SaveAs()
    {
        if (_image is null) return;

        // Null means cancelled: nothing was written, and the pending crop stays uncommitted.
        if (_files.SaveAs((name, folder) => FilePicker.SavePng(Handle, name, folder))
            is not { } destination) return;

        ReloadImage();
        SavedAs?.Invoke(destination);
        ShowToast("Saved");
    }

    private void CopyToClipboard()
    {
        if (_image is null) return;

        _files.CopyToClipboard();

        // The Copy button confirms this itself, by becoming a tick.
        _copied.Start(Environment.TickCount64);
        Invalidate();
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
    private readonly ConfirmFeedback _copied = new();

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

        _image = ImageSurface.Load(_files.Path, context);
        _effects = new PixelEffectSource(_image, _resources);
        _document.SetImageSize(_image.Width, _image.Height);
        Invalidate();
    }
}

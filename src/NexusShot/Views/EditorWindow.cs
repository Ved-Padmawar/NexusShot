using System.Runtime.InteropServices;
using NexusShot.Core;
using NexusShot.Render;
using NexusShot.Platform;

namespace NexusShot.Views;

/// <summary>
/// The markup editor. The capture sits on a dotted stage that runs to the window's top edge, and all
/// the chrome floats over it. Input mutates the document and invalidates; a frame is one pass over the
/// annotation list, and WM_PAINT is already coalesced to the display rate.
/// </summary>
public sealed partial class EditorWindow : CaptionWindow
{
    private readonly EditorDocument _document = new();
    internal EditorDocument Document => _document;

    /// <summary>Save, Save As and Copy, and the destination they share.</summary>
    private readonly EditorFiles _files;

    /// <summary>The app's settings - theme, accent, the colour lists and the OCR language - read live,
    /// so the editor never holds a copy that could go stale.</summary>
    private readonly AppSettings _settings;
    private readonly Action _settingsChanged;

    private D2DResources? _resources;
    private AnnotationRenderer? _renderer;
    private ImageSurface? _image;
    private PixelEffectSource? _effects;
    private Ui? _ui;
    private EditorChrome? _chrome;

    /// <summary>Zoom and pan: the user's intent. Where the image lands is derived from it each frame.</summary>
    private readonly Viewport _viewport = new();

    /// <summary>Client-space pointer, for the chrome and for hit-testing it before the canvas.</summary>
    private Point _clientPointer;
    private bool _pointerDown;

    /// <summary>The image's placement this frame: the mapping between client and image space.</summary>
    private double _scale = 1;
    private double _offsetX;
    private double _offsetY;

    private bool _dragging;

    /// <summary>The grip under the pointer, if any. Drives the cursor shape.</summary>
    private ResizeHandle? _hoverHandle;

    /// <summary>The inline text box: its lifecycle, keys and write-back.</summary>
    private readonly TextBoxController _text;

    /// <summary>True while the pointer is captured for a press that began on the chrome.</summary>
    private bool _chromeCaptured;

    /// <summary>True while a drag inside the box is selecting text.</summary>
    private bool _caretDragging;

    private string? _loadError;

    public EditorWindow(string path, AppSettings settings, Action settingsChanged) : base("NexusShot")
    {
        _settings = settings;
        _settingsChanged = settingsChanged;
        _text = new TextBoxController(_document);
        _files = new EditorFiles(_document);
        _files.OpenedAt(path);

        // The open box lives in this window's controller, so a write has to ask rather than do it.
        _files.Committing += CommitText;
    }

    private Theme CurrentTheme => SystemTheme.Resolve(_settings.Theme, _settings.Accent);

    /// <summary>Follows the shell's theme and accent. The Ui reads the theme per frame, so this only
    /// has to retint the frame DWM owns and ask for a repaint.</summary>
    public void Retheme()
    {
        SystemTheme.ApplyFrame(Handle, CurrentTheme);
        Invalidate();
    }

    /// <summary>The top band drags, except where its controls are.</summary>
    protected override bool IsDragRegion(Point client) =>
        client.X < ClientRect.Width - CaptionButtonsWidth && !(_chrome?.Covers(client) ?? false);

    protected override double DragBandHeight => _chrome?.TopBand ?? CaptionHeight;

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

        // The chrome carries the filename, so the caption shows no icon. The title stays for Alt+Tab.
        AppIcon.ApplyLargeOnly(Handle);
        UpdateTitle();
        SystemTheme.ApplyFrame(Handle, CurrentTheme);

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
        Repaint(null);

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
        _chrome?.Dispose();
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
        _ui = new Ui(_resources) { Theme = CurrentTheme };
        _chrome = new EditorChrome(_ui) { ShiftDown = () => KeyDown(VIRTUAL_KEY.VK_SHIFT) };

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

    /// <summary>The stage's inner area the image is placed in, clear of the floating chrome.</summary>
    private Rect Well() => _chrome?.Well(ClientRect.Width, ClientRect.Height) ?? new Rect(0, 0, 1, 1);

    /// <summary>The size of what is shown - the crop, once one is applied, rather than the whole image.</summary>
    private Size ImageSize => new(_document.VisibleBounds.Width, _document.VisibleBounds.Height);

    /// <summary>
    /// Places the image. 100% means one image pixel to one *physical* pixel, not one DIP - a DIP-based
    /// 1:1 would resample the image and soften it. Recomputed per frame from the rect being drawn
    /// into, so the transform cannot lag the window.
    /// </summary>
    private void Layout()
    {
        if (_image is null) return;
        var visible = _document.VisibleBounds;
        var placed = _viewport.Place(Well(), ImageSize);
        _scale = placed.Width / visible.Width;

        // The offset is where image pixel (0, 0) lands, so the crop's corner lands on the placement.
        _offsetX = placed.X - visible.X * _scale;
        _offsetY = placed.Y - visible.Y * _scale;
    }

    /// <summary>Inverse display scale: adorners are drawn in image space but must keep a constant
    /// on-screen size however far the image is zoomed out.</summary>
    private double AdornerScale => 1 / _scale;

    /// <summary>Grab radius in image pixels, so handles stay grabbable when zoomed out.</summary>
    private double HandleTolerance => 9 * AdornerScale;

    private Point ToImage(int clientX, int clientY)
    {
        if (_image is null) return Point.Zero;
        var visible = _document.VisibleBounds;
        return new Point(
            Math.Clamp((clientX - _offsetX) / _scale, visible.X, visible.Right),
            Math.Clamp((clientY - _offsetY) / _scale, visible.Y, visible.Bottom));
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

        // 96 DPI makes a unit a physical pixel; the chrome scales itself, the canvas does not.
        target.Object.SetDpi(96, 96);

        EnsureResources(target);
        if (_ui is null || _chrome is null || _renderer is null) return;
        _chrome.Scale = DpiScale;

        // Read per frame, so a theme change can never leave the canvas painted in the old colours.
        _ui.Theme = CurrentTheme;
        _ui.Scale = DpiScale;

        var client = ClientRect;
        var theme = _ui.Theme;
        renderTarget.Clear(D2DResources.ToD3D(theme.SurfaceStage));

        _ui.BeginFrame(target, _clientPointer, _pointerDown);
        _ui.FillDots(new Rect(0, 0, client.Width, client.Height), theme.StageDot, (float)(18 * DpiScale));

        if (_image is null)
        {
            _ui.Text(_loadError ?? "Could not open this image", new Rect(0, 0, client.Width, client.Height),
                theme.TextPrimary, 14 * DpiScale, align: TextAlign.Center);
            DrawCaptionButtons(_ui, client.Width);
            _ui.EndFrame();
            if (_ui.ClickedThisFrame) Invalidate();
            return;
        }

        Layout();
        var visible = _document.VisibleBounds;
        var imageRect = new Rect(_offsetX + visible.X * _scale, _offsetY + visible.Y * _scale,
            visible.Width * _scale, visible.Height * _scale);
        var corner = 6 * DpiScale;

        _ui.Shadow(imageRect, (float)corner, 36 * DpiScale, 18 * DpiScale, theme.Shadow);
        _ui.Shadow(imageRect, (float)corner, 8 * DpiScale, 3 * DpiScale, theme.Shadow);

        // ---- canvas, in image space: the transform carries zoom and placement, as the exporter's does ----
        renderTarget.Object.SetTransform(
            D2D_MATRIX_3X2_F.Scale((float)_scale, (float)_scale)
            * D2D_MATRIX_3X2_F.Translation((float)_offsetX, (float)_offsetY));

        _ui.PushRoundedLayer(visible, (float)(corner / _scale));
        renderTarget.DrawBitmap(
            _image.Bitmap, 1f,
            // Scaled from full resolution every frame, which keeps it sharp at any zoom.
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
            new D2D_RECT_F(0, 0, _image.Width, _image.Height));

        // An open text box draws its own annotation.
        _renderer.DrawAnnotations(target, _document, _effects, skip: _text.Annotation);
        _ui.PopRoundedLayer();

        // Editing is a sub-state of selection, so an open box keeps the grips that resize it.
        _renderer.DrawAdorners(target, _document, AdornerScale);

        if (_text.Editor is { } editing)
        {
            _renderer.DrawTextEditor(
                target, editing.Annotation, editing.Text, editing.Style, editing.Runs,
                editing.Caret, editing.SelectionStart, editing.SelectionEnd, editing.CaretVisible,
                AdornerScale);
        }

        renderTarget.Object.SetTransform(D2D_MATRIX_3X2_F.Identity());

        // ---- chrome, in client space ----
        var toast = DateTime.UtcNow < _toastUntil ? _toast : null;
        _chrome.Draw(new EditorChrome.Frame(
            _document, _settings, client.Width, client.Height, CaptionButtonsWidth,
            Path.GetFileNameWithoutExtension(_files.FileName), _scale, imageRect, _scale,
            new Point(_offsetX, _offsetY), toast, _fileBusy, ActiveTextStyle));
        DrawCaptionButtons(_ui, client.Width);
        _ui.EndFrame();

        if (!_fileBusy && !_confirmingClose) ApplyChrome();
        if (_ui.ClickedThisFrame) Invalidate();

        // Display rate while animating, a slow tick for a caret or toast, otherwise nothing.
        Repaint(_ui.Animating ? FrameInterval
            : toast is not null || _text.IsOpen ? 120
            : _ui.Blinking ? (uint)Ui.CaretBlink
            : null);
    }

    /// <summary>Applies what the chrome asked for. Everything that runs a modal loop or a file
    /// operation is posted, to run once this frame has finished.</summary>
    private void ApplyChrome()
    {
        if (_chrome is null) return;

        if (_chrome.ToolPicked is { } tool) SelectTool(tool);
        if (_chrome.StyleToggled is { } style) ToggleTextStyle(style);

        switch (_chrome.Requested)
        {
            case EditorChrome.Command.Undo: Undo(); break;
            case EditorChrome.Command.Redo: Redo(); break;
            case EditorChrome.Command.Save: Post(() => RunFileAction(Save)); break;
            case EditorChrome.Command.SaveAs: Post(() => RunFileAction(SaveAs)); break;
            case EditorChrome.Command.CopyAndClose: Post(() => RunFileAction(CopyAndClose)); break;
            case EditorChrome.Command.CopyText: Post(() => RunFileAction(CopyText)); break;
            case EditorChrome.Command.Share: Post(() => RunFileAction(Share)); break;
            case EditorChrome.Command.ZoomIn: ZoomBy(Viewport.Step, null); break;
            case EditorChrome.Command.ZoomOut: ZoomBy(1 / Viewport.Step, null); break;
            case EditorChrome.Command.ZoomActual: ZoomActual(); break;
            case EditorChrome.Command.ZoomFit: ZoomFit(); break;
            case EditorChrome.Command.PickFromScreen: Post(PickFromScreen); break;
        }

        if (_chrome.CopyRequested is { } text) CopyToClipboardText(text);

        if (_chrome.SettingsChanged)
        {
            _chrome.SettingsChanged = false;
            _settingsChanged();
        }

        // The size and colour controls change the brush footprint, and the cursor *is* the footprint.
        RefreshCursor();
    }

    private void ZoomBy(double factor, Point? anchor)
    {
        if (_image is null) return;
        _viewport.ZoomBy(factor, Well(), ImageSize, anchor ?? Well().Center);
        Invalidate();
    }

    private void ZoomActual()
    {
        if (_image is null) return;
        _viewport.Actual(Well(), ImageSize, Well().Center);
        Invalidate();
    }

    private void ZoomFit()
    {
        _viewport.Fit();
        Invalidate();
    }

    /// <summary>Freezes the screen for the eyedropper and takes the colour clicked. The picker stays
    /// open behind it, so the colour lands where the user was working.</summary>
    private void PickFromScreen()
    {
        if (ColorDropper.Pick(CurrentTheme) is not { } color || _chrome is null) return;
        var picked = color with { A = Palette.Parse(_document.Selected?.ColorHex ?? _document.ColorHex).A };
        _chrome.AdoptPickedColor(picked);
        _document.SetColor(picked.ToHex());
        Invalidate();
    }

    private void CopyToClipboardText(string text)
    {
        try { ClipboardText.Copy(text); ShowToast("Copied " + text); }
        catch (InvalidOperationException exception) { Log.Error("editor.copy_hex", exception); }
    }

    /// <summary>Alt+Tab and the taskbar name the window by the file it is editing.</summary>
    private void UpdateTitle() => SetWindowTextW(Handle, $"{_files.FileName} - NexusShot");

    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowTextW(IntPtr window, string text);
}

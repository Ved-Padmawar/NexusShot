using System.Runtime.InteropServices;
using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The quick-access card: a borderless thumbnail at the bottom-left, stacking upward. Never takes
/// focus (WS_EX_NOACTIVATE) and stays out of Alt+Tab (WS_EX_TOOLWINDOW), so a capture does not
/// interrupt what you were doing. Hovering reveals the actions; auto-dismiss pauses under the
/// pointer and while pinned.
/// </summary>
public sealed partial class FloatingPreview : D2DRenderWindow
{
    protected override void CreateRenderTarget()
    {
        RenderTarget?.Dispose();
        RenderTarget = null;
        RenderTarget = GraphicsBackend.CreateWindowTarget(Handle, ClientRect.Size.ToD2D_SIZE_U(), FactoryType, FactoryOptions, software: true);
    }
    // This tool window has no taskbar icon; avoid the dependency's per-window owned icon.
    protected override DirectN.Extensions.Utilities.Icon? LoadCreationIcon() => null;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmMouseLeave = 0x02A3;
    private const uint WmTimer = 0x0113;

    private const nuint DismissTimerId = 1;
    private const nuint DismissAnimationTimerId = 2;
    private const nuint CopyFeedbackTimerId = 3;
    private readonly ConfirmFeedback _copied = new();
    private readonly ConfirmFeedback _textCopied = new();

    /// <summary>Card actions - save-as, edit, pin, dismiss - are posted rather than run inline: each
    /// unwinds into a reflow or a modal loop, and neither may run inside DoDragDrop or this card's
    /// own Render.</summary>
    private UiThreadDispatch _dispatch = null!;
    private void Post(Action work) => _dispatch.Post(work);

    // Design units; scaled per-monitor.
    private const double StackGap = 10;
    private const double EdgeMargin = 18;

    private readonly QuickAccess _stack;
    private readonly QuickAccessCard _card;

    private D2DResources? _resources;
    private Ui? _ui;
    private ImageSurface? _thumbnail;

    /// <summary>The CPU copy behind <see cref="_thumbnail"/>, kept so a drag does not re-decode.</summary>
    private DecodedImage? _thumbnailPixels;

    private bool _hovered;

    public QuickAccessCard Card => _card;

    public event Action<FloatingPreview>? Dismissed;
    public event Action<ScreenshotHistoryItem>? EditRequested;

    private double _scale = 1;
    private double S(double units) => units * _scale;

    public FloatingPreview(QuickAccess stack, QuickAccessCard card)
        : base("NexusShot preview",
            (WINDOW_STYLE)WS_POPUP,
            (WINDOW_EX_STYLE)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE))
    {
        _stack = stack;
        _card = card;
    }

    /// <summary>Drops the thumbnail after the capture was re-saved: the file is the same, the
    /// pixels are not.</summary>
    public void Refresh()
    {
        _thumbnail?.Dispose();
        _thumbnail = null;
        _thumbnailPixels?.Dispose();
        _thumbnailPixels = null;

        _stack.ResetCountdown(_card);
        Invalidate();
    }

    /// <summary>Cards are dark in both themes - they sit over whatever is on screen - but carry the
    /// user's accent.</summary>
    private Theme Theme => SystemTheme.Resolve(AppTheme.Dark, _stack.Settings.Accent);

    private static Size CardSize(double scale) =>
        new(Math.Round(CardLayout.Width * scale), Math.Round(CardLayout.Height * scale));

    private Rect Scaled(Rect design) => new(S(design.X), S(design.Y), S(design.Width), S(design.Height));

    /// <summary>Places the card at its slot in the bottom-left stack.</summary>
    public void PlaceAt(RectInt workArea, double scale, double stackOffset)
    {
        // A reflow must not fight the dismissal slide.
        if (_dismissing) return;

        _scale = scale;
        var size = CardSize(scale);

        var slot = CardLayout.Slot(workArea.ToRect(), size, Math.Round(EdgeMargin * scale), Math.Round(stackOffset),
            _stack.Settings.CardCorner);
        var x = (int)Math.Round(slot.X);
        var y = (int)Math.Round(slot.Y);

        WindowInterop.SetWindowPos(Handle, HWND_TOPMOST, x, y, (int)size.Width, (int)size.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // Re-asserted: the first show can drop attributes set while hidden.
        ApplyDwmChrome();
    }

    /// <summary>The height this card occupies in the stack, including the gap below the next one.</summary>
    public static double StackHeight(double scale) => CardSize(scale).Height + StackGap * scale;

    protected override void OnCreated(object? sender, EventArgs e)
    {
        base.OnCreated(sender, e);

        _dispatch = new UiThreadDispatch(Handle);

        ApplyDwmChrome();

        // Always running: the setting is read per tick, so a change reaches cards already up.
        WindowInterop.SetTimer(Handle, DismissTimerId, 1000, IntPtr.Zero);
    }

    /// <summary>Rounds the card at the frame, and lets DWM draw its border: a hairline exactly on
    /// the rounded edge, which a stroke of our own could only approximate. Accent while hovered, none
    /// otherwise, so it cannot read as a light edge against a dark capture.</summary>
    private void ApplyDwmChrome()
    {
        var corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        var accent = Theme.Accent;
        var border = _hovered ? accent.R | accent.G << 8 | accent.B << 16 : DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    protected override void Render(IComObject<ID2D1HwndRenderTarget> renderTarget)
    {
        using var target = renderTarget.AsRenderTarget();
        target.Object.SetDpi(96, 96);

        _resources ??= new D2DResources(target);
        _ui ??= new Ui(_resources);
        _ui.Theme = Theme;

        var client = ClientRect;
        var card = new Rect(0, 0, client.Width, client.Height);

        renderTarget.Clear(new D3DCOLORVALUE(0, 0, 0, 0));

        if (_thumbnail is null)
        {
            using var context = target.AsDeviceContext();
            if (context is null) return;
            try
            {
                // Decoded here rather than via LoadScaled, so a drag can reuse the pixels.
                _thumbnailPixels?.Dispose();
                _thumbnailPixels = ImageSurface.DecodeScaled(_card.Item.FilePath,
                    maxWidth: (int)(CardLayout.Width * 2), maxHeight: (int)(CardLayout.Height * 2));
                _thumbnail = ImageSurface.Upload(_thumbnailPixels, context);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException
                or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
            {
                return;
            }
        }

        _ui.BeginFrame(target, PointerInClient(), _pointerDown);

        // Covered edge to edge; DWM rounds the window, so the overhang is clipped with the corners.
        target.DrawBitmap(
            _thumbnail.Bitmap, 1f,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
            AnnotationRenderer.ToRect(Scaled(CardLayout.Image(new Size(_thumbnail.Width, _thumbnail.Height)))));

        if (_card.IsPinned && !_hovered) DrawPin(_ui);
        if (_hovered) DrawActions(_ui, card);

        _ui.EndFrame();

        if (_ui.ClickedThisFrame) Invalidate();
    }

    /// <summary>Dismisses the card without acting on the capture.</summary>
    public void Dismiss()
    {
        if (_dismissing) return;
        _dismissing = true;

        WindowInterop.KillTimer(Handle, DismissTimerId);
        Post(DismissCore);
    }

    /// <summary>Removed from the stack now, so the reflow closes the gap while this card is still
    /// fading.</summary>
    private void DismissCore()
    {
        Dismissed?.Invoke(this);
        StartDismissAnimation();
    }

    /// <summary>Fades the card out while drifting it left, then closes it. The whole HWND fades via
    /// WS_EX_LAYERED alpha: the card is its own top-level window, so there is nothing of ours behind
    /// it to fade into.</summary>
    private void StartDismissAnimation()
    {
        var style = GetWindowLongPtrW(Handle, GWL_EXSTYLE);
        SetWindowLongPtrW(Handle, GWL_EXSTYLE, style | WS_EX_LAYERED);

        // A freshly layered window has no alpha set and may stop painting; pin it opaque first.
        SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);

        WindowInterop.GetWindowRect(Handle, out var bounds);
        _dismissOriginX = bounds.Left;
        _dismissOriginY = bounds.Top;
        _dismissStarted = Environment.TickCount64;

        WindowInterop.SetTimer(Handle, DismissAnimationTimerId, 10, IntPtr.Zero);
    }

    private void StepDismissAnimation()
    {
        const double durationMs = 180;
        var progress = Math.Min(1, (Environment.TickCount64 - _dismissStarted) / durationMs);
        var eased = progress * (2 - progress);

        SetLayeredWindowAttributes(Handle, 0, (byte)(255 * (1 - eased)), LWA_ALPHA);

        var slide = (int)Math.Round(eased * S(8)) * CardLayout.DismissDirection(_stack.Settings.CardCorner);
        WindowInterop.SetWindowPos(Handle, HWND_TOPMOST, _dismissOriginX + slide, _dismissOriginY, 0, 0,
            SWP_NOSIZE | SWP_NOACTIVATE);

        if (progress < 1) return;

        WindowInterop.KillTimer(Handle, DismissAnimationTimerId);
        Close();
    }

    private bool _dismissing;

    private bool _savingAs;

    private long _dismissStarted;
    private int _dismissOriginX;
    private int _dismissOriginY;

    private bool _pointerDown;

    /// <summary>Where the press landed, so a drag can be told from a click.</summary>
    private Point? _pressOrigin;

    /// <summary>True when the press landed on an action button, which is not a drag.</summary>
    private bool _pressedAction;

    /// <summary>True while DoDragDrop's modal loop runs with this card as the source.</summary>
    private bool _dragging;

    private Point PointerInClient()
    {
        WindowInterop.GetCursorPos(out var screen);
        WindowInterop.ScreenToClient(Handle, ref screen);
        return new Point(screen.X, screen.Y);
    }

    protected override LRESULT? WindowProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        // A card on its way out takes no further input; only its animation timer still runs.
        if (_dismissing && msg is WmMouseMove or WmLButtonDown or WmLButtonUp or WmMouseLeave)
            return new LRESULT { Value = 0 };

        switch (msg)
        {
            case WmMouseMove:
                if (!_hovered)
                {
                    _hovered = true;
                    TrackLeave();
                    ApplyDwmChrome();
                }

                // Past the threshold, button down, not on an action: a drag out of the app.
                if (_pointerDown && _pressOrigin is { } origin && !_pressedAction)
                {
                    var now = PointerInClient();
                    if (Math.Abs(now.X - origin.X) >= 6 || Math.Abs(now.Y - origin.Y) >= 6)
                    {
                        _pointerDown = false;
                        _pressOrigin = null;

                        // DoDragDrop needs the mouse; holding capture would starve it.
                        if (WindowInterop.GetCapture() == Handle) WindowInterop.ReleaseCapture();

                        // DoDragDrop pumps timers while the cursor is off the card; hold the countdown.
                        _dragging = true;
                        bool dropped;
                        try { dropped = FileDrag.Start(_card.Item.FilePath, BuildDragImage(origin)); }
                        finally { _dragging = false; }
                        if (dropped) Post(Dismiss);

                        return new LRESULT { Value = 0 };
                    }
                }

                Invalidate();
                return new LRESULT { Value = 0 };

            case UiThreadDispatch.Message:
                _dispatch.Drain();
                return new LRESULT { Value = 0 };

            case WmMouseLeave:
                // A live press may be a drag leaving the card, so it is not cancelled here.
                _hovered = false;
                ApplyDwmChrome();
                if (!_pointerDown) _pressOrigin = null;
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmLButtonDown:
                _pointerDown = true;
                var press = PointerInClient();
                _pressOrigin = press;
                _pressedAction = CardLayout.ButtonAt(new Point(press.X / _scale, press.Y / _scale)) is not null;

                // Captured, or moves stop as the cursor leaves - exactly when a drag-out begins.
                WindowInterop.SetCapture(Handle);
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmLButtonUp:
                _pointerDown = false;
                _pressOrigin = null;
                if (WindowInterop.GetCapture() == Handle) WindowInterop.ReleaseCapture();
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmTimer:
                if ((nuint)wParam.Value == CopyFeedbackTimerId)
                {
                    StepCopyFeedback();
                    return new LRESULT { Value = 0 };
                }

                if ((nuint)wParam.Value == DismissAnimationTimerId)
                {
                    StepDismissAnimation();
                    return new LRESULT { Value = 0 };
                }

                if (_stack.Tick(_card, held: _hovered || _dragging || _copying || _savingAs)) Dismiss();
                return new LRESULT { Value = 0 };
        }
        return base.WindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>The card itself, as the picture that follows the cursor. The hotspot is where the
    /// press landed, so the image stays under the finger rather than jumping.</summary>
    private DragImage? BuildDragImage(Point press)
    {
        // The card's own pixels: this runs on the UI thread before the drag starts.
        if (_thumbnailPixels is not { } decoded) return null;

        // The press is in card space; the hotspot is in the fitted thumbnail's own pixels.
        var image = Scaled(CardLayout.Image(new Size(decoded.Width, decoded.Height)));
        var x = image.Width > 0 ? (press.X - image.X) / image.Width * decoded.Width : 0;
        var y = image.Height > 0 ? (press.Y - image.Y) / image.Height * decoded.Height : 0;

        return DragImage.FromPixels(
            decoded.Span, decoded.Width, decoded.Height,
            Math.Clamp((int)x, 0, decoded.Width),
            Math.Clamp((int)y, 0, decoded.Height));
    }

    /// <summary>Asks for WM_MOUSELEAVE, which Windows does not send unless a window opts in.</summary>
    private void TrackLeave()
    {
        var track = new TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = 0x00000002,   // TME_LEAVE
            hwndTrack = Handle,
            dwHoverTime = 0,
        };
        TrackMouseEvent(ref track);
    }

    protected override void OnDestroyed(object? sender, EventArgs e)
    {
        _dispatch.Clear();
        WindowInterop.KillTimer(Handle, CopyFeedbackTimerId);
        WindowInterop.KillTimer(Handle, DismissTimerId);
        WindowInterop.KillTimer(Handle, DismissAnimationTimerId);
        _thumbnail?.Dispose();
        _thumbnailPixels?.Dispose();
        _resources?.Dispose();
        _thumbnail = null;
        _thumbnailPixels = null;
        _resources = null;

        // Clear our reference as part of explicit window teardown.
        RenderTarget?.Dispose();
        RenderTarget = null;
        base.OnDestroyed(sender, e);
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;

    /// <summary>DWMWA_COLOR_NONE: suppresses the frame's border entirely.</summary>
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtrW(IntPtr window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtrW(IntPtr window, int index, nint value);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(
        IntPtr window, uint key, byte alpha, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TrackMouseEvent(ref TRACKMOUSEEVENT track);
}

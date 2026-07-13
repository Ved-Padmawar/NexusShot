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
public sealed class FloatingPreview : D2DRenderWindow
{
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

    /// <summary>Posted on a completed drop, so the dismissal does not run inside DoDragDrop's
    /// unwinding modal loop.</summary>
    private const uint WmDropped = 0x0400 + 1;   // WM_APP + 1

    // Design units; scaled per-monitor.
    private const double CardWidth = 168;
    private const double MinCardHeight = 56;
    private const double MaxCardHeight = 240;
    private const double StackGap = 10;
    private const double EdgeMargin = 18;

    private ScreenshotHistoryItem _item;
    private readonly int _dismissSeconds;

    private D2DResources? _resources;
    private Ui? _ui;
    private ImageSurface? _thumbnail;

    private bool _hovered;
    private int _remaining;

    public bool IsPinned { get; private set; }
    public string FilePath => _item.FilePath;

    public event Action<FloatingPreview>? Dismissed;
    public event Action<ScreenshotHistoryItem>? EditRequested;
    public event Action? PinnedChanged;

    private double _scale = 1;
    private double S(double units) => units * _scale;

    public FloatingPreview(ScreenshotHistoryItem item, int dismissSeconds)
        : base("NexusShot preview",
            (WINDOW_STYLE)WS_POPUP,
            (WINDOW_EX_STYLE)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE))
    {
        _item = item;
        _dismissSeconds = dismissSeconds;
        _remaining = dismissSeconds;
    }

    /// <summary>Re-points the card at a re-saved capture: the file is the same, the pixels are not.
    /// The stack re-flows afterwards, since the new image may be a different shape.</summary>
    public void Refresh(ScreenshotHistoryItem item)
    {
        _item = item;

        _thumbnail?.Dispose();
        _thumbnail = null;

        _remaining = _dismissSeconds;
        Invalidate();
    }

    /// <summary>The card's size in physical pixels, for the given monitor scale.</summary>
    public static Size CardSize(ScreenshotHistoryItem item, double scale)
    {
        // The card takes the capture's aspect ratio, clamped so a very tall or very wide capture
        // still produces a usable card rather than a sliver.
        var aspect = item.Width > 0 && item.Height > 0
            ? (double)item.Height / item.Width
            : 110.0 / 168.0;

        var height = Math.Clamp(CardWidth * aspect, MinCardHeight, MaxCardHeight);
        return new Size(Math.Round(CardWidth * scale), Math.Round(height * scale));
    }

    /// <summary>Places the card at its slot in the bottom-left stack.</summary>
    public void PlaceAt(RectInt workArea, double scale, double stackOffset)
    {
        // A reflow must not fight the dismissal slide.
        if (_dismissing) return;

        _scale = scale;
        var size = CardSize(_item, scale);

        var x = workArea.X + (int)Math.Round(EdgeMargin * scale);
        var y = workArea.Bottom
            - (int)Math.Round(EdgeMargin * scale)
            - (int)size.Height
            - (int)Math.Round(stackOffset);

        SetWindowPos(Handle, HWND_TOPMOST, x, y, (int)size.Width, (int)size.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // The first show can drop attributes set while the window was hidden, so the rounding is
        // re-asserted rather than set once at creation.
        ApplyDwmChrome();
    }

    /// <summary>The height this card occupies in the stack, including the gap below the next one.</summary>
    public double StackHeight(double scale) => CardSize(_item, scale).Height + StackGap * scale;

    protected override void OnCreated(object? sender, EventArgs e)
    {
        base.OnCreated(sender, e);

        ApplyDwmChrome();

        // Zero means "keep it until acted on", so no timer at all.
        if (_dismissSeconds > 0) SetTimer(Handle, DismissTimerId, 1000, IntPtr.Zero);
    }

    /// <summary>Rounds the card at the frame. DWM clips the window itself, so the corners are cut
    /// out of the thumbnail - a stroked rounded rectangle on a square window would leave the image's
    /// real corners showing through underneath. The border is suppressed so DWM's hairline cannot
    /// read as a light edge against a dark capture.</summary>
    private void ApplyDwmChrome()
    {
        var corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        var border = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    protected override void Render(IComObject<ID2D1HwndRenderTarget> renderTarget)
    {
        using var target = renderTarget.AsRenderTarget();
        target.Object.SetDpi(96, 96);

        _resources ??= new D2DResources(target);
        _ui ??= new Ui(_resources) { Theme = Theme.Dark };

        var client = ClientRect;
        var card = new Rect(0, 0, client.Width, client.Height);

        renderTarget.Clear(new D3DCOLORVALUE(0, 0, 0, 0));

        if (_thumbnail is null)
        {
            using var context = target.AsDeviceContext();
            if (context is null) return;
            try
            {
                _thumbnail = ImageSurface.LoadScaled(_item.FilePath, context,
                    maxWidth: (int)(CardWidth * 2), maxHeight: (int)(MaxCardHeight * 2));
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                return;
            }
        }

        _ui.BeginFrame(target, PointerInClient(), _pointerDown);

        // The card's height is clamped, so on an extreme capture it is not the image's aspect ratio
        // and stretching to fit would squash it. Cover and clip instead. No border: DWM rounds the
        // frame, so a stroke would be clipped at the corners.
        _ui.PushClip(card);
        target.DrawBitmap(
            _thumbnail.Bitmap, 1f,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
            AnnotationRenderer.ToRect(Cover(card)));
        _ui.PopClip();

        if (IsPinned && !_hovered) DrawPin(_ui, card);

        if (_hovered)
        {
            DrawActions(_ui, card);
            DrawClose(_ui, card);
        }

        _ui.EndFrame();
    }

    /// <summary>The rect to draw the thumbnail into so it covers the card at its own aspect ratio,
    /// centred - the overflow on the long axis is clipped away.</summary>
    private Rect Cover(Rect card)
    {
        if (_thumbnail is null || _thumbnail.Width <= 0 || _thumbnail.Height <= 0) return card;

        var scale = Math.Max(
            card.Width / _thumbnail.Width,
            card.Height / _thumbnail.Height);

        var width = _thumbnail.Width * scale;
        var height = _thumbnail.Height * scale;

        return new Rect(
            card.Center.X - width / 2,
            card.Center.Y - height / 2,
            width,
            height);
    }

    private static readonly Rgba ActionBackground = new(0x20, 0x20, 0x24, 0xE6);
    private static readonly Rgba ActionBorder = new(0xFF, 0xFF, 0xFF, 0x26);

    /// <summary>Hover and press wash white over the rest fill and leave the border alone, which is
    /// what the stock button template these were gave them.</summary>
    private static readonly Rgba ActionOverlayHover = new(0xFF, 0xFF, 0xFF, 0x0F);
    private static readonly Rgba ActionOverlayPressed = new(0xFF, 0xFF, 0xFF, 0x0A);

    private static readonly Rgba CloseBackground = new(0x32, 0x32, 0x36, 0xF2);
    private static readonly Rgba CloseHover = new(0xC4, 0x2B, 0x1C, 0xFF);
    private static readonly Rgba CloseBorder = new(0xFF, 0xFF, 0xFF, 0x59);

    /// <summary>Dismisses the card without acting on the capture.</summary>
    private void DrawClose(Ui ui, Rect card)
    {
        var size = S(18);
        var bounds = new Rect(card.Right - size - S(4), S(4), size, size);

        var clicked = ui.Interact(5, bounds);
        var hot = ui.IsHot(5) || ui.IsActive(5);

        var center = bounds.Center;
        var radius = (float)(size / 2);
        ui.FillCircle(center, radius, hot ? CloseHover : CloseBackground);
        ui.StrokeCircle(center, radius, CloseBorder);
        ui.Icon(Icons.Close, bounds, Rgba.White, S(8));

        // Acted on last: Dismiss tears the window down, and the frame still has to finish.
        if (clicked) Dismiss();
    }

    /// <summary>The hover actions: a full-card scrim behind a centred row of circular buttons.</summary>
    private void DrawActions(Ui ui, Rect card)
    {
        ui.FillRect(card, ui.Theme.HoverScrim);

        const int count = 4;
        var size = S(26);
        var spacing = S(5);
        var totalWidth = size * count + spacing * (count - 1);
        var x = card.Center.X - totalWidth / 2;
        var y = card.Center.Y - size / 2;
        var glyph = S(12);

        // Copy leaves the card up: the capture is on the clipboard, but you may still want to drag
        // it, edit it, or copy it again.
        if (ActionButton(ui, 1, new Rect(x, y, size, size), Icons.Copy, glyph, false))
        {
            ClipboardImage.Copy(_item.FilePath);
        }
        x += size + spacing;

        if (ActionButton(ui, 2, new Rect(x, y, size, size), Icons.Save, glyph, false))
        {
            SaveAs();
        }
        x += size + spacing;

        if (ActionButton(ui, 3, new Rect(x, y, size, size), Icons.Edit, glyph, false))
        {
            EditRequested?.Invoke(_item);
            Dismiss();
        }
        x += size + spacing;

        // Pin: the accent when engaged, so its state is legible without a label.
        if (ActionButton(ui, 4, new Rect(x, y, size, size), Icons.Pin, glyph, IsPinned))
        {
            IsPinned = !IsPinned;
            _remaining = _dismissSeconds;
            PinnedChanged?.Invoke();
        }
    }

    /// <summary>A circular overlay action button, washed a little lighter on hover and press.</summary>
    private bool ActionButton(Ui ui, int id, Rect bounds, string glyph, double glyphSize, bool selected)
    {
        var clicked = ui.Interact(id, bounds);

        var center = bounds.Center;
        var radius = (float)(bounds.Width / 2);

        ui.FillCircle(center, radius, selected ? ui.Theme.Accent : ActionBackground);

        // Over whatever the button already is, so an engaged pin brightens from the accent rather
        // than snapping back to grey.
        if (ui.IsActive(id)) ui.FillCircle(center, radius, ActionOverlayPressed);
        else if (ui.IsHot(id)) ui.FillCircle(center, radius, ActionOverlayHover);

        ui.StrokeCircle(center, radius, ActionBorder);
        ui.Icon(glyph, bounds, Rgba.White, glyphSize);

        return clicked;
    }

    /// <summary>A pinned card that is not hovered still says so, quietly, in the corner.</summary>
    private void DrawPin(Ui ui, Rect card)
    {
        var badge = new Rect(card.Right - S(26), S(6), S(20), S(20));
        ui.FillRounded(badge, (float)S(4), ui.Theme.HoverScrim);
        ui.Icon(Icons.Pin, badge, ui.Theme.Accent, S(11));
    }

    /// <summary>Writes a copy wherever the user picks, then dismisses: the capture has landed
    /// somewhere permanent, so the card has done its job.</summary>
    private void SaveAs()
    {
        var suggested = Path.GetFileName(_item.FilePath);
        if (FilePicker.SavePng(Handle, suggested, Path.GetDirectoryName(_item.FilePath))
            is not { } destination) return;

        try
        {
            File.Copy(_item.FilePath, destination, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Dismiss();
    }

    /// <summary>Dismisses the card without acting on the capture.</summary>
    public void Dismiss()
    {
        if (_dismissing) return;
        _dismissing = true;

        KillTimer(Handle, DismissTimerId);

        // Removed from the stack now, so the reflow closes the gap while this card is still fading.
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

        GetWindowRect(Handle, out var bounds);
        _dismissOriginX = bounds.Left;
        _dismissOriginY = bounds.Top;
        _dismissStarted = Environment.TickCount64;

        SetTimer(Handle, DismissAnimationTimerId, 10, IntPtr.Zero);
    }

    private void StepDismissAnimation()
    {
        const double durationMs = 180;
        var progress = Math.Min(1, (Environment.TickCount64 - _dismissStarted) / durationMs);
        var eased = progress * (2 - progress);

        SetLayeredWindowAttributes(Handle, 0, (byte)(255 * (1 - eased)), LWA_ALPHA);

        var slide = (int)Math.Round(eased * S(8));
        SetWindowPos(Handle, HWND_TOPMOST, _dismissOriginX - slide, _dismissOriginY, 0, 0,
            SWP_NOSIZE | SWP_NOACTIVATE);

        if (progress < 1) return;

        KillTimer(Handle, DismissAnimationTimerId);
        Close();
    }

    private bool _dismissing;
    private long _dismissStarted;
    private int _dismissOriginX;
    private int _dismissOriginY;

    private bool _pointerDown;

    /// <summary>Where the press landed, so a drag can be told from a click.</summary>
    private Point? _pressOrigin;

    /// <summary>True when the press landed on an action button, which is not a drag.</summary>
    private bool _pressedAction;

    private Point PointerInClient()
    {
        GetCursorPos(out var screen);
        ScreenToClient(Handle, ref screen);
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
                }

                // Past the threshold with the button down, and not on an action: the user is
                // dragging the capture out of the app rather than clicking it.
                if (_pointerDown && _pressOrigin is { } origin && !_pressedAction)
                {
                    var now = PointerInClient();
                    if (Math.Abs(now.X - origin.X) >= 6 || Math.Abs(now.Y - origin.Y) >= 6)
                    {
                        _pointerDown = false;
                        _pressOrigin = null;

                        // DoDragDrop needs the mouse; holding capture would starve it.
                        if (GetCapture() == Handle) ReleaseCapture();

                        // A completed drop means the capture reached its destination, so the card is
                        // done. Posted rather than called - see WmDropped.
                        if (FileDrag.Start(_item.FilePath, BuildDragImage(origin)))
                            PostMessageW(Handle, WmDropped, IntPtr.Zero, IntPtr.Zero);

                        return new LRESULT { Value = 0 };
                    }
                }

                Invalidate();
                return new LRESULT { Value = 0 };

            case WmDropped:
                Dismiss();
                return new LRESULT { Value = 0 };

            case WmMouseLeave:
                // A live press is a drag beginning - leaving the card is how it starts, so it must
                // not be cancelled here.
                _hovered = false;
                if (!_pointerDown) _pressOrigin = null;
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmLButtonDown:
                _pointerDown = true;
                _pressOrigin = PointerInClient();

                _pressedAction = _ui?.WantsPointer ?? false;

                // Without capture the moves stop arriving the moment the cursor clears the card -
                // which is exactly when a drag-out passes its threshold.
                SetCapture(Handle);
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmLButtonUp:
                _pointerDown = false;
                _pressOrigin = null;
                if (GetCapture() == Handle) ReleaseCapture();
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmTimer:
                if ((nuint)wParam.Value == DismissAnimationTimerId)
                {
                    StepDismissAnimation();
                    return new LRESULT { Value = 0 };
                }

                // The countdown pauses under the pointer and while pinned: a timer that runs while
                // you are reaching for a button will eventually lose you a capture.
                if (_hovered || IsPinned)
                {
                    _remaining = _dismissSeconds;
                    return new LRESULT { Value = 0 };
                }
                if (--_remaining <= 0) Dismiss();
                return new LRESULT { Value = 0 };
        }
        return base.WindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>The card itself, as the picture that follows the cursor. The hotspot is where the
    /// press landed, so the image stays under the finger rather than jumping.</summary>
    private DragImage? BuildDragImage(Point press)
    {
        try
        {
            var size = CardSize(_item, _scale);
            var width = Math.Max(1, (int)size.Width);
            var height = Math.Max(1, (int)size.Height);

            var decoded = ImageSurface.DecodeScaled(_item.FilePath, width, height);

            return DragImage.FromPixels(
                decoded.Pixels, decoded.Width, decoded.Height,
                Math.Clamp((int)press.X, 0, decoded.Width),
                Math.Clamp((int)press.Y, 0, decoded.Height));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // No picture is better than no drag.
            return null;
        }
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
        KillTimer(Handle, DismissTimerId);
        KillTimer(Handle, DismissAnimationTimerId);
        _thumbnail?.Dispose();
        _resources?.Dispose();
        _thumbnail = null;
        _resources = null;
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(IntPtr window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(
        IntPtr window, uint key, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out RECT bounds);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr window, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();

    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref POINT point);
    [DllImport("user32.dll")] private static extern nuint SetTimer(IntPtr window, nuint id, uint elapse, IntPtr callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr window, nuint id);
}

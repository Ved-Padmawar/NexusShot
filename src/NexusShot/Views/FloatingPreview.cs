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

    // Design units; scaled per-monitor.
    private const double CardWidth = 168;
    private const double MinCardHeight = 56;
    private const double MaxCardHeight = 240;
    private const double StackGap = 10;
    private const double EdgeMargin = 18;
    private const double ActionBar = 34;

    private readonly ScreenshotHistoryItem _item;
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
        _scale = scale;
        var size = CardSize(_item, scale);

        var x = workArea.X + (int)Math.Round(EdgeMargin * scale);
        var y = workArea.Bottom
            - (int)Math.Round(EdgeMargin * scale)
            - (int)size.Height
            - (int)Math.Round(stackOffset);

        SetWindowPos(Handle, HWND_TOPMOST, x, y, (int)size.Width, (int)size.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>The height this card occupies in the stack, including the gap below the next one.</summary>
    public double StackHeight(double scale) => CardSize(_item, scale).Height + StackGap * scale;

    protected override void OnCreated(object? sender, EventArgs e)
    {
        base.OnCreated(sender, e);

        // Zero means "keep it until acted on", so no timer at all.
        if (_dismissSeconds > 0) SetTimer(Handle, DismissTimerId, 1000, IntPtr.Zero);
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

        // The image fills the card: the window is already the capture's aspect ratio, so there is
        // nothing to letterbox.
        target.DrawBitmap(
            _thumbnail.Bitmap, 1f,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
            AnnotationRenderer.ToRect(card));

        _ui.StrokeRounded(card, (float)S(6), _ui.Theme.StrokeStrong);

        if (IsPinned && !_hovered) DrawPin(_ui, card);
        if (_hovered) DrawActions(_ui, card);

        _ui.EndFrame();
    }

    /// <summary>The hover actions, on a scrim over the bottom of the card.</summary>
    private void DrawActions(Ui ui, Rect card)
    {
        var bar = new Rect(0, card.Bottom - S(ActionBar), card.Width, S(ActionBar));
        ui.FillRect(bar, ui.Theme.HoverScrim);

        var size = S(28);
        var gap = (bar.Width - size * 4) / 5;
        var x = gap;
        var y = bar.Y + (bar.Height - size) / 2;
        var glyph = S(13);

        if (ui.Tile(1, new Rect(x, y, size, size), false, Icons.Copy, glyph, tint: Rgba.White))
        {
            ClipboardImage.Copy(_item.FilePath);
            Dismiss();
        }
        x += size + gap;

        if (ui.Tile(2, new Rect(x, y, size, size), false, Icons.Reveal, glyph, tint: Rgba.White))
        {
            Reveal();
        }
        x += size + gap;

        if (ui.Tile(3, new Rect(x, y, size, size), false, Icons.Edit, glyph, tint: Rgba.White))
        {
            EditRequested?.Invoke(_item);
            Dismiss();
        }
        x += size + gap;

        // Pin: the accent when engaged, so its state is legible without a label.
        if (ui.Tile(4, new Rect(x, y, size, size), IsPinned, Icons.Pin, glyph,
            tint: IsPinned ? ui.Theme.Accent : Rgba.White))
        {
            IsPinned = !IsPinned;
            _remaining = _dismissSeconds;
            PinnedChanged?.Invoke();
        }
    }

    /// <summary>A pinned card that is not hovered still says so, quietly, in the corner.</summary>
    private void DrawPin(Ui ui, Rect card)
    {
        var badge = new Rect(card.Right - S(26), S(6), S(20), S(20));
        ui.FillRounded(badge, (float)S(4), ui.Theme.HoverScrim);
        ui.Icon(Icons.Pin, badge, ui.Theme.Accent, S(11));
    }

    private void Reveal()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_item.FilePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is IOException
            or System.ComponentModel.Win32Exception)
        {
            // Explorer not opening is not worth taking the app down for.
        }
    }

    public void Dismiss()
    {
        KillTimer(Handle, DismissTimerId);
        Dismissed?.Invoke(this);
        Close();
    }

    private bool _pointerDown;

    /// <summary>Where the press landed, so a drag can be told from a click.</summary>
    private Point? _pressOrigin;

    private Point PointerInClient()
    {
        GetCursorPos(out var screen);
        ScreenToClient(Handle, ref screen);
        return new Point(screen.X, screen.Y);
    }

    protected override LRESULT? WindowProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
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
                if (_pointerDown && _pressOrigin is { } origin && !(_ui?.WantsPointer ?? false))
                {
                    var now = PointerInClient();
                    if (Math.Abs(now.X - origin.X) >= 6 || Math.Abs(now.Y - origin.Y) >= 6)
                    {
                        _pointerDown = false;
                        _pressOrigin = null;
                        FileDrag.Start(_item.FilePath);
                        return new LRESULT { Value = 0 };
                    }
                }

                Invalidate();
                return new LRESULT { Value = 0 };

            case WmMouseLeave:
                _hovered = false;
                _pointerDown = false;
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmLButtonDown:
                _pointerDown = true;
                _pressOrigin = PointerInClient();
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmLButtonUp:
                _pointerDown = false;
                _pressOrigin = null;
                Invalidate();
                return new LRESULT { Value = 0 };

            case WmTimer:
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
        _thumbnail?.Dispose();
        _resources?.Dispose();
        _thumbnail = null;
        _resources = null;
        base.OnDestroyed(sender, e);
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

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

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref POINT point);
    [DllImport("user32.dll")] private static extern nuint SetTimer(IntPtr window, nuint id, uint elapse, IntPtr callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr window, nuint id);
}

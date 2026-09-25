using System.Runtime.InteropServices;
using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// A window whose content extends into the titlebar. The caption is not a strip above the client
/// area, it *is* the client area, painted by the app, with only the system buttons floating on top.
///
/// WM_NCCALCSIZE claims the caption's height while leaving the resize borders alone; WM_NCHITTEST
/// hands back the drag region and the resize edges, so the window still snaps and resizes.
/// </summary>
public abstract class CaptionWindow : D2DRenderWindow
{
    protected CaptionWindow(string title) : base(title) { }

    protected override void CreateRenderTarget()
    {
        RenderTarget?.Dispose();
        RenderTarget = null;
        RenderTarget = GraphicsBackend.CreateWindowTarget(Handle, ClientRect.Size.ToD2D_SIZE_U(), FactoryType, FactoryOptions);
    }

    // DirectNAot 1.6.6 loads a fresh owned icon before every class-registration attempt,
    // even when the class already exists, and never disposes that icon. Windows below
    // install our process-shared icons explicitly; no additional class icon is needed.
    protected override DirectN.Extensions.Utilities.Icon? LoadCreationIcon() => null;

    /// <summary>The caption's height in physical pixels: how far the client area now reaches up.</summary>
    public double CaptionHeight => 32 * DpiScale;

    /// <summary>Cached: a syscall, read several times per frame, that only changes on
    /// WM_DPICHANGED.</summary>
    protected double DpiScale => _dpiScale ??= Functions.GetDpiForWindow(Handle) / 96.0;
    private double? _dpiScale;

    protected void InvalidateDpiScale() => _dpiScale = null;

    /// <summary>The width the three system buttons occupy at the top-right; content must not run
    /// under them.</summary>
    public double CaptionButtonsWidth => 3 * 46 * DpiScale;

    /// <summary>The pointer in client pixels, read from Windows now rather than from the last mouse
    /// message: WM_SETCURSOR arrives before the WM_MOUSEMOVE that would report it.</summary>
    protected Point? PointerNow()
    {
        if (!Functions.GetCursorPos(out var point)) return null;
        if (!Functions.ScreenToClient(new HWND { Value = Handle }, ref point)) return null;
        return new Point(point.x, point.y);
    }

    /// <summary>The system cursor for what the chrome's control under the pointer asked for.</summary>
    protected static IntPtr SystemCursor(PointerCursor cursor) => cursor switch
    {
        PointerCursor.Hand => ToolCursors.Hand,
        PointerCursor.Text => ToolCursors.Text,
        _ => ToolCursors.Arrow,
    };

    /// <summary>True while the window is maximised, which changes the restore glyph and the insets.</summary>
    protected bool IsMaximised => WindowInterop.IsZoomedWindow(Handle);

    /// <summary>Whether a client point falls in the region that drags the window. Everything that is
    /// not a control in the top band should be draggable, so each window says which is which.</summary>
    protected abstract bool IsDragRegion(Point client);

    /// <summary>How far down the window its drag band reaches. The caption height by default; a window
    /// whose title area is taller than the system buttons extends it.</summary>
    protected virtual double DragBandHeight => CaptionHeight;

    protected override void OnCreated(object? sender, EventArgs e)
    {
        base.OnCreated(sender, e);

        // Frame changes apply only on request; without this the caption stays until a resize.
        WindowInterop.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    /// <summary>Ui keys hover and press by id alone, so an id shared with a widget in the window's
    /// own content makes the two light up together.</summary>
    private const int CaptionIdBase = 900_001;

    /// <summary>
    /// Keeps the window repainting at <paramref name="interval"/> milliseconds, or stops with null.
    /// Idempotent, so a window calls it after every frame with what that frame still needs: display
    /// rate while something moves, a slow tick for a blinking caret, nothing once all is still.
    /// </summary>
    protected void Repaint(uint? interval)
    {
        if (interval == _repaint) return;
        _repaint = interval;
        if (interval is { } ms) WindowInterop.SetTimer(Handle, RepaintTimerId, ms, IntPtr.Zero);
        else WindowInterop.KillTimer(Handle, RepaintTimerId);
    }

    private uint? _repaint;
    private const nuint RepaintTimerId = 0x7E57;

    /// <summary>Display rate for motion. Windows rounds timer periods to its tick, so this lands near
    /// 60 Hz rather than exactly on it; the animations are time-based, so that costs smoothness only.</summary>
    protected const uint FrameInterval = 16;

    /// <summary>Minimise, maximise/restore, close - drawn by us, in our theme, over our own pixels.</summary>
    protected void DrawCaptionButtons(Ui ui, double width)
    {
        var height = CaptionHeight;
        var button = 46 * DpiScale;
        var glyph = 10 * DpiScale;

        var x = width - button * 3;

        if (CaptionButton(ui, CaptionIdBase, new Rect(x, 0, button, height),
            Icons.CaptionMinimise, glyph, false))
            QueueSystemCommand(SC_MINIMIZE);

        x += button;

        var maximised = IsMaximised;
        if (CaptionButton(ui, CaptionIdBase + 1, new Rect(x, 0, button, height),
            maximised ? Icons.CaptionRestore : Icons.CaptionMaximise, glyph, false))
            QueueSystemCommand(maximised ? SC_RESTORE : SC_MAXIMIZE);

        x += button;

        // Close alone gets the red hover, which is the one convention users actually rely on.
        if (CaptionButton(ui, CaptionIdBase + 2, new Rect(x, 0, button, height),
            Icons.CaptionClose, glyph, true))
            WindowInterop.PostMessageW(Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Defers state changes until the current WM_PAINT has completed. Calling ShowWindow
    /// here would synchronously send WM_SIZE while Direct2D is between BeginDraw and EndDraw, and
    /// resizing a render target in that state fails with D2DERR_WRONG_STATE.</summary>
    private void QueueSystemCommand(int command) =>
        _ = WindowInterop.PostMessageW(Handle, WM_SYSCOMMAND, new IntPtr(command), IntPtr.Zero);

    private static readonly Rgba ClosePressed = new(0xC8, 0x4B, 0x3F, 0xFF);

    /// <summary>Whether this window is the foreground one. An inactive window's caption glyphs dim,
    /// as the system's do.</summary>
    protected bool IsActiveWindow => WindowInterop.GetForegroundWindow() == Handle;

    private bool CaptionButton(Ui ui, int id, Rect bounds, Icon glyph, double size, bool danger)
    {
        // The arrow, as every window's own caption buttons keep it.
        var clicked = ui.Interact(id, bounds, PointerCursor.Arrow);
        var hot = ui.IsHot(id);
        var active = ui.IsActive(id);

        var fill = danger
            ? active ? ClosePressed : hot ? Theme.CaptionClose : default
            : active ? ui.Theme.SurfacePressed : hot ? ui.Theme.SurfaceHover : default;

        if (fill.A > 0) ui.FillRect(bounds, fill);

        var foreground = danger && (hot || active) ? Rgba.White
            : hot ? ui.Theme.TextPrimary
            : IsActiveWindow ? ui.Theme.TextSecondary : ui.Theme.TextTertiary;
        ui.Icon(glyph, bounds, foreground, size);

        return clicked;
    }

    protected override LRESULT? WindowProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                // Claim the erase: the white class brush would flash before the first D2D frame.
                return new LRESULT { Value = 1 };

            case WM_NCCALCSIZE when wParam.Value != 0:
                return OnNcCalcSize(lParam);

            case WM_NCHITTEST:
                return OnNcHitTest(lParam);

            case WM_TIMER when (nuint)wParam.Value == RepaintTimerId:
                Invalidate();
                return new LRESULT { Value = 0 };

            case WM_ACTIVATE:
                // The caption glyphs dim with focus, and they are ours to repaint.
                Invalidate();
                break;

            case WM_DPICHANGED:
                // Dragged to a monitor at a different scale; every caption metric derives from it.
                InvalidateDpiScale();
                Invalidate();
                break;
        }
        return base.WindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Claims the caption for the client area, leaving the other three sides to the default
    /// frame so the resize borders survive. A maximised window is inset by the border thickness:
    /// Windows oversizes it by exactly that, and ignoring it crops the top of the content.</summary>
    private unsafe LRESULT OnNcCalcSize(LPARAM lParam)
    {
        var parameters = (NCCALCSIZE_PARAMS*)lParam.Value;
        var original = parameters->rgrc0;

        // Let the default frame compute the borders, then give the caption back.
        _ = WindowInterop.DefWindowProcW(Handle, WM_NCCALCSIZE, 1, lParam.Value);

        parameters->rgrc0.Top = original.Top;

        if (IsMaximised)
        {
            var border = WindowInterop.GetSystemMetrics(SM_CYSIZEFRAME) + WindowInterop.GetSystemMetrics(SM_CXPADDEDBORDER);
            parameters->rgrc0.Top += border;
        }

        return new LRESULT { Value = 0 };
    }

    /// <summary>Hands the frame back what it still owns. The resize edges come first: the top edge
    /// overlaps the caption we just claimed, and losing it would leave a window you cannot resize
    /// from the top.</summary>
    private LRESULT OnNcHitTest(LPARAM lParam)
    {
        var value = lParam.Value.ToInt64();
        var screen = new WindowInterop.POINT { X = (short)(value & 0xFFFF), Y = (short)((value >> 16) & 0xFFFF) };

        var point = screen;
        WindowInterop.ScreenToClient(Handle, ref point);
        WindowInterop.GetClientRect(Handle, out var client);

        var border = (int)Math.Round(8 * DpiScale);

        // A maximised window has no resize edges.
        if (!IsMaximised)
        {
            var left = point.X < border;
            var right = point.X >= client.Right - border;
            var top = point.Y < border;
            var bottom = point.Y >= client.Bottom - border;

            var hit = (top, bottom, left, right) switch
            {
                (true, _, true, _) => HTTOPLEFT,
                (true, _, _, true) => HTTOPRIGHT,
                (_, true, true, _) => HTBOTTOMLEFT,
                (_, true, _, true) => HTBOTTOMRIGHT,
                (true, _, _, _) => HTTOP,
                (_, true, _, _) => HTBOTTOM,
                (_, _, true, _) => HTLEFT,
                (_, _, _, true) => HTRIGHT,
                _ => 0,
            };

            if (hit != 0) return new LRESULT { Value = hit };
        }

        if (point.Y < DragBandHeight && IsDragRegion(new Point(point.X, point.Y)))
            return new LRESULT { Value = HTCAPTION };

        return new LRESULT { Value = HTCLIENT };
    }

    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_ACTIVATE = 0x0006;
    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_DPICHANGED = 0x02E0;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint WM_CLOSE = 0x0010;

    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;

    private const int SC_MINIMIZE = 0xF020;
    private const int SC_MAXIMIZE = 0xF030;
    private const int SC_RESTORE = 0xF120;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct NCCALCSIZE_PARAMS
    {
        public WindowInterop.RECT rgrc0, rgrc1, rgrc2;
        public IntPtr lppos;
    }
}

using System.Runtime.InteropServices;
using NexusShot.Core;
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

    /// <summary>The caption's height in physical pixels: how far the client area now reaches up.</summary>
    public double CaptionHeight => 32 * DpiScale;

    protected double DpiScale => Functions.GetDpiForWindow(Handle) / 96.0;

    /// <summary>The width the three system buttons occupy at the top-right; content must not run
    /// under them.</summary>
    public double CaptionButtonsWidth => 3 * 46 * DpiScale;

    /// <summary>True while the window is maximised, which changes the restore glyph and the insets.</summary>
    protected bool IsMaximised => IsZoomedWindow(Handle);

    /// <summary>Whether a client point falls in the region that drags the window. Everything that is
    /// not a control in the top strip should be draggable, so each window says which is which.</summary>
    protected abstract bool IsDragRegion(Point client);

    protected override void OnCreated(object? sender, EventArgs e)
    {
        base.OnCreated(sender, e);

        // The frame is only recalculated on request, so ask for one now that we intend to handle
        // WM_NCCALCSIZE - otherwise the caption stays until the first resize.
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    /// <summary>Minimise, maximise/restore, close - drawn by us, in our theme, over our own pixels.</summary>
    protected void DrawCaptionButtons(Ui ui, double width)
    {
        var height = CaptionHeight;
        var button = 46 * DpiScale;
        var glyph = 10 * DpiScale;

        var x = width - button * 3;

        if (CaptionButton(ui, 9001, new Rect(x, 0, button, height), Icons.CaptionMinimise, glyph, false))
            QueueSystemCommand(SC_MINIMIZE);

        x += button;

        var maximised = IsMaximised;
        if (CaptionButton(ui, 9002, new Rect(x, 0, button, height),
            maximised ? Icons.CaptionRestore : Icons.CaptionMaximise, glyph, false))
            QueueSystemCommand(maximised ? SC_RESTORE : SC_MAXIMIZE);

        x += button;

        // Close alone gets the red hover, which is the one convention users actually rely on.
        if (CaptionButton(ui, 9003, new Rect(x, 0, button, height), Icons.CaptionClose, glyph, true))
            PostMessageW(Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Defers state changes until the current WM_PAINT has completed. Calling ShowWindow
    /// here would synchronously send WM_SIZE while Direct2D is between BeginDraw and EndDraw, and
    /// resizing a render target in that state fails with D2DERR_WRONG_STATE.</summary>
    private void QueueSystemCommand(int command) =>
        _ = PostMessageW(Handle, WM_SYSCOMMAND, new IntPtr(command), IntPtr.Zero);

    private static readonly Rgba CloseHover = new(0xC4, 0x2B, 0x1C, 0xFF);
    private static readonly Rgba ClosePressed = new(0xC8, 0x4B, 0x3F, 0xFF);

    private bool CaptionButton(Ui ui, int id, Rect bounds, string glyph, double size, bool danger)
    {
        var clicked = ui.Interact(id, bounds);
        var hot = ui.IsHot(id);
        var active = ui.IsActive(id);

        var fill = danger
            ? active ? ClosePressed : hot ? CloseHover : default
            : active ? ui.Theme.FillPressed : hot ? ui.Theme.FillHover : default;

        if (fill.A > 0) ui.FillRect(bounds, fill);

        var foreground = danger && (hot || active) ? Rgba.White : ui.Theme.TextSecondary;
        ui.Icon(glyph, bounds, foreground, size);

        return clicked;
    }

    protected override LRESULT? WindowProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                // Claim the erase without doing it. The class brush is white, and letting Windows
                // paint it before the first D2D frame is what flashes white on open.
                return new LRESULT { Value = 1 };

            case WM_NCCALCSIZE when wParam.Value != 0:
                return OnNcCalcSize(lParam);

            case WM_NCHITTEST:
                return OnNcHitTest(lParam);
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
        _ = DefWindowProcW(Handle, WM_NCCALCSIZE, 1, lParam.Value);

        parameters->rgrc0.Top = original.Top;

        if (IsMaximised)
        {
            var border = GetSystemMetrics(SM_CYSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
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
        var screen = new POINT { X = (short)(value & 0xFFFF), Y = (short)((value >> 16) & 0xFFFF) };

        var point = screen;
        ScreenToClient(Handle, ref point);
        GetClientRect(Handle, out var client);

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

        if (point.Y < CaptionHeight && IsDragRegion(new Point(point.X, point.Y)))
            return new LRESULT { Value = HTCAPTION };

        return new LRESULT { Value = HTCLIENT };
    }

    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint WM_NCHITTEST = 0x0084;
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
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NCCALCSIZE_PARAMS
    {
        public RECT rgrc0, rgrc1, rgrc2;
        public IntPtr lppos;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr window, uint msg, nuint wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr window, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out RECT client);

    [DllImport("user32.dll", EntryPoint = "IsZoomed")]
    private static extern bool IsZoomedWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr window, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;
using static NexusShot.Views.Win32Window;

namespace NexusShot.Views;

/// <summary>
/// The timed-capture countdown. Never activates and lets clicks through: activating would close
/// the menu or tooltip the timer exists to capture.
/// </summary>
public sealed partial class CountdownBadge : PopupWindow
{
    private const nuint TickTimerId = 1;

    private int _remaining;
    private bool _cancelled;
    private Ui? _ui;
    private double _scale = 1;

    /// <summary>Raised at zero, once the badge is off screen and cannot appear in the capture.</summary>
    public event Action? Elapsed;
    public event Action? Dismissed;

    public CountdownBadge(int seconds)
        : base("NexusShot timer", WS_EX_TRANSPARENT | WS_EX_NOACTIVATE)
    {
        _remaining = seconds;
    }

    public void Start()
    {
        var work = Monitors.WorkAreaUnderCursor();
        _scale = Monitors.DpiScaleUnderCursor(Handle);
        var size = (int)Math.Round(64 * _scale);

        WindowInterop.SetWindowPos(Handle, HWND_TOPMOST,
            work.X + (work.Width - size) / 2, work.Y + (int)Math.Round(24 * _scale), size, size,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);

        WindowInterop.SetDwmAttribute(Handle, WindowInterop.DWMWA_WINDOW_CORNER_PREFERENCE, WindowInterop.DWMWCP_ROUND);

        WindowInterop.SetTimer(Handle, TickTimerId, 1000, IntPtr.Zero);
    }

    /// <summary>Stops the countdown without capturing.</summary>
    public void Cancel()
    {
        _cancelled = true;
        Close();
    }

    protected override void Render(IComObject<ID2D1HwndRenderTarget> renderTarget)
    {
        using var target = renderTarget.AsRenderTarget();
        target.Object.SetDpi(96, 96);

        _ui ??= new Ui(Resources(target)) { Theme = Theme.Dark };

        var client = ClientRect;
        var bounds = new Rect(0, 0, client.Width, client.Height);
        _ui.BeginFrame(target, new Point(-1, -1));
        _ui.FillRect(bounds, _ui.Theme.SurfaceWindow);
        _ui.Text(_remaining.ToString(), bounds, _ui.Theme.TextPrimary, 28 * _scale,
            Weight.Bold, TextAlign.Center, face: Face.Display);
        _ui.EndFrame();
    }

    protected override LRESULT? WindowProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (msg == WmTimer && (nuint)wParam.Value == TickTimerId)
        {
            if (--_remaining > 0)
            {
                Invalidate();
                return new LRESULT { Value = 0 };
            }

            WindowInterop.KillTimer(Handle, TickTimerId);

            // A destroyed window can still be in the next composed frame, so hide and flush first.
            WindowInterop.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW);
            WindowInterop.DwmFlush();

            Close();
            return new LRESULT { Value = 0 };
        }
        return base.WindowProc(hwnd, msg, wParam, lParam);
    }

    protected override void OnDestroyed(object? sender, EventArgs e)
    {
        WindowInterop.KillTimer(Handle, TickTimerId);
        base.OnDestroyed(sender, e);

        Dismissed?.Invoke();
        if (!_cancelled && _remaining <= 0) Elapsed?.Invoke();
    }

}

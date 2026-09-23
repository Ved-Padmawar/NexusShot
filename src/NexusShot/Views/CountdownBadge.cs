using System.Runtime.InteropServices;
using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The timed-capture countdown. Never activates and lets clicks through: activating would close
/// the menu or tooltip the timer exists to capture.
/// </summary>
public sealed partial class CountdownBadge : D2DRenderWindow
{
    protected override void CreateRenderTarget()
    {
        RenderTarget?.Dispose();
        RenderTarget = null;
        RenderTarget = GraphicsBackend.CreateWindowTarget(Handle, ClientRect.Size.ToD2D_SIZE_U(), FactoryType, FactoryOptions, software: true);
    }

    protected override DirectN.Extensions.Utilities.Icon? LoadCreationIcon() => null;

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

    private const uint WmTimer = 0x0113;
    private const nuint TickTimerId = 1;

    private int _remaining;
    private bool _cancelled;
    private D2DResources? _resources;
    private Ui? _ui;
    private double _scale = 1;

    /// <summary>Raised at zero, once the badge is off screen and cannot appear in the capture.</summary>
    public event Action? Elapsed;
    public event Action? Dismissed;

    public CountdownBadge(int seconds)
        : base("NexusShot timer",
            (WINDOW_STYLE)WS_POPUP,
            (WINDOW_EX_STYLE)(WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE))
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

        var corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

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

        _resources ??= new D2DResources(target);
        _ui ??= new Ui(_resources) { Theme = Theme.Dark };

        var client = ClientRect;
        var bounds = new Rect(0, 0, client.Width, client.Height);
        _ui.BeginFrame(target, new Point(-1, -1), false);
        _ui.FillRect(bounds, _ui.Theme.SurfaceBase);
        _ui.Text(_remaining.ToString(), bounds, _ui.Theme.TextPrimary, (float)(28 * _scale),
            bold: true, align: TextAlign.Center);
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
            DwmFlush();

            Close();
            return new LRESULT { Value = 0 };
        }
        return base.WindowProc(hwnd, msg, wParam, lParam);
    }

    protected override void OnDestroyed(object? sender, EventArgs e)
    {
        WindowInterop.KillTimer(Handle, TickTimerId);
        _resources?.Dispose();
        _resources = null;
        RenderTarget?.Dispose();
        RenderTarget = null;
        base.OnDestroyed(sender, e);

        Dismissed?.Invoke();
        if (!_cancelled && _remaining <= 0) Elapsed?.Invoke();
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmFlush();
}

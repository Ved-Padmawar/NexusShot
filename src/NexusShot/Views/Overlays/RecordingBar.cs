using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;
using static NexusShot.Views.Win32Window;

namespace NexusShot.Views;

/// <summary>
/// The controls while recording: the elapsed time, pause, stop and discard, at the top of the recorded
/// monitor. It never takes focus, so the app being recorded keeps it, and it is excluded from capture -
/// where exclusion fails it stays hidden rather than appear in the file, and the tray still stops it.
/// </summary>
internal sealed class RecordingBar(RecordingState state, TimeSpan? limit, Theme theme)
    : PopupWindow("NexusShot recording", WS_EX_NOACTIVATE)
{
    private const nuint TimerId = 1;

    /// <summary>In design units; wide enough for "00:00 / 00:00" beside the three controls.</summary>
    private const double Width = 296;
    private const double Height = 44;

    private Ui? _ui;
    private Point _pointer = new(-1, -1);
    private double _scale = 1;

    public event Action? PauseRequested;

    /// <summary>True to discard.</summary>
    public event Action<bool>? StopRequested;

    private double S(double units) => units * _scale;

    public void Open(RectInt area)
    {
        if (!WindowInterop.ExcludeFromCapture(Handle)) return;
        var (x, y) = (area.X + area.Width / 2, area.Y + area.Height / 2);
        var work = Monitors.WorkAreaAt(x, y);
        _scale = Monitors.DpiScaleAt(x, y);
        var width = (int)Math.Round(S(Width));
        WindowInterop.SetWindowPos(Handle, HWND_TOPMOST, work.X + (work.Width - width) / 2, work.Y + (int)Math.Round(S(16)),
            width, (int)Math.Round(S(Height)), SWP_NOACTIVATE | SWP_SHOWWINDOW);
        WindowInterop.SetDwmAttribute(Handle, WindowInterop.DWMWA_WINDOW_CORNER_PREFERENCE, WindowInterop.DWMWCP_ROUND);
        WindowInterop.SetTimer(Handle, TimerId, 250, IntPtr.Zero);
    }

    protected override void Render(IComObject<ID2D1HwndRenderTarget> renderTarget)
    {
        using var target = renderTarget.AsRenderTarget();
        target.Object.SetDpi(96, 96);
        _ui ??= new Ui(Resources(target)) { Theme = theme, Scale = _scale };
        var ui = _ui;
        var bounds = new Rect(0, 0, ClientRect.Width, ClientRect.Height);
        var row = bounds.Deflate(S(7));
        ui.BeginFrame(target, _pointer);
        ui.FillRect(bounds, theme.SurfaceWindow);

        var paused = state.Phase == RecordingPhase.Paused;
        var saving = state.Phase == RecordingPhase.Finalizing;
        ui.Inert = !state.IsCapturing;

        var right = row.Right;
        right -= S(30);
        if (ui.IconButton(Ui.Id("recording.discard"), new Rect(right, row.Y, S(30), row.Height), Icons.Delete, "Discard",
            destructive: true, iconSize: 16))
            StopRequested?.Invoke(true);
        var stop = ui.ButtonWidth("Stop", Icons.Stop, small: true);
        right -= S(4) + stop;
        if (ui.Button(Ui.Id("recording.stop"), new Rect(right, row.Y, stop, row.Height), "Stop", ButtonStyle.Primary, Icons.Stop,
            small: true, tooltip: "Stop and save"))
            StopRequested?.Invoke(false);
        right -= S(4) + S(30);
        if (ui.IconButton(Ui.Id("recording.pause"), new Rect(right, row.Y, S(30), row.Height), paused ? Icons.Play : Icons.Pause,
            paused ? "Resume" : "Pause", iconSize: 16))
            PauseRequested?.Invoke();
        ui.Separator(new Point(right - S(7), row.Center.Y));

        var dot = new Rect(row.X + S(6), row.Center.Y - S(4), S(8), S(8));
        ui.FillRounded(dot, (float)S(4), state.Phase == RecordingPhase.Recording ? theme.Danger : theme.TextTertiary);
        var label = saving ? "Saving…" : Clock(state.Clock.Elapsed) + (limit is { } max ? " / " + Clock(max) : "");
        ui.Text(label, new Rect(dot.Right + S(8), row.Y, right - S(14) - dot.Right - S(8), row.Height),
            paused || saving ? theme.TextSecondary : theme.TextPrimary, S(Metrics.FontSm), Weight.Semibold, face: Face.Mono);

        ui.EndFrame();
        ui.Inert = false;
        if (ui.Animating || ui.ClickedThisFrame) Invalidate();
    }

    private static string Clock(TimeSpan time) => $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";

    protected override LRESULT? WindowProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case WmTimer:
                Invalidate();
                return new LRESULT();
            case WmMouseMove:
                _pointer = ClientPoint(lParam);
                Invalidate();
                return new LRESULT();
            case WmLButtonDown:
                _ui?.NotePress(ClientPoint(lParam));
                Invalidate();
                return new LRESULT();
            case WmLButtonUp:
                _ui?.NoteRelease(ClientPoint(lParam));
                Invalidate();
                return new LRESULT();
            case WmSetCursor:
                Functions.SetCursor(new HCURSOR
                {
                    Value = _ui?.CursorAt(_pointer) == PointerCursor.Hand ? ToolCursors.Hand : ToolCursors.Arrow,
                });
                return new LRESULT { Value = 1 };
            case WmClose:
                StopRequested?.Invoke(false);
                return new LRESULT();
        }
        return base.WindowProc(hwnd, msg, wParam, lParam);
    }

    protected override void OnDestroyed(object? sender, EventArgs e)
    {
        WindowInterop.KillTimer(Handle, TimerId);
        base.OnDestroyed(sender, e);
    }
}

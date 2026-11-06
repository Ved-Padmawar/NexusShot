using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;
using static NexusShot.Views.Win32Window;

namespace NexusShot.Views;

/// <summary>
/// The outline of the area being recorded: four strips along its inside edge. Strips rather than one
/// window over the area, whose render target would be as large as the recording. Inside, not around,
/// because a full-screen area has no outside; capture exclusion keeps them out of the file.
/// </summary>
public sealed class RecordingFrame : IDisposable
{
    private const double Thickness = 2;

    private readonly Strip[] _strips;

    public RecordingFrame(RectInt area, Theme theme)
    {
        var edge = Math.Max(1, (int)Math.Round(Thickness * Monitors.DpiScaleAt(area.X + area.Width / 2, area.Y + area.Height / 2)));
        RectInt[] bounds =
        [
            new(area.X, area.Y, area.Width, edge),
            new(area.X, area.Bottom - edge, area.Width, edge),
            new(area.X, area.Y + edge, edge, area.Height - 2 * edge),
            new(area.Right - edge, area.Y + edge, edge, area.Height - 2 * edge),
        ];
        _strips = [.. bounds.Where(strip => strip.Width > 0 && strip.Height > 0).Select(strip => new Strip(strip, theme.Danger))];
    }

    public void Dispose()
    {
        foreach (var strip in _strips) strip.Dispose();
    }

    /// <summary>Never activates and lets clicks through to what is being recorded.</summary>
    private sealed class Strip : PopupWindow
    {
        private readonly Rgba _color;
        private Ui? _ui;

        public Strip(RectInt bounds, Rgba color)
            : base("NexusShot recording outline", WS_EX_TRANSPARENT | WS_EX_NOACTIVATE)
        {
            _color = color;
            if (!WindowInterop.ExcludeFromCapture(Handle)) return;
            WindowInterop.SetWindowPos(Handle, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        protected override void Render(IComObject<ID2D1HwndRenderTarget> renderTarget)
        {
            using var target = renderTarget.AsRenderTarget();
            target.Object.SetDpi(96, 96);
            _ui ??= new Ui(Resources(target));
            _ui.BeginFrame(target, new Point(-1, -1));
            _ui.FillRect(new Rect(0, 0, ClientRect.Width, ClientRect.Height), _color);
            _ui.EndFrame();
        }
    }
}

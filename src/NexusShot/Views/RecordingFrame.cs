using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

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
    private sealed class Strip : D2DRenderWindow
    {
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_EX_TOPMOST = 0x00000008;
        private const uint WS_EX_TRANSPARENT = 0x00000020;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private static readonly IntPtr HWND_TOPMOST = new(-1);

        private readonly Rgba _color;
        private D2DResources? _resources;
        private Ui? _ui;

        public Strip(RectInt bounds, Rgba color)
            : base("NexusShot recording outline", (WINDOW_STYLE)WS_POPUP,
                (WINDOW_EX_STYLE)(WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE))
        {
            _color = color;
            if (!WindowInterop.ExcludeFromCapture(Handle)) return;
            WindowInterop.SetWindowPos(Handle, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        protected override DirectN.Extensions.Utilities.Icon? LoadCreationIcon() => null;

        protected override void CreateRenderTarget()
        {
            RenderTarget?.Dispose();
            RenderTarget = GraphicsBackend.CreateWindowTarget(Handle, ClientRect.Size.ToD2D_SIZE_U(), FactoryType, FactoryOptions, software: true);
        }

        protected override void Render(IComObject<ID2D1HwndRenderTarget> renderTarget)
        {
            using var target = renderTarget.AsRenderTarget();
            target.Object.SetDpi(96, 96);
            _resources ??= new D2DResources(target);
            _ui ??= new Ui(_resources);
            _ui.BeginFrame(target, new Point(-1, -1));
            _ui.FillRect(new Rect(0, 0, ClientRect.Width, ClientRect.Height), _color);
            _ui.EndFrame();
        }

        protected override void OnDestroyed(object? sender, EventArgs e)
        {
            _resources?.Dispose();
            _resources = null;
            RenderTarget?.Dispose();
            RenderTarget = null;
            base.OnDestroyed(sender, e);
        }
    }
}

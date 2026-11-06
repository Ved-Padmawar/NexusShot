using NexusShot.Render;
using static NexusShot.Views.Win32Window;

namespace NexusShot.Views;

/// <summary>
/// A borderless top-most tool window drawn with Direct2D: no icon, no taskbar button, no Alt+Tab entry.
/// Its Direct2D resources and render target go when the window is destroyed rather than at Dispose,
/// so a closed popup holds no graphics memory. <paramref name="software"/> draws on the CPU, which a
/// small window redrawing on a timer prefers to waking the GPU.
/// </summary>
public abstract class PopupWindow(string title, uint extendedStyle = 0, bool software = true, bool translucent = false)
    : D2DRenderWindow(title, (WINDOW_STYLE)WS_POPUP, (WINDOW_EX_STYLE)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | extendedStyle))
{
    private D2DResources? _resources;

    /// <summary>Made against the first target drawn to, then kept until the window goes.</summary>
    protected D2DResources Resources(IComObject<ID2D1RenderTarget> target) => _resources ??= new D2DResources(target);

    protected override DirectN.Extensions.Utilities.Icon? LoadCreationIcon() => null;

    protected override void CreateRenderTarget()
    {
        RenderTarget?.Dispose();
        RenderTarget = GraphicsBackend.CreateWindowTarget(Handle, ClientRect.Size.ToD2D_SIZE_U(), FactoryType, FactoryOptions,
            software, translucent);
    }

    protected override void OnDestroyed(object? sender, EventArgs e)
    {
        ReleaseGraphics();
        base.OnDestroyed(sender, e);
    }

    protected override void Dispose(bool disposing)
    {
        ReleaseGraphics();
        base.Dispose(disposing);
    }

    private void ReleaseGraphics()
    {
        _resources?.Dispose();
        _resources = null;
        RenderTarget?.Dispose();
        RenderTarget = null;
    }
}

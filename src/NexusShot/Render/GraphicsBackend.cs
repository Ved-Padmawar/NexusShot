namespace NexusShot.Render;

/// <summary>Creates the windows' render targets, so every window agrees on how one is made.</summary>
internal static class GraphicsBackend
{
    public static IComObject<ID2D1HwndRenderTarget> CreateWindowTarget(
        HWND window, D2D_SIZE_U size, D2D1_FACTORY_TYPE factoryType, D2D1_FACTORY_OPTIONS? options)
    {
        using var factory = D2D1Functions.D2D1CreateFactory(factoryType, options);
        return factory.CreateHwndRenderTarget(
            new D2D1_HWND_RENDER_TARGET_PROPERTIES { hwnd = window, pixelSize = size },
            new D2D1_RENDER_TARGET_PROPERTIES());
    }
}

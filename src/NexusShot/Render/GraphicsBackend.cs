namespace NexusShot.Render;

/// <summary>Creates the windows' render targets, so every window agrees on how one is made.</summary>
internal static class GraphicsBackend
{
    /// <summary>
    /// <paramref name="software"/> keeps small, rarely redrawn windows off the GPU: the first hardware
    /// target loads the graphics driver, about 100 MB never released. <paramref name="translucent"/>
    /// keeps alpha for a system backdrop.
    /// </summary>
    public static IComObject<ID2D1HwndRenderTarget> CreateWindowTarget(
        HWND window, D2D_SIZE_U size, D2D1_FACTORY_TYPE factoryType, D2D1_FACTORY_OPTIONS? options,
        bool software = false, bool translucent = false)
    {
        using var factory = D2D1Functions.D2D1CreateFactory(factoryType, options);
        return factory.CreateHwndRenderTarget(
            new D2D1_HWND_RENDER_TARGET_PROPERTIES { hwnd = window, pixelSize = size },
            new D2D1_RENDER_TARGET_PROPERTIES
            {
                type = software
                    ? D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_SOFTWARE
                    : D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_DEFAULT,
                pixelFormat = translucent
                    ? new D2D1_PIXEL_FORMAT
                    {
                        format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                        alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
                    }
                    : default,
            });
    }
}

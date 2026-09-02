namespace NexusShot.Render;

/// <summary>Compatibility policy for graphics configurations with reproduced resource leaks.</summary>
internal static class GraphicsBackend
{
    private static readonly Lazy<bool> Software = new(DetectSoftwareRequirement);
    public static bool UseSoftwareRendering => Software.Value;

    // Intel 8086:7D51, UMD 32.0.101.6104: repeated bitmap/effect creation retained
    // event handles after disposal. Software D2D/WARP stayed flat in the same workload.
    // Match only the tested combination; a driver update is evaluated as hardware again.
    internal static bool RequiresSoftwareRendering(uint vendor, uint device, long driver) =>
        vendor == 0x8086 && device == 0x7D51 && driver == 0x00200000006517D8;

    private static bool DetectSoftwareRequirement()
    {
        try
        {
            using var factory = DXGIFunctions.CreateDXGIFactory1();
            if (factory.Object.EnumAdapters(0, out var rawAdapter).IsError) return false;
            using var adapter = new ComObject<IDXGIAdapter>(rawAdapter);
            var description = adapter.GetDesc();
            if (adapter.Object.CheckInterfaceSupport(typeof(IDXGIDevice).GUID, out var version).IsError)
                return false;
            return RequiresSoftwareRendering(description.VendorId, description.DeviceId, version);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException
            or InvalidOperationException or DllNotFoundException)
        {
            Core.Log.Error("graphics.adapter_detection", exception);
            return false;
        }
    }

    public static IComObject<ID2D1HwndRenderTarget> CreateWindowTarget(
        HWND window, D2D_SIZE_U size, D2D1_FACTORY_TYPE factoryType, D2D1_FACTORY_OPTIONS? options)
    {
        using var factory = D2D1Functions.D2D1CreateFactory(factoryType, options);
        return factory.CreateHwndRenderTarget(
            new D2D1_HWND_RENDER_TARGET_PROPERTIES { hwnd = window, pixelSize = size },
            new D2D1_RENDER_TARGET_PROPERTIES
            {
                type = UseSoftwareRendering ? D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_SOFTWARE
                    : D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_DEFAULT,
            });
    }
}

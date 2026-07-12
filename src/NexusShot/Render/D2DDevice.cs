namespace NexusShot.Render;

/// <summary>
/// Creates a Direct2D device on top of a hardware D3D11 device.
///
/// A device context (rather than a plain render target) is what unlocks ID2D1Effect, which is how
/// blur and pixelate run on the GPU. The exporter needs one for exactly that reason: a WIC render
/// target cannot host effects, and exporting a blur through a software fallback would silently
/// differ from the blur the user approved on screen.
/// </summary>
public static class D2DDevice
{
    public static IComObject<ID2D1Device> Create()
    {
        using var d3d = D3D11Functions.D3D11CreateDevice(
            null,
            D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            // BGRA support is required for D2D interop.
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT);

        using var dxgi = d3d.As<IDXGIDevice>()!;
        return D2DResources.D2DFactory.CreateDevice(dxgi);
    }
}

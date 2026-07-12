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
    /// <summary>
    /// A device, and the factory it belongs to.
    ///
    /// These are returned together deliberately. D2D refuses to use resources created by different
    /// factories, so the caller must build its geometry from *this* factory - handing back a bare
    /// device would leave the pairing implicit and easy to get wrong.
    /// </summary>
    public static (IComObject<ID2D1Device> Device, IComObject<ID2D1Factory1> Factory) Create()
    {
        var factory = D2D1Functions.D2D1CreateFactory<ID2D1Factory1>(
            D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED);

        using var d3d = D3D11Functions.D3D11CreateDevice(
            null,
            D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            // BGRA support is required for D2D interop.
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT);

        using var dxgi = d3d.As<IDXGIDevice>()!;
        return (factory.CreateDevice(dxgi), factory);
    }
}

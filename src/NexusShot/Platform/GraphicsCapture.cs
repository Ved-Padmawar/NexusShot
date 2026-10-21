using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusShot.Core;
using NexusShot.Render;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace NexusShot.Platform;

/// <summary>
/// A window or a monitor through Windows.Graphics.Capture. A window comes back as its own pixels, not
/// what is on screen where it sits - so windows overlapping it, and parts of it off the edge of the
/// desktop, neither intrude nor go missing, and hardware-overlay video is captured rather than black.
/// Win11's rounded corners come back transparent.
///
/// On an HDR display the frame is taken as half-float scRGB and tone-mapped by <see cref="HdrToneMap"/>;
/// asked for in 8 bits, Windows clips every highlight to white before the app sees it.
/// </summary>
[SupportedOSPlatform("windows10.0.18362")]
internal static unsafe partial class GraphicsCapture
{
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    /// <summary>Slots in IGraphicsCaptureItemInterop, the first methods after IUnknown's three.</summary>
    private const int CreateForWindowSlot = 3;
    private const int CreateForMonitorSlot = 4;

    private const int BytesPerHalfPixel = 8;

    /// <summary>A capture item from a window handle arrived in 1903; <c>IsSupported</c> is false where
    /// capture is disabled by policy.</summary>
    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    /// <summary>Waits for the one frame taken; a target that never presents one is refused.</summary>
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(2);

    /// <summary>A window's pixels; <paramref name="hdrWhiteNits"/> is the SDR white level of the HDR display
    /// it is on, or null for an SDR one.</summary>
    public static DecodedImage CaptureWindow(IntPtr window, bool includeCursor, float? hdrWhiteNits) =>
        Capture(ItemFor(CreateForWindowSlot, window), includeCursor, hdrWhiteNits);

    /// <summary>An HDR monitor, tone-mapped for <paramref name="sdrWhiteNits"/>.</summary>
    public static DecodedImage CaptureMonitor(IntPtr monitor, bool includeCursor, float sdrWhiteNits) =>
        Capture(ItemFor(CreateForMonitorSlot, monitor), includeCursor, sdrWhiteNits);

    private static DecodedImage Capture(GraphicsCaptureItem item, bool includeCursor, float? hdrWhiteNits)
    {
        using var d3d = D3D11Functions.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT);
        using var device = WinRTDevice(d3d);

        var format = hdrWhiteNits is null ? DirectXPixelFormat.B8G8R8A8UIntNormalized : DirectXPixelFormat.R16G16B16A16Float;
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, format, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) session.IsCursorCaptureEnabled = includeCursor;

        // Some builds refuse borderless; the frame only flashes for one frame, so that is no failure.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            try { session.IsBorderRequired = false; }
            catch (Exception exception) when (exception is UnauthorizedAccessException or COMException) { }
        }

        using var arrived = new ManualResetEventSlim();
        pool.FrameArrived += (_, _) => arrived.Set();
        session.StartCapture();
        if (!arrived.Wait(FrameTimeout)) throw new TimeoutException("The capture target did not present a frame.");

        using var frame = pool.TryGetNextFrame()
            ?? throw new InvalidOperationException("The capture produced no frame.");
        return hdrWhiteNits is { } white
            ? Read(d3d, frame, BytesPerHalfPixel, (source, row) => HdrToneMap.ConvertRow(MemoryMarshal.Cast<byte, Half>(source), row, white))
            : Read(d3d, frame, 4, (source, row) => source.CopyTo(row));
    }

    private static IDirect3DDevice WinRTDevice(IComObject<ID3D11Device> d3d)
    {
        using var dxgi = d3d.As<IDXGIDevice>()!;
        var pointer = ComObject.GetOrCreateComInstance<IDXGIDevice>(dxgi.Object);
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(pointer, out var inspectable));
            try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
            finally { Marshal.Release(inspectable); }
        }
        finally { Marshal.Release(pointer); }
    }

    /// <summary>A capture item from a window or monitor handle, through the interop method in
    /// <paramref name="slot"/>.</summary>
    private static GraphicsCaptureItem ItemFor(int slot, IntPtr handle)
    {
        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in IID_IGraphicsCaptureItemInterop, out var interop));
        try
        {
            var createFor = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(void***)interop)[slot];
            var iid = IID_IGraphicsCaptureItem;
            IntPtr item;
            Marshal.ThrowExceptionForHR(createFor(interop, handle, &iid, &item));
            try { return GraphicsCaptureItem.FromAbi(item); }
            finally { Marshal.Release(item); }
        }
        finally { Marshal.Release(interop); }
    }

    /// <summary>Turns one row of frame pixels, as the surface holds them, into one row of BGRA bytes.</summary>
    private delegate void RowConverter(ReadOnlySpan<byte> source, Span<byte> destination);

    /// <summary>Copies the frame to a CPU-readable texture and out row by row, cropped to the content:
    /// the pool's surface can be larger than the target when it was sized before a resize.</summary>
    private static DecodedImage Read(IComObject<ID3D11Device> d3d, Direct3D11CaptureFrame frame,
        int bytesPerPixel, RowConverter convert)
    {
        using var surface = frame.Surface;
        using var texture = Texture(surface);
        var desc = texture.GetDesc();
        var width = Math.Min((int)desc.Width, frame.ContentSize.Width);
        var height = Math.Min((int)desc.Height, frame.ContentSize.Height);

        using var staging = d3d.CreateTexture2D(new D3D11_TEXTURE2D_DESC
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
        });
        using var context = d3d.GetImmediateContext()!;
        using var source = new ComObject<ID3D11Resource>(texture.Object, releaseOnDispose: false);
        using var destination = new ComObject<ID3D11Resource>(staging.Object, releaseOnDispose: false);
        context.Object.CopyResource(destination.Object, source.Object);

        var mapped = context.Map(destination, 0, D3D11_MAP.D3D11_MAP_READ);
        try
        {
            var image = DecodedImage.Allocate(width, height);
            var pixels = image.Span;
            for (var row = 0; row < height; row++)
                convert(new ReadOnlySpan<byte>((byte*)mapped.pData + row * mapped.RowPitch, width * bytesPerPixel),
                    pixels.Slice(row * image.Stride, width * 4));
            return image;
        }
        finally { context.Unmap(destination, 0); }
    }

    private static IComObject<ID3D11Texture2D> Texture(IDirect3DSurface surface)
    {
        var native = ((IWinRTObject)surface).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, in IID_IDirect3DDxgiInterfaceAccess, out var access));
        try
        {
            // IDirect3DDxgiInterfaceAccess::GetInterface, the first method after IUnknown's three.
            var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(void***)access)[3];
            var iid = IID_ID3D11Texture2D;
            IntPtr texture;
            Marshal.ThrowExceptionForHR(getInterface(access, &iid, &texture));
            return ComObject.FromPointer<ID3D11Texture2D>(texture)
                ?? throw new InvalidOperationException("The frame is not a Direct3D 11 texture.");
        }
        finally { Marshal.Release(access); }
    }

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}

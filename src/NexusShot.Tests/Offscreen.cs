using DirectN;
using DirectN.Extensions;
using DirectN.Extensions.Com;
using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot.Tests;

/// <summary>
/// A Direct2D device context drawing into a bitmap that is read back after every frame, so the
/// renderer and the immediate-mode widgets can be tested by their pixels and their clicks without a
/// window. Uses the same device the app and the exporter create.
/// </summary>
internal sealed class Offscreen : IDisposable
{
    private readonly IComObject<ID2D1Device> _device;
    private readonly IComObject<ID2D1Factory1> _factory;
    private readonly IComObject<ID2D1DeviceContext> _context;
    private readonly IComObject<ID2D1Bitmap1> _surface;
    private readonly IComObject<ID2D1Bitmap1> _staging;
    private byte[] _pixels = [];

    /// <summary>A readback stalls on the GPU, so it happens only when a test asks for pixels.</summary>
    private bool _stale;

    public int Width { get; }
    public int Height { get; }
    public IComObject<ID2D1RenderTarget> Target { get; }
    public D2DResources Resources { get; }

    public Offscreen(int width, int height)
    {
        (Width, Height) = (width, height);
        (_device, _factory) = D2DDevice.Create();
        _context = _device.CreateDeviceContext();
        _surface = Bitmap(D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET);
        _staging = Bitmap(D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW);
        _context.Object.SetTarget(_surface.Object);
        Target = _context.AsRenderTarget2();
        Resources = new D2DResources(Target);
    }

    /// <summary>Draws one frame over <paramref name="background"/>.</summary>
    public void Draw(Rgba background, Action<IComObject<ID2D1RenderTarget>> draw)
    {
        _context.BeginDraw();
        _context.Clear(new D3DCOLORVALUE
        {
            r = background.R / 255f, g = background.G / 255f, b = background.B / 255f, a = background.A / 255f,
        });
        draw(Target);
        _context.EndDraw();
        _stale = true;
    }

    /// <summary>One immediate-mode frame with the pointer at <paramref name="pointer"/>.</summary>
    public void Frame(Ui ui, Point pointer, bool down, Action body) => Draw(ui.Theme.SurfaceWindow, target =>
    {
        ui.BeginFrame(target, pointer, down);
        body();
        ui.EndFrame();
    });

    /// <summary>A press and release at one point, the two frames a click takes; the result is the
    /// release frame's.</summary>
    public T Click<T>(Ui ui, Point at, Func<T> body)
    {
        Frame(ui, at, down: false, () => body());
        Frame(ui, at, down: true, () => body());
        var result = default(T)!;
        Frame(ui, at, down: false, () => result = body());
        return result;
    }

    /// <summary>A pixel of the last frame, as straight colour (the frames drawn here are opaque).</summary>
    public Rgba Pixel(int x, int y)
    {
        if (_stale) ReadBack();
        var at = (y * Width + x) * 4;
        return new Rgba(_pixels[at + 2], _pixels[at + 1], _pixels[at], _pixels[at + 3]);
    }

    /// <summary>How many pixels of the last frame inside <paramref name="area"/> differ from
    /// <paramref name="color"/>.</summary>
    public int CountOther(Rect area, Rgba color)
    {
        var count = 0;
        for (var y = (int)area.Top; y < (int)area.Bottom; y++)
            for (var x = (int)area.Left; x < (int)area.Right; x++)
                if (Pixel(x, y) != color) count++;
        return count;
    }

    private unsafe void ReadBack()
    {
        _staging.Object.CopyFromBitmap(0, _surface.Object, 0).ThrowOnError();
        _staging.Object.Map(D2D1_MAP_OPTIONS.D2D1_MAP_OPTIONS_READ, out var mapped).ThrowOnError();
        try
        {
            _stale = false;
            _pixels = new byte[Width * Height * 4];
            for (var row = 0; row < Height; row++)
                new ReadOnlySpan<byte>((byte*)mapped.bits + row * mapped.pitch, Width * 4)
                    .CopyTo(_pixels.AsSpan(row * Width * 4));
        }
        finally
        {
            _staging.Object.Unmap().ThrowOnError();
        }
    }

    private IComObject<ID2D1Bitmap1> Bitmap(D2D1_BITMAP_OPTIONS options) => _context.CreateBitmap<ID2D1Bitmap1>(
        new D2D_SIZE_U { width = (uint)Width, height = (uint)Height },
        new D2D1_BITMAP_PROPERTIES1
        {
            pixelFormat = new D2D1_PIXEL_FORMAT
            {
                format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
            },
            dpiX = 96,
            dpiY = 96,
            bitmapOptions = options,
        });

    public void Dispose()
    {
        Resources.Dispose();
        _context.Object.SetTarget(null);
        _staging.Dispose();
        _surface.Dispose();
        _context.Dispose();
        _device.Dispose();
        _factory.Dispose();
    }
}

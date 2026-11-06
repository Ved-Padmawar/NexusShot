using System.Runtime.InteropServices;
using NexusShot.Core;
using NexusShot.Render;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace NexusShot.Platform;

internal static unsafe partial class GraphicsCapture
{
    /// <summary>
    /// A desktop area as a live stream, for recording: one session per monitor the area touches, each
    /// composited into a persistent canvas. The compositor only delivers a frame when a monitor changes,
    /// so an unchanged monitor costs nothing and keeps its last pixels. Desktop outside every monitor
    /// stays black.
    /// </summary>
    public sealed class AreaStream : IDisposable
    {
        private readonly RectInt _area;
        private readonly IComObject<ID3D11Device> _d3d;
        private readonly IComObject<ID3D11DeviceContext> _context;
        private readonly IDirect3DDevice _device;
        private readonly List<MonitorFeed> _feeds = [];
        private readonly DecodedImage _canvas;

        public AreaStream(RectInt area, bool includeCursor)
        {
            _area = area;
            try
            {
                _d3d = D3D11Functions.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                    D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT);
                _context = _d3d.GetImmediateContext()!;
                _device = WinRTDevice(_d3d);
                var hdr = DisplayColor.HdrDisplays();
                foreach (var monitor in Monitors.All())
                {
                    if (area.Intersect(monitor) is not { } crop) continue;
                    float? white = hdr.FirstOrDefault(display => display.Bounds == monitor) is { SdrWhiteNits: > 0 } display
                        ? display.SdrWhiteNits : null;
                    _feeds.Add(new MonitorFeed(_d3d, _device, monitor, crop, includeCursor, white));
                }
                if (_feeds.Count == 0)
                    throw new ArgumentOutOfRangeException(nameof(area), "The recording area is outside every monitor.");

                _canvas = DecodedImage.Allocate(area.Width, area.Height);
                MemoryMarshal.Cast<byte, uint>(_canvas.Span).Fill(0xFF000000);
                foreach (var feed in _feeds) feed.AwaitFirstFrame();
            }
            catch { Dispose(); throw; }
        }

        /// <summary>The area as it is now, as a frame the caller owns.</summary>
        public DecodedImage Next()
        {
            foreach (var feed in _feeds) feed.DrawInto(_context, _canvas, _area);
            return DecodedImage.CopyFrom(_canvas.Span, _canvas.Width, _canvas.Height);
        }

        public void Dispose()
        {
            foreach (var feed in _feeds) feed.Dispose();
            _canvas?.Dispose();
            (_device as IDisposable)?.Dispose();
            _context?.Dispose();
            _d3d?.Dispose();
        }
    }

    /// <summary>One monitor's session. Only the newest frame is held: the handler swaps it in on the
    /// pool's thread and the recording thread swaps it out, so neither waits on the other.</summary>
    private sealed class MonitorFeed : IDisposable
    {
        private readonly RectInt _monitor;
        private readonly RectInt _crop;
        private readonly float? _hdrWhiteNits;
        private readonly Direct3D11CaptureFramePool _pool;
        private readonly GraphicsCaptureSession _session;
        private readonly IComObject<ID3D11Texture2D> _staging;
        private readonly ManualResetEventSlim _firstFrame = new();
        private Direct3D11CaptureFrame? _latest;

        public MonitorFeed(IComObject<ID3D11Device> d3d, IDirect3DDevice device, RectInt monitor, RectInt crop,
            bool includeCursor, float? hdrWhiteNits)
        {
            _monitor = monitor;
            _crop = crop;
            _hdrWhiteNits = hdrWhiteNits;
            try
            {
                var item = ItemFor(CreateForMonitorSlot, Monitors.At(monitor.X, monitor.Y));
                _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, FormatFor(hdrWhiteNits), 2, item.Size);
                _session = Session(_pool, item, includeCursor);
                _staging = d3d.CreateTexture2D(new D3D11_TEXTURE2D_DESC
                {
                    Width = (uint)crop.Width,
                    Height = (uint)crop.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = hdrWhiteNits is null ? DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM : DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                    Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                    CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                });
                _pool.FrameArrived += OnFrameArrived;
                _session.StartCapture();
            }
            catch { Dispose(); throw; }
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
        {
            Interlocked.Exchange(ref _latest, pool.TryGetNextFrame())?.Dispose();
            _firstFrame.Set();
        }

        /// <summary>Every session presents a first frame on start, so one that does not is broken.</summary>
        public void AwaitFirstFrame()
        {
            if (!_firstFrame.Wait(FrameTimeout)) throw new TimeoutException("A monitor did not present a frame.");
        }

        /// <summary>Copies this monitor's part of the newest frame into the canvas; with no new frame the
        /// canvas already holds it. A resolution change mid-recording copies only what the frame still has.</summary>
        public void DrawInto(IComObject<ID3D11DeviceContext> context, DecodedImage canvas, RectInt area)
        {
            using var frame = Interlocked.Exchange(ref _latest, null);
            if (frame is null) return;
            using var surface = frame.Surface;
            using var texture = Texture(surface);
            var desc = texture.GetDesc();
            var box = new D3D11_BOX
            {
                left = (uint)(_crop.X - _monitor.X),
                top = (uint)(_crop.Y - _monitor.Y),
                back = 1,
            };
            box.right = Math.Min(box.left + (uint)_crop.Width, desc.Width);
            box.bottom = Math.Min(box.top + (uint)_crop.Height, desc.Height);
            if (box.right <= box.left || box.bottom <= box.top) return;

            using (var source = new ComObject<ID3D11Resource>(texture.Object, releaseOnDispose: false))
            using (var destination = new ComObject<ID3D11Resource>(_staging.Object, releaseOnDispose: false))
                context.Object.CopySubresourceRegion(destination.Object, 0, 0, 0, 0, source.Object, 0, (nint)(&box));

            var width = (int)(box.right - box.left);
            var height = (int)(box.bottom - box.top);
            var bytesPerPixel = BytesPerPixel(_hdrWhiteNits);
            var pixels = canvas.Span;
            var mapped = context.Map(_staging, 0, D3D11_MAP.D3D11_MAP_READ);
            try
            {
                for (var row = 0; row < height; row++)
                    ConvertRow(new ReadOnlySpan<byte>((byte*)mapped.pData + row * mapped.RowPitch, width * bytesPerPixel),
                        pixels.Slice((_crop.Y - area.Y + row) * canvas.Stride + (_crop.X - area.X) * 4, width * 4),
                        _hdrWhiteNits);
            }
            finally { context.Unmap(_staging, 0); }
        }

        /// <summary>The handler is detached before the pool closes, so no frame arrives after the last
        /// one is released.</summary>
        public void Dispose()
        {
            if (_pool is not null) _pool.FrameArrived -= OnFrameArrived;
            _session?.Dispose();
            _pool?.Dispose();
            Interlocked.Exchange(ref _latest, null)?.Dispose();
            _staging?.Dispose();
            _firstFrame.Dispose();
        }
    }
}

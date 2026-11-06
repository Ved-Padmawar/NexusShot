using System.Runtime.InteropServices;
using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot.Platform;

internal interface IRecordingEncoder : IDisposable
{
    void Write(DecodedImage pixels, long timestamp, long duration);
    void Complete();
}

/// <summary>Each frame after the first stores only the rectangle that changed, over the last frame left
/// in place, with a palette fitted to that rectangle alone - so a still screen costs a few bytes a frame.</summary>
internal sealed class GifEncoder : IRecordingEncoder
{
    private readonly FileStream _file;
    private readonly ManagedIStream _stream;
    private readonly IComObject<IWICBitmapEncoder> _encoder;
    private DecodedImage? _previous;

    public GifEncoder(string path)
    {
        try
        {
            _file = File.Create(path);
            _stream = new ManagedIStream(_file);
            _encoder = WicImagingFactory.CreateEncoder(Constants.GUID_ContainerFormatGif);
            _encoder.Initialize(_stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache);
            using var metadata = _encoder.GetMetadataQueryWriter();
            metadata.SetMetadataByName("/appext/Application", "NETSCAPE2.0"u8.ToArray());
            metadata.SetMetadataByName("/appext/Data", new byte[] { 3, 1, 0, 0, 0 });
        }
        catch { Dispose(); throw; }
    }

    /// <summary>A frame matching the last still needs its delay, so it becomes one unchanged pixel.</summary>
    public void Write(DecodedImage pixels, long timestamp, long duration)
    {
        var (x, y, width, height) = _previous is null ? (0, 0, pixels.Width, pixels.Height)
            : FrameDiff.ChangedBounds(Words(_previous), Words(pixels), pixels.Width) ?? (0, 0, 1, 1);
        WriteFrame(pixels, x, y, width, height, timestamp, duration);
        _previous ??= DecodedImage.Allocate(pixels.Width, pixels.Height);
        pixels.Span.CopyTo(_previous.Span);
    }

    private static ReadOnlySpan<uint> Words(DecodedImage image) => MemoryMarshal.Cast<byte, uint>(image.Span);

    private void WriteFrame(DecodedImage pixels, int x, int y, int width, int height, long timestamp, long duration)
    {
        using var bitmap = WicImagingFactory.CreateBitmapFromMemory((uint)width, (uint)height,
            Constants.GUID_WICPixelFormat32bppPBGRA, (uint)pixels.Stride, (uint)(pixels.Stride * (height - 1) + width * 4),
            pixels.Pointer + y * pixels.Stride + x * 4);
        using var palette = WicImagingFactory.CreatePalette();
        palette.Object.InitializeFromBitmap(bitmap.Object, 256, false).ThrowOnError();
        using var converter = WicImagingFactory.CreateFormatConverter();
        converter.Object.Initialize(bitmap.Object, Constants.GUID_WICPixelFormat8bppIndexed,
            WICBitmapDitherType.WICBitmapDitherTypeErrorDiffusion, palette.Object, 0,
            WICBitmapPaletteType.WICBitmapPaletteTypeCustom).ThrowOnError();

        using var frame = _encoder.CreateNewFrame();
        frame.Initialize();
        frame.SetSize((uint)width, (uint)height);
        frame.SetPixelFormat(Constants.GUID_WICPixelFormat8bppIndexed);
        frame.Encode.Object.SetPalette(palette.Object).ThrowOnError();
        using var metadata = frame.GetMetadataQueryWriter();
        // Quantise endpoints, rather than every duration, to avoid cumulative rounding drift.
        var delay = (long)Math.Round((timestamp + duration) / (double)TimeSpan.TicksPerMillisecond / 10)
            - (long)Math.Round(timestamp / (double)TimeSpan.TicksPerMillisecond / 10);
        metadata.SetMetadataByName("/grctlext/Delay", (ushort)Math.Clamp(delay, 2, ushort.MaxValue));
        metadata.SetMetadataByName("/grctlext/Disposal", (byte)1);
        metadata.SetMetadataByName("/imgdesc/Left", (ushort)x);
        metadata.SetMetadataByName("/imgdesc/Top", (ushort)y);
        frame.WriteSource(converter);
        frame.Commit();
    }

    public void Complete() => _encoder.Commit();

    public void Dispose()
    {
        _previous?.Dispose();
        _encoder?.Dispose();
        _stream?.Dispose();
        _file?.Dispose();
    }
}

internal sealed class Mp4Encoder : IRecordingEncoder
{
    private const uint MF_VERSION = 0x00020070;
    private IComObject<IMFSinkWriter>? _writer;
    private readonly uint _stream;
    private bool _started;

    public Mp4Encoder(string path, RecordingPlan plan)
    {
        Functions.MFStartup(MF_VERSION, 0).ThrowOnError();
        _started = true;
        try
        {
            Functions.MFCreateAttributes(out var attributes, 2).ThrowOnError();
            using var options = new ComObject<IMFAttributes>(attributes);
            options.Object.SetGUID(Constants.MF_TRANSCODE_CONTAINERTYPE, Constants.MFTranscodeContainerType_MPEG4).ThrowOnError();
            options.Object.SetUINT32(Constants.MF_SINK_WRITER_DISABLE_THROTTLING, 1).ThrowOnError();
            using Pwstr filename = path;
            Functions.MFCreateSinkWriterFromURL(filename, null!, options.Object, out var writer).ThrowOnError();
            _writer = new ComObject<IMFSinkWriter>(writer);
            using var output = MediaType(plan, Constants.MFVideoFormat_H264);
            output.Object.SetUINT32(Constants.MF_MT_AVG_BITRATE, (uint)plan.BitsPerSecond).ThrowOnError();
            _writer.Object.AddStream(output.Object, out _stream).ThrowOnError();
            using var input = MediaType(plan, Constants.MFVideoFormat_RGB32);
            // A positive stride explicitly describes our top-down DIB memory.
            input.Object.SetUINT32(Constants.MF_MT_DEFAULT_STRIDE, (uint)(plan.Width * 4)).ThrowOnError();
            _writer.Object.SetInputMediaType(_stream, input.Object, null!).ThrowOnError();
            _writer.Object.BeginWriting().ThrowOnError();
        }
        catch { Dispose(); throw; }
    }

    private static IComObject<IMFMediaType> MediaType(RecordingPlan plan, Guid subtype)
    {
        Functions.MFCreateMediaType(out var raw).ThrowOnError();
        var type = new ComObject<IMFMediaType>(raw);
        try
        {
            type.Object.SetGUID(Constants.MF_MT_MAJOR_TYPE, Constants.MFMediaType_Video).ThrowOnError();
            type.Object.SetGUID(Constants.MF_MT_SUBTYPE, subtype).ThrowOnError();
            type.Object.SetUINT32(Constants.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive).ThrowOnError();
            type.Object.SetSize(Constants.MF_MT_FRAME_SIZE, (uint)plan.Width, (uint)plan.Height);
            type.Object.SetRatio(Constants.MF_MT_FRAME_RATE, (uint)plan.FramesPerSecond, 1);
            type.Object.SetRatio(Constants.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            return type;
        }
        catch { type.Dispose(); throw; }
    }

    public unsafe void Write(DecodedImage pixels, long timestamp, long duration)
    {
        Functions.MFCreateMemoryBuffer((uint)pixels.ByteLength, out var rawBuffer).ThrowOnError();
        using var buffer = new ComObject<IMFMediaBuffer>(rawBuffer);
        buffer.Object.Lock(out var destination, IntPtr.Zero, IntPtr.Zero).ThrowOnError();
        try { pixels.Span.CopyTo(new Span<byte>((void*)destination, pixels.ByteLength)); }
        finally { buffer.Object.Unlock().ThrowOnError(); }
        buffer.Object.SetCurrentLength((uint)pixels.ByteLength).ThrowOnError();
        Functions.MFCreateSample(out var rawSample).ThrowOnError();
        using var sample = new ComObject<IMFSample>(rawSample);
        sample.Object.AddBuffer(buffer.Object).ThrowOnError();
        sample.Object.SetSampleTime(timestamp).ThrowOnError();
        sample.Object.SetSampleDuration(duration).ThrowOnError();
        _writer!.Object.WriteSample(_stream, sample.Object).ThrowOnError();
    }

    public void Complete() => _writer!.Object.Finalize().ThrowOnError();

    /// <summary>Never throws: it runs while unwinding a failed recording, and a failed MFShutdown only
    /// leaves Media Foundation's process reference count raised.</summary>
    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
        if (_started) _ = Functions.MFShutdown();
        _started = false;
    }
}

using System.Runtime.InteropServices;

namespace NexusShot.Render;

/// <summary>
/// Decoded pixels with no device attached: premultiplied BGRA, top-down.
///
/// The buffer is unmanaged and freed on <see cref="Dispose"/>. A full-screen capture is tens of
/// megabytes, which as a <c>byte[]</c> lands on the large object heap: the GC then holds that
/// address space long after the pixels are dead, so a tray app that has taken one screenshot never
/// gives the memory back. Freed native memory returns to the OS.
///
/// Owned by exactly one holder at a time; consumers read through <see cref="Span"/>.
/// </summary>
public sealed class DecodedImage : IDisposable
{
    private IntPtr _buffer;

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public int ByteLength => Stride * Height;

    private DecodedImage(IntPtr buffer, int width, int height)
    {
        _buffer = buffer;
        Width = width;
        Height = height;
    }

    /// <summary>Allocates an uninitialised buffer for a decoder to write into.</summary>
    public static DecodedImage Allocate(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var length = (long)width * height * 4;
        return new DecodedImage(Marshal.AllocHGlobal((nint)length), width, height);
    }

    /// <summary>Copies existing pixels in. For callers that already hold a buffer they do not own.</summary>
    public static DecodedImage CopyFrom(ReadOnlySpan<byte> pixels, int width, int height)
    {
        var image = Allocate(width, height);
        pixels[..image.ByteLength].CopyTo(image.Span);
        return image;
    }

    /// <summary>The pixels. Valid only while this instance is alive.</summary>
    public unsafe Span<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_buffer == IntPtr.Zero, this);
            return new Span<byte>((void*)_buffer, ByteLength);
        }
    }

    /// <summary>The raw pointer, for interop that writes or reads directly.</summary>
    public IntPtr Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_buffer == IntPtr.Zero, this);
            return _buffer;
        }
    }

    /// <summary>A crop, as a new independently owned image. The origin is pinned inside the source
    /// before the size is clamped, so a selection rounded a pixel past the edge narrows to the last
    /// row and column rather than inverting.</summary>
    public DecodedImage Crop(int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, Math.Max(0, Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, Height - 1));
        width = Math.Clamp(width, 1, Width - x);
        height = Math.Clamp(height, 1, Height - y);

        var crop = Allocate(width, height);
        var source = Span;
        var destination = crop.Span;
        for (var row = 0; row < height; row++)
        {
            source.Slice((y + row) * Stride + x * 4, width * 4)
                .CopyTo(destination.Slice(row * crop.Stride, width * 4));
        }
        return crop;
    }

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, IntPtr.Zero);
        if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        GC.SuppressFinalize(this);
    }

    /// <summary>A missed Dispose must not leak the process's largest allocation.</summary>
    ~DecodedImage() => Dispose();
}

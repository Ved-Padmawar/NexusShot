namespace NexusShot.Render;

/// <summary>
/// The decoded source image as a GPU bitmap.
///
/// Decoded via WIC and uploaded at full resolution; the GPU rescales it every frame, so the view
/// always samples the real image rather than a pre-scaled copy. The CPU-side pixels are released as
/// soon as the upload completes - nothing here reads them back, and holding them would double the
/// cost of every open image.
/// </summary>
public sealed class ImageSurface : IDisposable
{
    private const long MaximumPixels = 268_435_456; // 1 GiB at 32bpp

    public required IComObject<ID2D1Bitmap> Bitmap { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Decodes a file and uploads it, freeing the CPU copy before returning.</summary>
    public static ImageSurface Load(string path, IComObject<ID2D1DeviceContext> context)
    {
        using var pixels = Decode(path);
        return Upload(pixels, context);
    }

    /// <summary>
    /// Decodes an image down to fit a box, for thumbnails.
    ///
    /// WIC scales as part of the decode, so the full-resolution bitmap is never allocated - which is
    /// the whole point. A history of 4K captures each cached at full size is hundreds of megabytes
    /// for images that end up in a 52x34 chip.
    /// </summary>
    public static ImageSurface LoadScaled(
        string path, IComObject<ID2D1DeviceContext> context, int maxWidth, int maxHeight)
    {
        using var pixels = DecodeScaled(path, maxWidth, maxHeight);
        return Upload(pixels, context);
    }

    /// <summary>The CPU half of <see cref="LoadScaled"/>: decodes and scales, touching no device, so
    /// it is safe to call from any thread. The caller owns the result.</summary>
    public static DecodedImage DecodeScaled(string path, int maxWidth, int maxHeight)
    {
        using var decoder = WicImagingFactory.CreateDecoderFromFilename(path);
        using var frame = decoder.GetFrame(0);
        frame.Object.GetSize(out var sourceWidth, out var sourceHeight).ThrowOnError();

        var scale = Math.Min(1, Math.Min(
            maxWidth / (double)sourceWidth,
            maxHeight / (double)sourceHeight));

        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        if ((long)width * height > MaximumPixels)
            throw new InvalidOperationException("Image too large to decode.");

        using var scaler = WicImagingFactory.CreateBitmapScaler();
        scaler.Object.Initialize(
            frame.Object, (uint)width, (uint)height,
            WICBitmapInterpolationMode.WICBitmapInterpolationModeFant).ThrowOnError();

        using var converter = WicImagingFactory.CreateFormatConverter();
        converter.Object.Initialize(
            scaler.Object,
            Constants.GUID_WICPixelFormat32bppPBGRA,
            WICBitmapDitherType.WICBitmapDitherTypeNone,
            null!,
            0,
            WICBitmapPaletteType.WICBitmapPaletteTypeCustom).ThrowOnError();

        return CopyPixels(converter.Object, width, height);
    }

    /// <summary>The GPU half: uploads decoded pixels. Must run on the thread that owns the device.
    /// The image is only read, so the caller keeps ownership of it.</summary>
    public static ImageSurface Upload(DecodedImage image, IComObject<ID2D1DeviceContext> context)
    {
        var bitmap = context.CreateBitmap(
            new D2D_SIZE_U { width = (uint)image.Width, height = (uint)image.Height },
            image.Pointer,
            (uint)image.Stride,
            new D2D1_BITMAP_PROPERTIES1
            {
                pixelFormat = new D2D1_PIXEL_FORMAT
                {
                    format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                    alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
                },
                dpiX = 96,
                dpiY = 96,
            });

        return new ImageSurface { Bitmap = bitmap, Width = image.Width, Height = image.Height };
    }

    /// <summary>The image's dimensions, without decoding it. WIC reads the header only.</summary>
    public static (int Width, int Height) ReadSize(string path)
    {
        using var decoder = WicImagingFactory.CreateDecoderFromFilename(path);
        using var frame = decoder.GetFrame(0);
        frame.Object.GetSize(out var width, out var height).ThrowOnError();
        return ((int)width, (int)height);
    }

    /// <summary>
    /// Decodes to premultiplied BGRA - the format D2D composites in, so no conversion happens on
    /// the hot path. WIC does the premultiplication as part of the format conversion.
    /// </summary>
    public static DecodedImage Decode(string path)
    {
        using var decoder = WicImagingFactory.CreateDecoderFromFilename(path);
        using var frame = decoder.GetFrame(0);
        using var converter = WicImagingFactory.CreateFormatConverter();

        converter.Object.Initialize(
            frame.Object,
            Constants.GUID_WICPixelFormat32bppPBGRA,
            WICBitmapDitherType.WICBitmapDitherTypeNone,
            null!,
            0,
            WICBitmapPaletteType.WICBitmapPaletteTypeCustom).ThrowOnError();

        converter.Object.GetSize(out var width, out var height).ThrowOnError();
        if ((long)width * height > MaximumPixels)
            throw new InvalidOperationException("Image too large to decode.");

        return CopyPixels(converter.Object, (int)width, (int)height);
    }

    /// <summary>Drains a WIC source straight into unmanaged memory, so the pixels never reach the
    /// managed heap. The image is freed if the copy fails, rather than leaking on the throw.</summary>
    private static DecodedImage CopyPixels(IWICBitmapSource source, int width, int height)
    {
        var image = DecodedImage.Allocate(width, height);
        try
        {
            source.CopyPixels(0, (uint)image.Stride, (uint)image.ByteLength, image.Pointer)
                .ThrowOnError();
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    public void Dispose() => Bitmap.Dispose();
}

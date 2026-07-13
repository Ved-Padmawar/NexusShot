using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>Decoded pixels with no device attached: premultiplied BGRA, top-down.</summary>
public sealed record DecodedImage(byte[] Pixels, int Width, int Height);

/// <summary>
/// The decoded source image: a GPU bitmap for drawing, plus the CPU pixels for anything that needs
/// to read them back (export, colour picking).
///
/// Decoded via WIC and uploaded at full resolution; the GPU rescales it every frame, so the view
/// always samples the real image rather than a pre-scaled copy.
/// </summary>
public sealed class ImageSurface : IDisposable
{
    public required IComObject<ID2D1Bitmap> Bitmap { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>
    /// Premultiplied BGRA, top-down - or null when the caller did not ask for it.
    ///
    /// Holding the decoded pixels doubles the cost of an image: a 4K screenshot is ~33 MB on the CPU
    /// on top of the same again on the GPU. Only the editor reads them back (to sample the colour
    /// under a text box), so everything else - the history grid especially, which caches one surface
    /// per capture - loads without them.
    /// </summary>
    public byte[]? Pixels { get; init; }

    public int Stride => Width * 4;

    /// <summary>
    /// Decodes a file and uploads it. <paramref name="keepPixels"/> retains the CPU copy, which is
    /// only worth doing for a surface something will read back.
    /// </summary>
    public static unsafe ImageSurface Load(
        string path, IComObject<ID2D1DeviceContext> context, bool keepPixels = false)
    {
        var (pixels, width, height) = Decode(path);

        var properties = new D2D1_BITMAP_PROPERTIES1
        {
            pixelFormat = new D2D1_PIXEL_FORMAT
            {
                format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
            },
            dpiX = 96,
            dpiY = 96,
        };

        fixed (byte* data = pixels)
        {
            var bitmap = context.CreateBitmap(
                new D2D_SIZE_U { width = (uint)width, height = (uint)height },
                (nint)data,
                (uint)(width * 4),
                properties);

            return new ImageSurface
            {
                Bitmap = bitmap,
                Width = width,
                Height = height,
                Pixels = keepPixels ? pixels : null,
            };
        }
    }

    /// <summary>
    /// Decodes an image down to fit a box, for thumbnails.
    ///
    /// WIC scales as part of the decode, so the full-resolution bitmap is never allocated - which is
    /// the whole point. A history of 4K captures each cached at full size is hundreds of megabytes
    /// for images that end up in a 52x34 chip.
    /// </summary>
    public static ImageSurface LoadScaled(
        string path, IComObject<ID2D1DeviceContext> context, int maxWidth, int maxHeight) =>
        Upload(DecodeScaled(path, maxWidth, maxHeight), context);

    /// <summary>The CPU half of <see cref="LoadScaled"/>: decodes and scales, touching no device, so
    /// it is safe to call from any thread.</summary>
    public static unsafe DecodedImage DecodeScaled(string path, int maxWidth, int maxHeight)
    {
        using var decoder = WicImagingFactory.CreateDecoderFromFilename(path);
        using var frame = decoder.GetFrame(0);
        frame.Object.GetSize(out var sourceWidth, out var sourceHeight).ThrowOnError();

        var scale = Math.Min(
            maxWidth / (double)sourceWidth,
            maxHeight / (double)sourceHeight);
        scale = Math.Min(1, scale);

        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));

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

        var stride = width * 4;
        var pixels = new byte[stride * height];
        fixed (byte* buffer = pixels)
        {
            converter.Object.CopyPixels(0, (uint)stride, (uint)pixels.Length, (nint)buffer).ThrowOnError();
        }

        return new DecodedImage(pixels, width, height);
    }

    /// <summary>The GPU half: uploads decoded pixels. Must run on the thread that owns the device.</summary>
    public static unsafe ImageSurface Upload(DecodedImage image, IComObject<ID2D1DeviceContext> context)
    {
        fixed (byte* buffer = image.Pixels)
        {
            var bitmap = context.CreateBitmap(
                new D2D_SIZE_U { width = (uint)image.Width, height = (uint)image.Height },
                (nint)buffer,
                (uint)(image.Width * 4),
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

            return new ImageSurface
            {
                Bitmap = bitmap,
                Width = image.Width,
                Height = image.Height,
                Pixels = null,
            };
        }
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
    public static unsafe (byte[] Pixels, int Width, int Height) Decode(string path)
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
        var stride = (int)width * 4;
        var pixels = new byte[stride * (int)height];

        fixed (byte* buffer = pixels)
        {
            converter.Object.CopyPixels(0, (uint)stride, (uint)pixels.Length, (nint)buffer).ThrowOnError();
        }
        return (pixels, (int)width, (int)height);
    }

    public void Dispose() => Bitmap.Dispose();
}

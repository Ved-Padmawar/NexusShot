namespace NexusShot.Render;

/// <summary>
/// The decoded source image as a GPU bitmap.
///
/// Decoded via WIC and uploaded at full resolution; the GPU rescales it every frame, so the view
/// always samples the real image rather than a pre-scaled copy. The CPU-side pixels are released as
/// soon as the upload completes - holding them would double the cost of every open image - and an
/// export reads them back only for as long as it runs.
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
    /// it is safe to call from any thread. The caller owns the result. With <paramref name="cover"/>
    /// the image fills the box instead of fitting inside it, for a view that crops to the box.</summary>
    public static DecodedImage DecodeScaled(string path, int maxWidth, int maxHeight, bool cover = false)
    {
        using var decoder = WicImagingFactory.CreateDecoderFromFilename(path);
        using var frame = decoder.GetFrame(0);
        using var rotator = Rotator(frame);
        IWICBitmapSource source = rotator is null ? frame.Object : rotator.Object;
        source.GetSize(out var sourceWidth, out var sourceHeight).ThrowOnError();

        var toWidth = maxWidth / (double)sourceWidth;
        var toHeight = maxHeight / (double)sourceHeight;
        var scale = Math.Min(1, cover ? Math.Max(toWidth, toHeight) : Math.Min(toWidth, toHeight));

        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        if ((long)width * height > MaximumPixels)
            throw new InvalidOperationException("Image too large to decode.");

        using var scaler = WicImagingFactory.CreateBitmapScaler();
        scaler.Object.Initialize(
            source, (uint)width, (uint)height,
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

    /// <summary>An icon's pixels, premultiplied and uploaded. The caller keeps the icon.</summary>
    public static ImageSurface FromIcon(IntPtr icon, IComObject<ID2D1DeviceContext> context)
    {
        using var bitmap = WicImagingFactory.CreateBitmapFromHICON(new HICON { Value = icon });
        using var converter = WicImagingFactory.CreateFormatConverter();
        converter.Object.Initialize(bitmap.Object, Constants.GUID_WICPixelFormat32bppPBGRA,
            WICBitmapDitherType.WICBitmapDitherTypeNone, null!, 0,
            WICBitmapPaletteType.WICBitmapPaletteTypeCustom).ThrowOnError();
        converter.Object.GetSize(out var width, out var height).ThrowOnError();
        using var pixels = CopyPixels(converter.Object, (int)width, (int)height);
        return Upload(pixels, context);
    }

    /// <summary>The image's dimensions as shown, without decoding it. WIC reads the header only.</summary>
    public static (int Width, int Height) ReadSize(string path)
    {
        using var decoder = WicImagingFactory.CreateDecoderFromFilename(path);
        using var frame = decoder.GetFrame(0);
        frame.Object.GetSize(out var width, out var height).ThrowOnError();
        return Orientation(frame) >= 5 ? ((int)height, (int)width) : ((int)width, (int)height);
    }

    /// <summary>
    /// Decodes to premultiplied BGRA - the format D2D composites in, so no conversion happens on
    /// the hot path. WIC does the premultiplication as part of the format conversion.
    /// </summary>
    public static DecodedImage Decode(string path)
    {
        using var decoder = WicImagingFactory.CreateDecoderFromFilename(path);
        return Decode(decoder);
    }

    /// <summary>For bytes that never touch disk: a pasted image.</summary>
    public static DecodedImage Decode(Stream stream)
    {
        using var decoder = WicImagingFactory.CreateDecoderFromStream(stream);
        return Decode(decoder);
    }

    private static DecodedImage Decode(IComObject<IWICBitmapDecoder> decoder)
    {
        using var frame = decoder.GetFrame(0);
        using var rotator = Rotator(frame);
        using var converter = WicImagingFactory.CreateFormatConverter();

        converter.Object.Initialize(
            rotator is null ? frame.Object : rotator.Object,
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

    /// <summary>
    /// Turns a frame upright, or null when it already is. A camera stores its pixels sensor-side up
    /// with an EXIF orientation saying how to turn them; every read here applies it, so size,
    /// thumbnail and editor agree, and a save writes the pixels upright.
    /// </summary>
    private static IComObject<IWICBitmapFlipRotator>? Rotator(IComObject<IWICBitmapFrameDecode> frame)
    {
        // EXIF 5 and 7 mirror across a diagonal: WIC rotates first, then flips.
        var transform = Orientation(frame) switch
        {
            2 => WICBitmapTransformOptions.WICBitmapTransformFlipHorizontal,
            3 => WICBitmapTransformOptions.WICBitmapTransformRotate180,
            4 => WICBitmapTransformOptions.WICBitmapTransformFlipVertical,
            5 => WICBitmapTransformOptions.WICBitmapTransformRotate90 | WICBitmapTransformOptions.WICBitmapTransformFlipHorizontal,
            6 => WICBitmapTransformOptions.WICBitmapTransformRotate90,
            7 => WICBitmapTransformOptions.WICBitmapTransformRotate270 | WICBitmapTransformOptions.WICBitmapTransformFlipHorizontal,
            8 => WICBitmapTransformOptions.WICBitmapTransformRotate270,
            _ => WICBitmapTransformOptions.WICBitmapTransformRotate0,
        };
        if (transform == WICBitmapTransformOptions.WICBitmapTransformRotate0) return null;

        var rotator = WicImagingFactory.CreateBitmapFlipRotator();
        try { rotator.Object.Initialize(frame.Object, transform).ThrowOnError(); }
        catch { rotator.Dispose(); throw; }
        return rotator;
    }

    /// <summary>The EXIF orientation, 1 (upright) when the format or file carries none.</summary>
    private static unsafe ushort Orientation(IComObject<IWICBitmapFrameDecode> frame)
    {
        using var reader = frame.GetMetadataQueryReader();
        if (reader is null) return 1;
        var value = new PROPVARIANT();
        fixed (char* name = "System.Photo.Orientation")
        {
            if (reader.Object.GetMetadataByName(new PWSTR(name), ref value).IsError) return 1;
        }
        return value.Anonymous.Anonymous.vt == VARENUM.VT_UI2 ? value.Anonymous.Anonymous.Anonymous.uiVal : (ushort)1;
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

    /// <summary>The pixels back from the GPU, so an export flattens what is on screen, not a file that
    /// may have moved or changed. Runs on the device's thread; the caller owns the result.</summary>
    public unsafe DecodedImage Read(IComObject<ID2D1DeviceContext> context)
    {
        using var staging = context.CreateBitmap<ID2D1Bitmap1>(
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
                bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CPU_READ
                    | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
            });
        staging.Object.CopyFromBitmap(nint.Zero, Bitmap.Object, nint.Zero).ThrowOnError();

        staging.Object.Map(D2D1_MAP_OPTIONS.D2D1_MAP_OPTIONS_READ, out var mapped).ThrowOnError();
        var image = DecodedImage.Allocate(Width, Height);
        try
        {
            // Row by row: the GPU's pitch is not width*4.
            for (var y = 0; y < Height; y++)
                Buffer.MemoryCopy((byte*)mapped.bits + (long)y * mapped.pitch, (byte*)image.Pointer + (long)y * image.Stride,
                    image.Stride, image.Stride);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
        finally
        {
            staging.Object.Unmap().ThrowOnError();
        }
    }

    public void Dispose() => Bitmap.Dispose();
}

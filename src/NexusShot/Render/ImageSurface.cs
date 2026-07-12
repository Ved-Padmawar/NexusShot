using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>
/// The decoded source image: a GPU bitmap for drawing, plus the CPU pixels for anything that needs
/// to read them back (export, colour picking).
///
/// Decoding goes through WIC. The XAML build had to stream a file into a BitmapImage because a
/// file:// URI silently never decoded in an unpackaged app - that whole class of problem does not
/// exist here. Neither does the blurry preview: the bitmap is uploaded at full resolution and the
/// GPU rescales it every frame, so the view always samples the real image rather than a pre-scaled
/// copy, at any zoom.
/// </summary>
public sealed class ImageSurface : IDisposable
{
    public required IComObject<ID2D1Bitmap> Bitmap { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Premultiplied BGRA, top-down. The same bytes that were uploaded.</summary>
    public required byte[] Pixels { get; init; }

    public int Stride => Width * 4;

    /// <summary>Decodes a file to premultiplied BGRA and uploads it.</summary>
    public static unsafe ImageSurface Load(string path, IComObject<ID2D1DeviceContext> context)
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
                Pixels = pixels,
            };
        }
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

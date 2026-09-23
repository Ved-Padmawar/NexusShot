using NexusShot.Core;

namespace NexusShot.Render;

/// <summary>
/// Image encoding, via WIC. Shared by the exporter and the capture path so there is exactly one
/// place that knows how a NexusShot file is written.
/// </summary>
public static class ImageWriter
{
    /// <summary>Writes premultiplied BGRA pixels, top-down, as a PNG.</summary>
    public static void Write(string path, DecodedImage image) =>
        Write(path, image.Pointer, image.Width, image.Height, image.Stride, ImageFormat.Png);

    /// <summary>Writes from a raw buffer, for a caller that already holds one - the exporter reads
    /// back from a mapped GPU bitmap, whose rows the driver may pad beyond width * 4. JPEG and BMP
    /// carry no alpha, so they are written as 24-bit; the images they open from are opaque.</summary>
    public static void Write(string path, IntPtr premultipliedBgra, int width, int height, int stride, ImageFormat format)
    {
        using var bitmap = WicImagingFactory.CreateBitmapFromMemory(
            (uint)width,
            (uint)height,
            Constants.GUID_WICPixelFormat32bppPBGRA,
            (uint)stride,
            (uint)(stride * height),
            premultipliedBgra);

        var (container, pixelFormat) = format switch
        {
            ImageFormat.Jpeg => (Constants.GUID_ContainerFormatJpeg, Constants.GUID_WICPixelFormat24bppBGR),
            ImageFormat.Bmp => (Constants.GUID_ContainerFormatBmp, Constants.GUID_WICPixelFormat24bppBGR),
            _ => (Constants.GUID_ContainerFormatPng, Constants.GUID_WICPixelFormat32bppPBGRA),
        };

        using var converter = WicImagingFactory.CreateFormatConverter();
        converter.Object.Initialize(
            bitmap.Object,
            pixelFormat,
            WICBitmapDitherType.WICBitmapDitherTypeNone,
            null!,
            0,
            WICBitmapPaletteType.WICBitmapPaletteTypeCustom).ThrowOnError();

        using var file = File.Create(path);
        using var stream = new ManagedIStream(file);
        using var encoder = WicImagingFactory.CreateEncoder(container);
        encoder.Initialize(stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache);

        using var frame = encoder.CreateNewFrame();
        frame.Initialize();
        frame.SetSize((uint)width, (uint)height);
        frame.SetPixelFormat(pixelFormat);
        frame.WriteSource(converter);
        frame.Commit();
        encoder.Commit();
    }
}

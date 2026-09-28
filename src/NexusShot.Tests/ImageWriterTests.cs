using DirectN;
using DirectN.Extensions;
using DirectN.Extensions.Com;
using DirectN.Extensions.Utilities;
using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot.Tests;

public class ImageWriterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("nexusshot-writer-").FullName;

    [Theory]
    [InlineData(ImageFormat.Jpeg, ".jpg")]
    [InlineData(ImageFormat.Bmp, ".bmp")]
    public void WritesAFileThatDecodesBackAtTheSameSize(ImageFormat format, string extension)
    {
        using var image = DecodedImage.Allocate(32, 20);
        image.Span.Fill(0xFF);
        var path = Path.Combine(_directory, "out" + extension);

        ImageWriter.Write(path, image.Pointer, image.Width, image.Height, image.Stride, format);

        using var decoded = ImageSurface.Decode(path);
        Assert.Equal((32, 20), (decoded.Width, decoded.Height));
    }

    [Fact]
    public unsafe void AnExifRotationIsAppliedToSizeAndPixels()
    {
        // 16 x 8, left half red and right half blue, tagged "rotate 90 clockwise to view".
        using var image = DecodedImage.Allocate(16, 8);
        var pixels = image.Span;
        pixels.Clear();
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 16; x++)
        {
            var i = (y * 16 + x) * 4;
            pixels[i + (x < 8 ? 2 : 0)] = 255;   // BGRA
            pixels[i + 3] = 255;
        }
        var path = Path.Combine(_directory, "rotated.jpg");
        WriteJpegWithOrientation(path, image, orientation: 6);

        Assert.Equal((8, 16), ImageSurface.ReadSize(path));
        using var decoded = ImageSurface.Decode(path);
        Assert.Equal((8, 16), (decoded.Width, decoded.Height));
        // Turned clockwise, the left half becomes the top.
        Assert.True(decoded.Span[(4 * 8 + 4) * 4 + 2] > 200, "top should be red");
        Assert.True(decoded.Span[(12 * 8 + 4) * 4] > 200, "bottom should be blue");
    }

    private static unsafe void WriteJpegWithOrientation(string path, DecodedImage image, ushort orientation)
    {
        using var bitmap = WicImagingFactory.CreateBitmapFromMemory((uint)image.Width, (uint)image.Height,
            Constants.GUID_WICPixelFormat32bppPBGRA, (uint)image.Stride, (uint)image.ByteLength, image.Pointer);
        using var file = File.Create(path);
        using var stream = new ManagedIStream(file);
        using var encoder = WicImagingFactory.CreateEncoder(Constants.GUID_ContainerFormatJpeg);
        encoder.Initialize(stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache);
        using var frame = encoder.CreateNewFrame();
        frame.Initialize();
        frame.SetSize((uint)image.Width, (uint)image.Height);
        var format = Constants.GUID_WICPixelFormat24bppBGR;
        frame.SetPixelFormat(format);
        using (var writer = frame.GetMetadataQueryWriter())
        {
            var value = new PROPVARIANT();
            value.Anonymous.Anonymous.vt = VARENUM.VT_UI2;
            value.Anonymous.Anonymous.Anonymous.uiVal = orientation;
            fixed (char* name = "/app1/ifd/{ushort=274}")
                writer.Object.SetMetadataByName(new PWSTR(name), in value).ThrowOnError();
        }
        using var converter = WicImagingFactory.CreateFormatConverter();
        converter.Object.Initialize(bitmap.Object, format, WICBitmapDitherType.WICBitmapDitherTypeNone, null!, 0,
            WICBitmapPaletteType.WICBitmapPaletteTypeCustom).ThrowOnError();
        frame.WriteSource(converter);
        frame.Commit();
        encoder.Commit();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

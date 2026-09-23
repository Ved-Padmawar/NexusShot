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

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

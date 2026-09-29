using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Tests;

/// <summary>What a launch asks for, and a pasted DIB read back as an image.</summary>
public class LaunchAndPasteTests
{
    private static LaunchRequest Parse(params string[] args) => LaunchRequest.Parse(args, path => path.EndsWith(".png"));

    [Fact]
    public void ACaptureIsAskedForByName()
    {
        Assert.Equal(CaptureCommand.Window, Parse("--capture", "WINDOW").Capture);
        Assert.Equal(CaptureCommand.Text, Parse("--startup", "--capture", "text").Capture);
        Assert.False(Parse("--capture", "window").ShowsLibrary);
    }

    [Fact]
    public void AnUnknownCaptureOrArgumentStillOpensTheApp()
    {
        var request = Parse("--capture", "everything");
        Assert.Null(request.Capture);
        Assert.True(request.ShowsLibrary);
        Assert.True(Parse("--bogus").ShowsLibrary);
    }

    [Fact]
    public void AnExistingFileOpensAndSignInStaysInTheTray()
    {
        Assert.Equal(Path.GetFullPath("shot.png"), Parse("shot.png").File);
        Assert.Null(Parse("missing.jpg").File);
        Assert.False(Parse("--startup").ShowsLibrary);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APastedDibDecodesToThePixelsItCarries(bool v5)
    {
        var headerSize = v5 ? 124 : 40;
        byte[] pixels = [10, 20, 30, 255, 40, 50, 60, 255];
        var dib = new byte[headerSize + pixels.Length];
        ClipboardImage.WriteDib(dib, pixels, 2, 1, headerSize, 8, v5);

        using var image = ImageSurface.Decode(new MemoryStream(ClipboardReader.AsBitmapFile(dib)));

        Assert.Equal((2, 1), (image.Width, image.Height));
        Assert.Equal(new byte[] { 10, 20, 30 }, image.Span[..3].ToArray());
        Assert.Equal(new byte[] { 40, 50, 60 }, image.Span[4..7].ToArray());
    }
}

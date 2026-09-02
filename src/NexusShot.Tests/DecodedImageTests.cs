using NexusShot.Render;

namespace NexusShot.Tests;

/// <summary>
/// The unmanaged pixel buffer. These pixels used to be a <c>byte[]</c>, which put a full-screen
/// capture on the large object heap and left the process holding that address space long after the
/// image was dead.
/// </summary>
public class DecodedImageTests
{
    [Fact]
    public void AllocatesFourBytesPerPixel()
    {
        using var image = DecodedImage.Allocate(64, 32);

        Assert.Equal(64, image.Width);
        Assert.Equal(32, image.Height);
        Assert.Equal(64 * 4, image.Stride);
        Assert.Equal(64 * 32 * 4, image.ByteLength);
        Assert.Equal(image.ByteLength, image.Span.Length);
    }

    [Fact]
    public void PixelsWrittenThroughTheSpanReadBack()
    {
        using var image = DecodedImage.Allocate(4, 4);
        image.Span.Fill(0);
        image.Span[5] = 200;

        Assert.Equal(200, image.Span[5]);
        Assert.Equal(0, image.Span[6]);
    }

    [Fact]
    public void UsingADisposedImageThrowsRatherThanReadingFreedMemory()
    {
        var image = DecodedImage.Allocate(8, 8);
        image.Dispose();

        // Reading through a dangling pointer would be silent corruption, so both accessors check.
        Assert.Throws<ObjectDisposedException>(() => _ = image.Pointer);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            foreach (var _ in image.Span) break;
        });
    }

    [Fact]
    public void DisposingTwiceFreesOnceAndDoesNotThrow()
    {
        // Ownership transfers between the decode, the upload and the clipboard copy, so a second
        // Dispose on an already-released image must be harmless rather than a double free.
        var image = DecodedImage.Allocate(8, 8);
        image.Dispose();
        image.Dispose();
    }

    [Fact]
    public void CropCopiesTheSelectedRectangle()
    {
        using var source = DecodedImage.Allocate(4, 4);
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
                source.Span[y * source.Stride + x * 4] = (byte)(y * 4 + x);

        using var crop = source.Crop(1, 1, 2, 2);

        Assert.Equal(2, crop.Width);
        Assert.Equal(2, crop.Height);
        Assert.Equal(5, crop.Span[0]);                    // source (1,1)
        Assert.Equal(6, crop.Span[4]);                    // source (2,1)
        Assert.Equal(9, crop.Span[crop.Stride]);          // source (1,2)
    }

    [Fact]
    public void CropIsIndependentOfTheImageItCameFrom()
    {
        var source = DecodedImage.Allocate(4, 4);
        source.Span.Fill(7);
        var crop = source.Crop(0, 0, 2, 2);

        // The region overlay frees the desktop snapshot as soon as the crop is taken.
        source.Dispose();

        Assert.Equal(7, crop.Span[0]);
        crop.Dispose();
    }

    [Fact]
    public void CropClampsASelectionRunningPastTheEdge()
    {
        // A selection rounded a pixel beyond the desktop edge narrows to the last row and column
        // rather than producing an empty or inverted rectangle.
        using var source = DecodedImage.Allocate(4, 4);

        using var crop = source.Crop(3, 3, 99, 99);

        Assert.Equal(1, crop.Width);
        Assert.Equal(1, crop.Height);
    }

    [Fact]
    public void CopyFromTakesItsOwnCopyOfTheCallersPixels()
    {
        var pixels = new byte[2 * 2 * 4];
        pixels[0] = 42;

        using var image = DecodedImage.CopyFrom(pixels, 2, 2);
        pixels[0] = 0;

        Assert.Equal(42, image.Span[0]);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(-1, 4)]
    public void ADegenerateSizeIsRejected(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DecodedImage.Allocate(width, height));
    }

    [Fact]
    public void AnAbandonedImageIsReclaimedByItsFinalizer()
    {
        // The buffer is the largest allocation the process makes, so a missed Dispose must not
        // strand it for the life of the app. Dropped without disposing here on purpose.
        var reference = AllocateAndAbandon();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(reference.IsAlive);

        static WeakReference AllocateAndAbandon() => new(DecodedImage.Allocate(256, 256));
    }
}

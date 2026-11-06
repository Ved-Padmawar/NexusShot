using DirectN;
using DirectN.Extensions;
using DirectN.Extensions.Com;
using DirectN.Extensions.Utilities;
using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Tests;

public sealed class RecordingEncoderTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("nexusshot-recording-").FullName;
    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static RecordingPlan Plan(RecordingFormat format, int width, int height) =>
        new(new AppSettings { RecordingFormat = format }, width, height);

    private static DecodedImage Solid(byte blue, byte green, byte red)
    {
        var pixels = DecodedImage.Allocate(64, 48);
        for (var i = 0; i < pixels.ByteLength; i += 4)
        {
            pixels.Span[i] = blue;
            pixels.Span[i + 1] = green;
            pixels.Span[i + 2] = red;
            pixels.Span[i + 3] = 255;
        }
        return pixels;
    }

    [Fact]
    public void GifHasDistinctFramesDelaysAndAnInfiniteLoop()
    {
        var path = Path.Combine(_folder, "animation.gif");
        using (var encoder = new GifEncoder(path))
        using (var red = Solid(0, 0, 255))
        using (var blue = Solid(255, 0, 0))
        {
            encoder.Write(red, 0, TimeSpan.FromMilliseconds(100).Ticks);
            encoder.Write(blue, TimeSpan.FromMilliseconds(100).Ticks, TimeSpan.FromMilliseconds(250).Ticks);
            encoder.Complete();
        }
        using var decoder = WicImagingFactory.CreateDecoderFromFilename(path);
        decoder.Object.GetFrameCount(out var count).ThrowOnError();
        Assert.Equal(2u, count);
        using var first = decoder.GetFrame(0);
        using var second = decoder.GetFrame(1);
        Assert.Equal((ushort)10, Delay(first.Object));
        Assert.Equal((ushort)25, Delay(second.Object));
        Assert.Contains("NETSCAPE2.0", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path)));
        using var pixels = ImageSurface.Decode(path);
        Assert.Equal(255, pixels.Span[2]);
        Assert.Equal(0, pixels.Span[0]);
    }

    [Fact]
    public void GifFramesAfterTheFirstStoreOnlyWhatChanged()
    {
        var path = Path.Combine(_folder, "changes.gif");
        using (var encoder = new GifEncoder(path))
        using (var red = Solid(0, 0, 255))
        using (var marked = Solid(0, 0, 255))
        {
            for (var y = 10; y < 18; y++)
                for (var x = 20; x < 26; x++)
                    marked.Span[y * marked.Stride + x * 4] = 255;
            encoder.Write(red, 0, TimeSpan.TicksPerSecond);
            encoder.Write(marked, TimeSpan.TicksPerSecond, TimeSpan.TicksPerSecond);
            encoder.Write(marked, 2 * TimeSpan.TicksPerSecond, TimeSpan.TicksPerSecond);
            encoder.Complete();
        }
        using var decoder = WicImagingFactory.CreateDecoderFromFilename(path);
        using var first = decoder.GetFrame(0);
        using var changed = decoder.GetFrame(1);
        using var unchanged = decoder.GetFrame(2);
        Assert.Equal((64u, 48u), Size(first.Object));
        Assert.Equal((6u, 8u), Size(changed.Object));
        Assert.Equal(((ushort)20, (ushort)10), ((ushort)Metadata(changed.Object, "/imgdesc/Left")!, (ushort)Metadata(changed.Object, "/imgdesc/Top")!));
        Assert.Equal((1u, 1u), Size(unchanged.Object));
    }

    private static (uint, uint) Size(IWICBitmapFrameDecode frame)
    {
        frame.GetSize(out var width, out var height).ThrowOnError();
        return (width, height);
    }

    private static object? Delay(IWICBitmapFrameDecode frame) => Metadata(frame, "/grctlext/Delay");

    private static object? Metadata(IWICBitmapFrameDecode frame, string query)
    {
        frame.GetMetadataQueryReader(out var raw).ThrowOnError();
        using var reader = new ComObject<IWICMetadataQueryReader>(raw);
        using Pwstr name = query;
        var value = new PROPVARIANT();
        reader.Object.GetMetadataByName(name, ref value).ThrowOnError();
        using var variant = PropVariant.Attach(ref value);
        return variant.Value;
    }

    [Fact]
    public async Task Mp4RetainsSizeDurationAndTopDownPixelOrder()
    {
        var path = Path.Combine(_folder, "video.mp4");
        using (var encoder = new Mp4Encoder(path, Plan(RecordingFormat.Mp4, 64, 48)))
        using (var pixels = Solid(0, 0, 255))
        {
            for (var i = pixels.ByteLength / 2; i < pixels.ByteLength; i += 4)
            {
                pixels.Span[i] = 255;
                pixels.Span[i + 2] = 0;
            }
            for (var i = 0; i < 10; i++) encoder.Write(pixels, i * TimeSpan.TicksPerSecond / 10, TimeSpan.TicksPerSecond / 10);
            encoder.Complete();
        }
        Assert.Equal((64, 48), CaptureMedia.ReadSize(path));
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var properties = await file.Properties.GetVideoPropertiesAsync();
        Assert.InRange(properties.Duration.TotalSeconds, 0.95, 1.05);
        using var thumbnail = CaptureMedia.Thumbnail(path, 64, 48);
        var top = thumbnail.Stride * (thumbnail.Height / 4) + thumbnail.Width / 2 * 4;
        var bottom = thumbnail.Stride * (thumbnail.Height * 3 / 4) + thumbnail.Width / 2 * 4;
        Assert.True(thumbnail.Span[top + 2] > 200 && thumbnail.Span[top] < 50);
        Assert.True(thumbnail.Span[bottom] > 200 && thumbnail.Span[bottom + 2] < 50);
    }

    [Fact]
    public void ARecordingIsPublishedOnlyAfterCommit()
    {
        var plan = Plan(RecordingFormat.Gif, 64, 48);
        var path = Path.Combine(_folder, ".pending.tmp");
        File.WriteAllText(path, "test");
        using var pending = new PendingRecording(path, _folder, plan);
        Assert.Empty(Directory.GetFiles(_folder, "*.gif"));
        var item = pending.Commit();
        Assert.True(File.Exists(item.FilePath));
        Assert.False(File.Exists(path));
        Assert.Equal((64, 48), (item.Width, item.Height));
    }

    [Fact]
    public void DiscardRemovesTheUnpublishedRecording()
    {
        var path = Path.Combine(_folder, ".pending.tmp");
        File.WriteAllText(path, "test");
        new PendingRecording(path, _folder, Plan(RecordingFormat.Mp4, 64, 48)).Dispose();
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void CaptureFailureRemovesThePartialEncode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScreenRecording.Record(new RectInt(0, 0, 0, 0),
            Plan(RecordingFormat.Gif, 64, 48), _folder, false, new RecordingClock(), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void AFolderRescanFindsRecordingsAndIgnoresPartialFiles()
    {
        var gif = Path.Combine(_folder, "capture.gif");
        var mp4 = Path.Combine(_folder, "capture.mp4");
        using (var pixels = Solid(10, 20, 30))
        {
            using (var encoder = new GifEncoder(gif))
            {
                encoder.Write(pixels, 0, TimeSpan.TicksPerSecond);
                encoder.Complete();
            }
            using (var encoder = new Mp4Encoder(mp4, Plan(RecordingFormat.Mp4, 64, 48)))
            {
                encoder.Write(pixels, 0, TimeSpan.TicksPerSecond);
                encoder.Complete();
            }
        }
        File.WriteAllText(Path.Combine(_folder, ".partial.tmp"), "incomplete");
        var scan = HistoryScanner.Scan([_folder], [], new Dictionary<string, FileVersion>());
        Assert.Empty(scan.Unreadable);
        Assert.Equal(2, scan.Changed.Count);
        Assert.All(scan.Changed, found => Assert.Equal((64, 48), (found.Item.Width, found.Item.Height)));
    }
}

using NexusShot.Core;
using NexusShot.Render;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

/// <summary>
/// The export, by its pixels. The exporter draws with the same renderer as the screen, so these are
/// the renderer's tests too. Expected pixels come from the geometry the test states, never from the
/// renderer's own helpers, so a drawing bug cannot agree with itself.
/// </summary>
public class ExportRenderTests : IDisposable
{
    private static readonly Rgba Red = new(255, 0, 0);
    private static readonly Rgba White = Rgba.White;
    private const int Size = 128;

    private readonly string _directory = Directory.CreateTempSubdirectory("nexusshot-export-").FullName;
    private readonly string _source;
    private readonly string _output;

    public ExportRenderTests()
    {
        _source = Path.Combine(_directory, "source.png");
        _output = Path.Combine(_directory, "result.png");
        using var canvas = DecodedImage.Allocate(Size, Size);
        canvas.Span.Fill(255);
        ImageWriter.Write(_source, canvas);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static EditorDocument Blank()
    {
        var document = new EditorDocument { ColorHex = "#FF0000" };
        document.SetImageSize(Size, Size);
        return document;
    }

    private DecodedImage Export(EditorDocument document, Rect? crop = null)
    {
        Exporter.Save(document, _source, _output, crop);
        return ImageSurface.Decode(_output);
    }

    private static Rgba At(DecodedImage image, int x, int y) =>
        image.OpaquePixelAt(x, y) ?? throw new ArgumentOutOfRangeException(nameof(x));

    [Theory]
    [InlineData(EditorTool.Rectangle)]
    [InlineData(EditorTool.Ellipse)]
    public void OutlinedShapesStayHollowAndInsideTheirBounds(EditorTool tool)
    {
        var document = Blank();
        Draw(document, tool, new Point(32, 32), new Point(96, 96), thickness: 8);

        using var image = Export(document);

        Assert.Equal(White, At(image, 4, 4));
        Assert.Equal(White, At(image, 64, 64));
        Assert.Equal(White, At(image, 30, 64));      // the stroke is inset, never painted past the box
        Assert.Equal(Red, At(image, 34, 64));        // the left edge's stroke
        Assert.Equal(tool == EditorTool.Rectangle ? Red : White, At(image, 36, 36));   // an ellipse has no corner
    }

    [Fact]
    public void ASolidFillPaintsTheInteriorAndATintOnlyWashesIt()
    {
        var solid = Blank();
        solid.SetFill(ShapeFill.Solid);
        Draw(solid, EditorTool.Rectangle, new Point(32, 32), new Point(96, 96));
        using (var image = Export(solid)) Assert.Equal(Red, At(image, 64, 64));

        var tinted = Blank();
        tinted.SetFill(ShapeFill.Tinted);
        Draw(tinted, EditorTool.Rectangle, new Point(32, 32), new Point(96, 96));
        using (var image = Export(tinted))
        {
            var wash = At(image, 64, 64);
            Assert.Equal(255, wash.R);
            Assert.InRange(wash.G, 150, 230);
        }
    }

    [Fact]
    public void ALineIsSolidAlongItsLengthAndNowhereElse()
    {
        var document = Blank();
        Draw(document, EditorTool.Line, new Point(20, 64), new Point(108, 64), thickness: 8);

        using var image = Export(document);

        Assert.Equal(Red, At(image, 64, 64));
        Assert.Equal(White, At(image, 64, 80));
    }

    [Fact]
    public void TheHighlighterLetsTheCaptureShowThrough()
    {
        var document = Blank();
        Draw(document, EditorTool.Highlight, new Point(32, 32), new Point(96, 96));

        using var image = Export(document);

        var tinted = At(image, 64, 64);
        Assert.Equal(255, tinted.R);
        Assert.InRange(tinted.G, 100, 254);
        Assert.Equal(White, At(image, 10, 10));
    }

    [Fact]
    public void TheSpotlightDimsEverythingOutsideItsRegionOnly()
    {
        var document = Blank();
        Draw(document, EditorTool.Spotlight, new Point(32, 32), new Point(96, 96));

        using var image = Export(document);

        Assert.Equal(White, At(image, 64, 64));
        var dimmed = At(image, 10, 10);
        Assert.True(dimmed.R < 200 && dimmed.R == dimmed.G && dimmed.G == dimmed.B, dimmed.ToString());
    }

    [Fact]
    public void AnArrowEndsInAFilledHeadAtItsTip()
    {
        var document = Blank();
        Draw(document, EditorTool.Arrow, new Point(10, 64), new Point(110, 64), thickness: 6);

        using var image = Export(document);

        // Off the shaft's width but inside the head's spread, behind the tip.
        Assert.Equal(Red, At(image, 95, 68));
        Assert.Equal(White, At(image, 40, 68));
    }

    [Fact]
    public void ACounterIsAFilledBadgeWithAContrastingNumber()
    {
        var document = Blank();
        Counter(document, new Point(64, 64));

        using var image = Export(document);

        var badge = Enumerable.Range(52, 24).SelectMany(y => Enumerable.Range(52, 24).Select(x => At(image, x, y))).ToList();
        Assert.Contains(Red, badge);
        Assert.Contains(badge, pixel => pixel.G > 200 && pixel.B > 200);   // white digit on red
        Assert.Equal(White, At(image, 20, 20));
    }

    [Fact]
    public void TextIsPaintedInsideItsBoxOnly()
    {
        var document = Blank();
        var text = Draw(document, EditorTool.Text, new Point(10, 10), new Point(118, 60));
        document.SetTextContent(text, "Hello", text.Bounds);

        using var image = Export(document);

        var box = text.Bounds;
        var inked = 0;
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                if (At(image, x, y) == White) continue;
                Assert.True(box.Contains(new Point(x, y)), $"ink at {x},{y} outside the box {box}");
                inked++;
            }
        Assert.True(inked > 20, "the text drew nothing");
    }

    [Fact]
    public void BoldingOneWordLeavesTheNextOneRegular()
    {
        byte[] Render(TextStyle style, TextRun[] runs)
        {
            var document = Blank();
            var text = Draw(document, EditorTool.Text, new Point(4, 10), new Point(124, 60), thickness: 20);
            document.SetTextContent(text, "Hi Hi", text.Bounds, (style, runs));
            using var image = Export(document);
            return image.Span.ToArray();
        }

        var firstBold = Render(TextStyle.None, [new(3, TextStyle.Bold), new(2, TextStyle.None)]);
        var allBold = Render(TextStyle.Bold, []);

        // The first word, bold in both, sits in the same place in both; so the second word starts at
        // the same x, and only its weight can differ.
        int Differences(int fromX, int toX) => Enumerable.Range(0, Size).Sum(y => Enumerable.Range(fromX, toX - fromX)
            .Count(x => firstBold[(y * Size + x) * 4 + 1] != allBold[(y * Size + x) * 4 + 1]));
        var secondWord = Enumerable.Range(0, Size).First(x => Differences(x, x + 1) > 0);
        Assert.True(secondWord > 30, $"the first word differs at x {secondWord}");   // "Hi " at 20 px ends past x 30
        Assert.True(Differences(secondWord, Size) > 20, "the second word came out bold");
    }

    [Fact]
    public void AnEmptyTextBoxAndTheSelectionGripsNeverReachTheFile()
    {
        var document = Blank();
        Draw(document, EditorTool.Text, new Point(10, 10), new Point(118, 60));
        var shape = Draw(document, EditorTool.Rectangle, new Point(40, 70), new Point(100, 120));
        document.SelectAnnotation(shape);

        using var image = Export(document);

        Assert.Equal(White, At(image, 10, 10));      // the empty box's editing frame
        Assert.Equal(White, At(image, 70, 95));      // inside the selected shape, where grips would sit
    }

    [Fact]
    public void AnErasedStrokeKeepsBothEndsAndLosesItsMiddle()
    {
        var document = Blank();
        Stroke(document, EditorTool.Pen, [new Point(20, 64), new Point(108, 64)], thickness: 16);
        Stroke(document, EditorTool.Eraser, [new Point(64, 64)], thickness: 24);

        using var image = Export(document);

        Assert.Equal(Red, At(image, 32, 64));
        Assert.Equal(White, At(image, 64, 64));
        Assert.Equal(Red, At(image, 96, 64));
    }

    [Theory]
    [InlineData(EditorTool.Blur)]
    [InlineData(EditorTool.Pixelate)]
    public void AnEffectDabChangesThePixelsUnderItAndNoOthers(EditorTool tool)
    {
        // A hard edge for the effect to smear: left half black, right half white.
        using (var canvas = DecodedImage.Allocate(Size, Size))
        {
            for (var y = 0; y < Size; y++)
                for (var x = 0; x < Size; x++)
                    canvas.Span.Slice((y * Size + x) * 4, 4).Fill(x < Size / 2 ? (byte)0 : (byte)255);
            for (var i = 3; i < canvas.ByteLength; i += 4) canvas.Span[i] = 255;
            ImageWriter.Write(_source, canvas);
        }

        var document = Blank();
        var dab = Stroke(document, tool, [new Point(64, 64)], thickness: 10);

        using var image = Export(document);

        var changed = 0;
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var original = x < Size / 2 ? Rgba.Black : White;
                if (At(image, x, y) == original) continue;
                Assert.True(new Point(x, y).DistanceTo(new Point(64, 64)) <= dab.BrushRadius + 2,
                    $"{tool} changed {x},{y}, outside its footprint");
                changed++;
            }
        Assert.True(changed > 0, $"{tool} left the edge untouched");
    }

    [Fact]
    public void ACropKeepsTheSourcePixelsItFramed()
    {
        // Coordinate-coded, so the check covers the crop's origin and not only its size.
        using (var canvas = DecodedImage.Allocate(Size, Size))
        {
            for (var y = 0; y < Size; y++)
                for (var x = 0; x < Size; x++)
                {
                    var at = (y * Size + x) * 4;
                    (canvas.Span[at], canvas.Span[at + 1], canvas.Span[at + 2], canvas.Span[at + 3]) = ((byte)x, (byte)y, 0, 255);
                }
            ImageWriter.Write(_source, canvas);
        }

        var document = Blank();
        document.BeginCropSession();
        Drag(document, new Point(Size, Size), new Point(64, 64));
        Drag(document, new Point(32, 32), new Point(48, 48));
        document.CommitCrop();

        using var image = Export(document);

        Assert.Equal((64, 64), (image.Width, image.Height));
        Assert.Equal(new Rgba(0, 16, 16), At(image, 0, 0));
        Assert.Equal(new Rgba(0, 79, 79), At(image, 63, 63));
    }

    [Fact]
    public void ACropOverrideExportsTheFrameWithoutCommittingIt()
    {
        var document = Blank();
        document.BeginCropSession();

        using var image = Export(document, new Rect(10, 20, 30, 40));

        Assert.Equal((30, 40), (image.Width, image.Height));
        Assert.True(document.IsCropSessionActive);
        Assert.Null(document.CropBounds);
    }

    [Theory]
    [InlineData(".jpg", new byte[] { 0xFF, 0xD8 })]
    [InlineData(".bmp", new byte[] { (byte)'B', (byte)'M' })]
    [InlineData(".png", new byte[] { 0x89, (byte)'P' })]
    public void TheFileIsWrittenInTheFormatItsNameAsksFor(string extension, byte[] magic)
    {
        var path = Path.Combine(_directory, "out" + extension);
        Exporter.Save(Blank(), _source, path);
        Assert.Equal(magic, File.ReadAllBytes(path)[..2]);
    }

    /// <summary>Blur and pixelate change nothing on a flat image; their own test gives them an edge.</summary>
    public static TheoryData<EditorTool> DrawingTools() =>
        [.. Enum.GetValues<EditorTool>().Where(tool => tool is not (EditorTool.Select or EditorTool.Crop
            or EditorTool.Eraser or EditorTool.Blur or EditorTool.Pixelate))];

    [Theory]
    [MemberData(nameof(DrawingTools))]
    public void EveryToolPaintsAndUndoRestoresTheOriginalExactly(EditorTool tool)
    {
        var original = File.ReadAllBytes(_source);
        using var before = ImageSurface.Decode(_source);
        var document = Blank();
        Draw(document, tool, new Point(20, 20), new Point(100, 90), thickness: 12);
        if (tool == EditorTool.Text) document.Annotations[0].Text = "Text";

        using (var drawn = Export(document))
            Assert.False(drawn.Span.SequenceEqual(before.Span), $"{tool} painted nothing");

        document.Undo();
        using (var undone = Export(document))
            Assert.True(undone.Span.SequenceEqual(before.Span), $"undo left {tool} pixels behind");
        Assert.Equal(original, File.ReadAllBytes(_source));
    }

    [Fact]
    public void AFailedOverwriteLeavesTheDestinationByteForByte()
    {
        File.Copy(_source, _output);
        var before = File.ReadAllBytes(_output);
        var document = Blank();
        Draw(document, EditorTool.Rectangle, new Point(10, 10), new Point(60, 60));

        using (new FileStream(_output, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Record.Exception(() => Exporter.Save(document, _source, _output));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        }

        Assert.Equal(before, File.ReadAllBytes(_output));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }
}

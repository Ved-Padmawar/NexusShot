using NexusShot.Core;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

/// <summary>
/// Colour parsing and the derived state that hangs off it, plus the two document-level stamps the
/// renderer's caches key on. All of it is reachable without a device.
/// </summary>
public class ColorAndCacheTests
{
    [Fact]
    public void ValidHexParsesToItsComponents()
    {
        Assert.True(Palette.TryParse("#0A84FF", out var color));
        Assert.Equal(new Rgba(10, 132, 255), color);
    }

    [Fact]
    public void HexParsesWithOrWithoutTheHash()
    {
        Assert.True(Palette.TryParse("34C759", out var bare));
        Assert.True(Palette.TryParse("#34C759", out var hashed));
        Assert.Equal(hashed, bare);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    [InlineData("#FF3B300")]
    public void MalformedHexIsRejectedRatherThanBecomingASwatch(string? hex)
    {
        Assert.False(Palette.TryParse(hex, out var color));

        // The point of the named fallback: a bad string must not silently resolve to whichever
        // swatch happens to sit first in the toolbar.
        Assert.Equal(Palette.Fallback, color);
    }

    [Fact]
    public void ParseFallsBackInsteadOfThrowingOnThePaintPath()
    {
        Assert.Equal(Palette.Fallback, Palette.Parse("not-a-colour"));
    }

    [Fact]
    public void AnnotationColorTracksItsHex()
    {
        var document = NewDocument();
        var rectangle = Draw(document, EditorTool.Rectangle, new Point(10, 10), new Point(80, 80));

        document.SelectAnnotation(rectangle);
        document.SetColor("#34C759");

        Assert.Equal("#34C759", rectangle.ColorHex);
        Assert.Equal(new Rgba(52, 199, 89), rectangle.Color);
    }

    [Fact]
    public void StrokeBoundsFollowPointsAppendedAfterTheFirstRead()
    {
        var document = NewDocument();
        document.ActiveTool = EditorTool.Pen;
        document.BeginGesture(new Point(100, 100));
        document.ContinueGesture(new Point(140, 130));
        var stroke = Assert.Single(document.Annotations);

        // Reads the memoised value, then grows the stroke: a cache that ignored the geometry stamp
        // would keep reporting the old box.
        var before = stroke.Bounds;
        Assert.Equal(40, before.Width, 3);

        document.ContinueGesture(new Point(220, 190));
        Assert.Equal(new Rect(100, 100, 120, 90), stroke.Bounds);
        document.EndGesture(new Point(240, 200));
        Assert.Equal(new Rect(100, 100, 140, 100), stroke.Bounds);
    }

    [Fact]
    public void AnnotationGenerationMovesOnAddAndRemove()
    {
        var document = NewDocument();
        var start = document.AnnotationGeneration;

        var rectangle = Draw(document, EditorTool.Rectangle, new Point(10, 10), new Point(80, 80));
        var afterAdd = document.AnnotationGeneration;
        Assert.True(afterAdd > start);

        document.SelectAnnotation(rectangle);
        document.DeleteSelected();
        Assert.True(document.AnnotationGeneration > afterAdd);
    }

    [Fact]
    public void UndoMovesTheGenerationEvenWhenTheCountIsUnchanged()
    {
        var document = NewDocument();
        var rectangle = Draw(document, EditorTool.Rectangle, new Point(10, 10), new Point(80, 80));

        document.SelectAnnotation(rectangle);
        document.SetColor("#34C759");

        var before = document.AnnotationGeneration;
        document.Undo();

        // Undo restores clones: the count is identical, but every annotation is a new instance, so
        // a cache keyed on count alone would hold geometry for objects the document has dropped.
        Assert.Single(document.Annotations);
        Assert.NotSame(rectangle, document.Annotations[0]);
        Assert.True(document.AnnotationGeneration > before);
    }
}

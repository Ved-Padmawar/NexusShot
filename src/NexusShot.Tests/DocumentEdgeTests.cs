using NexusShot.Core;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

/// <summary>Document writers that are only reached from one toolbar button each, so a regression in
/// one would otherwise show up only by hand: clearing a crop, text formatting, fills on the wrong
/// kind of shape, no-op edits, and the selection surviving undo.</summary>
public class DocumentEdgeTests
{
    [Fact]
    public void ClearingACropIsUndoableAndANoOpWithoutOne()
    {
        var document = NewDocument();
        document.ClearCrop();
        Assert.False(document.CanUndo);

        document.BeginCropSession();
        Drag(document, new Point(ImageWidth, ImageHeight), new Point(400, 300));
        document.CommitCrop();
        document.ClearCrop();
        Assert.Null(document.CropBounds);

        document.Undo();
        Assert.Equal(new Rect(0, 0, 400, 300), document.CropBounds);
    }

    [Fact]
    public void FormattingASelectedTextBoxIsOneUndoStepAndSetsTheDefault()
    {
        var document = NewDocument();
        var text = Draw(document, EditorTool.Text, new Point(100, 100), new Point(400, 200));
        document.SelectAnnotation(text);

        document.SetTextStyle(TextStyle.Bold, on: true);

        Assert.Equal(TextStyle.Bold, text.Style);
        Assert.Equal(TextStyle.Bold, document.TextStyle);
        document.Undo();
        Assert.Equal(TextStyle.None, Assert.Single(document.Annotations).Style);
    }

    [Fact]
    public void FormattingWithAShapeSelectedChangesOnlyTheDefault()
    {
        var document = NewDocument();
        Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(400, 200));
        document.Undo();
        document.Redo();
        Assert.False(document.CanRedo);
        document.SelectAnnotation(document.Annotations[0]);

        document.SetTextStyle(TextStyle.Italic, on: true);

        Assert.Equal(TextStyle.Italic, document.TextStyle);
        Assert.Equal(TextStyle.None, document.Annotations[0].Style);
        document.Undo();
        Assert.Empty(document.Annotations);     // the undo was the shape's creation, not a format step
    }

    [Fact]
    public void AFillPickedWithALineSelectedIsOnlyTheNewDefault()
    {
        var document = NewDocument();
        var line = Draw(document, EditorTool.Line, new Point(100, 100), new Point(400, 200));
        document.SelectAnnotation(line);

        document.SetFill(ShapeFill.Solid);

        Assert.Equal(ShapeFill.Solid, document.ShapeFill);
        document.Undo();
        Assert.Empty(document.Annotations);
    }

    [Fact]
    public void PickingTheColourASelectionAlreadyHasCostsNoUndoStep()
    {
        var document = NewDocument();
        var shape = Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));
        document.SelectAnnotation(shape);

        document.SetColor(shape.ColorHex);
        document.Undo();

        Assert.Empty(document.Annotations);
    }

    [Fact]
    public void UndoReselectsTheRestoredCopyOfTheSelection()
    {
        var document = NewDocument();
        var shape = Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));
        Drag(document, new Point(200, 200), new Point(250, 250));

        document.Undo();

        Assert.NotNull(document.Selected);
        Assert.Equal(shape.Id, document.Selected.Id);
        Assert.Same(document.Annotations[0], document.Selected);
    }

    [Fact]
    public void AnExportSnapshotTakesThePendingFrameAndSharesNothing()
    {
        var document = NewDocument();
        Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));
        document.BeginCropSession();
        Drag(document, new Point(ImageWidth, ImageHeight), new Point(500, 400));

        var snapshot = document.CreateExportSnapshot();
        document.Annotations[0].Translate(10, 10);

        Assert.Equal(new Rect(0, 0, 500, 400), snapshot.CropBounds);
        Assert.Equal(new Rect(100, 100, 200, 200), snapshot.Annotations[0].Bounds);
        Assert.False(snapshot.CanUndo);
        Assert.Null(snapshot.Selected);
    }

    [Fact]
    public void UndoingPastACounterResetStillNumbersWithinTheNewRun()
    {
        var document = NewDocument();
        Counter(document, new Point(100, 100));
        Counter(document, new Point(200, 100));
        document.ResetCounter();
        Counter(document, new Point(300, 100));
        Counter(document, new Point(400, 100));

        document.Undo();

        Assert.Equal(2, document.NextCounter);
    }

    [Fact]
    public void TheEraserSizeIsItsOwnAndNeverResizesTheSelection()
    {
        var document = NewDocument();
        var shape = Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));
        document.SelectAnnotation(null);
        document.ActiveTool = EditorTool.Eraser;

        document.SetStrokeThickness(90);

        Assert.Equal(90, document.EraserThickness);
        Assert.Equal(4, shape.StrokeThickness);
        Assert.Equal(4, document.StrokeThickness);
    }

    [Theory]
    [InlineData(EditorTool.Blur, 2)]
    [InlineData(EditorTool.Pixelate, 12)]
    public void AnEffectDabCoversExactlyTheCursorRing(EditorTool tool, double thickness)
    {
        var document = NewDocument();
        var dab = Stroke(document, tool, [new Point(500, 400)], thickness);

        Assert.Equal(PaintStrokeGeometry.EffectRadius(document.ActiveThickness) * 2, dab.Bounds.Width, 6);
    }

    [Fact]
    public void TheCropToolWithoutASessionDrawsNothing()
    {
        var document = new EditorDocument { ActiveTool = EditorTool.Crop };
        document.BeginCropSession();      // no image yet, so no session
        Drag(document, new Point(10, 10), new Point(200, 200));

        Assert.False(document.IsCropSessionActive);
        Assert.Empty(document.Annotations);
    }

    [Fact]
    public void AnArrowIsGrabbedAlongItsShaftNotAcrossItsBox()
    {
        var arrow = new Annotation
        {
            Tool = EditorTool.Arrow, Start = new Point(100, 100), End = new Point(300, 300), StrokeThickness = 4,
        };
        Assert.True(arrow.HitTest(new Point(200, 200)));
        Assert.False(arrow.HitTest(new Point(290, 110)));
    }

    [Fact]
    public void ToolShortcutsRoundTripAndNoTwoToolsShareALetter()
    {
        var tools = Enum.GetValues<EditorTool>();
        foreach (var tool in tools)
            Assert.Equal(tool, ToolShortcuts.ToolFor(ToolShortcuts.Letter(tool)));

        Assert.Equal(tools.Length, tools.Select(ToolShortcuts.Letter).Distinct().Count());
        Assert.Equal(EditorTool.Blur, ToolShortcuts.ToolFor('B'));
        Assert.Equal(EditorTool.Pixelate, ToolShortcuts.ToolFor('P'));
        Assert.Null(ToolShortcuts.ToolFor('Q'));
        Assert.Null(ToolShortcuts.ToolFor('b'));
    }

    [Theory]
    [InlineData("""{"tag_name": 4}""")]
    [InlineData("""null""")]
    [InlineData("""[]""")]
    [InlineData("""{"tag_name": "v0.4.0", "assets": [1, "x"]}""")]
    [InlineData("""{"tag_name": "v0.4.0", "assets": [{"name": 5, "browser_download_url": 6}]}""")]
    [InlineData("""{"tag_name": "not a version", "assets": []}""")]
    [InlineData("""{broken""")]
    public void AMalformedReleaseIsNoUpdateRatherThanAnError(string json) => Assert.Null(Updates.Parse(json));

    [Fact]
    public void ATwoPartTagIsReadAsPatchZero()
    {
        var release = Updates.Parse($$"""
            { "tag_name": "V1.2", "assets": [
              { "name": "NexusShot-1.2.0.exe", "browser_download_url": "{{Updates.DownloadPrefix}}v1.2/NexusShot-1.2.0.exe" },
              { "name": "NexusShot-1.2.0.exe.sig", "browser_download_url": "{{Updates.DownloadPrefix}}v1.2/NexusShot-1.2.0.exe.sig" } ] }
            """);
        Assert.Equal(new Version(1, 2, 0), release!.Version);
        Assert.Equal(0, release.InstallerSize);
    }

    [Theory]
    [InlineData(1920, 48, 40)]
    [InlineData(40, 48, 1)]
    [InlineData(100, 0, 100)]
    public void ThePreviewShrinkFactorNeverDropsBelowOne(int width, int target, int factor) =>
        Assert.Equal(factor, Downsample.FactorFor(width, target));
}

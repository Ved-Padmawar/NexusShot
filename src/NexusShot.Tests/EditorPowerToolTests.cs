using NexusShot.Core;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

/// <summary>Duplicate, nudge, paint order and dashes: each one undo step, each kept inside the image.</summary>
public class EditorPowerToolTests
{
    [Fact]
    public void ADuplicateIsANewAnnotationOffsetAndSelected()
    {
        var document = NewDocument();
        var original = Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(200, 150));

        document.DuplicateSelected();

        Assert.Equal(2, document.Annotations.Count);
        var copy = document.Annotations[1];
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Same(copy, document.Selected);
        Assert.Equal(new Rect(116, 116, 100, 50), copy.Bounds);
        document.Undo();
        Assert.Single(document.Annotations);
    }

    [Fact]
    public void ARunOfNudgesIsOneUndoStepAndStaysInTheImage()
    {
        var document = NewDocument();
        var shape = Draw(document, EditorTool.Rectangle, new Point(5, 100), new Point(50, 150));

        document.NudgeSelected(-1, 0);
        document.NudgeSelected(-10, 0);
        document.NudgeSelected(0, 1);

        Assert.Equal(new Rect(0, 101, 45, 50), shape.Bounds);
        document.Undo();
        Assert.Equal(new Rect(5, 100, 45, 50), document.Annotations[0].Bounds);
    }

    [Fact]
    public void ReorderingMovesThroughThePaintOrder()
    {
        var document = NewDocument();
        var bottom = Draw(document, EditorTool.Rectangle, new Point(10, 10), new Point(50, 50));
        var middle = Draw(document, EditorTool.Rectangle, new Point(60, 10), new Point(90, 50));
        var top = Draw(document, EditorTool.Rectangle, new Point(100, 10), new Point(150, 50));

        document.SelectAnnotation(bottom);
        document.Reorder(LayerMove.Forward);
        Assert.Equal([middle.Id, bottom.Id, top.Id], document.Annotations.Select(a => a.Id));

        document.Reorder(LayerMove.Front);
        Assert.Equal([middle.Id, top.Id, bottom.Id], document.Annotations.Select(a => a.Id));

        document.Reorder(LayerMove.Front);
        document.Reorder(LayerMove.Back);
        Assert.Equal([bottom.Id, middle.Id, top.Id], document.Annotations.Select(a => a.Id));

        document.Undo();
        Assert.Equal([middle.Id, top.Id, bottom.Id], document.Annotations.Select(a => a.Id));
    }

    [Fact]
    public void PaintOrderIsOfferedOnlyWhenTheSelectionOverlapsSomething()
    {
        var document = NewDocument();
        Draw(document, EditorTool.Rectangle, new Point(10, 10), new Point(50, 50));
        Draw(document, EditorTool.Rectangle, new Point(300, 300), new Point(350, 350));
        Assert.False(document.SelectionOverlaps);

        Draw(document, EditorTool.Ellipse, new Point(30, 30), new Point(80, 80));
        Assert.True(document.SelectionOverlaps);
    }

    [Fact]
    public void DashingAppliesToTheSelectionAndToWhatIsDrawnNext()
    {
        var document = NewDocument();
        var line = Draw(document, EditorTool.Line, new Point(10, 10), new Point(200, 10));

        document.SetDashed(true);
        Assert.True(line.Dashed);

        var arrow = Draw(document, EditorTool.Arrow, new Point(10, 100), new Point(200, 100));
        Assert.True(arrow.Dashed);
        Assert.True(arrow.Clone().Dashed);

        document.Undo();
        document.Undo();
        Assert.False(document.Annotations[0].Dashed);
    }
}

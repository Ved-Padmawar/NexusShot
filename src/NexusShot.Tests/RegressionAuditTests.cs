using NexusShot.Core;
using NexusShot.Views;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

public class RegressionAuditTests
{
    [Fact]
    public void UndoCommittedCropPreservesAnnotationsAndClosesTheCropSession()
    {
        var d = NewDocument();
        var shape = Draw(d, EditorTool.Rectangle, new(100, 100), new(200, 200));
        d.BeginCropSession();
        Drag(d, new(ImageWidth, ImageHeight), new(400, 300));
        d.CommitCrop();
        d.Undo();
        Assert.Equal(shape.Id, Assert.Single(d.Annotations).Id);
        Assert.Null(d.CropBounds);
        Assert.Null(d.PendingCrop);
    }

    [Fact]
    public void RedoCommittedCropRestoresExactBounds()
    {
        var d = NewDocument();
        d.BeginCropSession();
        Drag(d, new(ImageWidth, ImageHeight), new(400, 300));
        d.CommitCrop();
        d.Undo();
        Assert.Null(d.CropBounds);
        d.Redo();
        Assert.Equal(new Rect(0, 0, 400, 300), d.CropBounds);
        Assert.Null(d.PendingCrop);
    }

    [Fact]
    public void CancellingCropPreservesRedo()
    {
        var d = DocumentWithRedo();
        d.BeginCropSession();
        Drag(d, new(ImageWidth, ImageHeight), new(400, 300));
        d.CancelCropSession();
        Assert.True(d.CanRedo);
        d.Redo();
        Assert.Equal(2, d.Annotations.Count);
        Assert.Null(d.CropBounds);
    }

    [Fact]
    public void CommittingUnchangedFullImageDoesNotConsumeRedo()
    {
        var d = DocumentWithRedo();
        d.BeginCropSession();
        d.CommitCrop();
        Assert.True(d.CanRedo);
        d.Undo();
        Assert.Empty(d.Annotations);
    }

    [Theory]
    [InlineData(EditorTool.Pen)]
    [InlineData(EditorTool.Brush)]
    [InlineData(EditorTool.Blur)]
    [InlineData(EditorTool.Pixelate)]
    [InlineData(EditorTool.Eraser)]
    public void StrokeIncludesTheReleasePointWithoutAnIntermediateMove(EditorTool tool)
    {
        var d = NewDocument();
        if (tool == EditorTool.Eraser)
            Stroke(d, EditorTool.Pen, [new(100, 200), new(400, 200)]);
        d.SelectAnnotation(null);
        d.ActiveTool = tool;
        d.BeginGesture(new(100, 200));
        d.EndGesture(new(400, 200));
        var annotation = Assert.Single(d.Annotations);
        var points = tool == EditorTool.Eraser ? Assert.Single(annotation.Erasures).Points : annotation.Points;
        Assert.Equal(new Point(100, 200), points[0]);
        Assert.Equal(new Point(400, 200), points[^1]);
    }

    [Fact]
    public void MoveUsesTheReleasePositionAndIsUndoable()
    {
        var d = NewDocument();
        var shape = Draw(d, EditorTool.Rectangle, new(100, 100), new(300, 300));
        d.BeginGesture(new(200, 200));
        d.EndGesture(new(260, 240));
        Assert.Equal(new Rect(160, 140, 200, 200), shape.Bounds);
        d.Undo();
        Assert.Equal(new Rect(100, 100, 200, 200), Assert.Single(d.Annotations).Bounds);
    }

    [Fact]
    public void ResizeUsesTheReleasePosition()
    {
        var d = NewDocument();
        var shape = Draw(d, EditorTool.Rectangle, new(100, 100), new(300, 300));
        d.BeginGesture(new(300, 300));
        d.EndGesture(new(450, 380));
        Assert.Equal(new Rect(100, 100, 350, 280), shape.Bounds);
    }

    [Fact]
    public void CropUsesTheReleasePosition()
    {
        var d = NewDocument();
        d.BeginCropSession();
        d.BeginGesture(new(ImageWidth, ImageHeight));
        d.EndGesture(new(400, 300));
        Assert.Equal(new Rect(0, 0, 400, 300), d.PendingCrop);
    }

    [Fact]
    public void ClosingDuringDispatchCancelsTheRestOfTheBatch()
    {
        var dispatch = new UiThreadDispatch(0);
        var calls = new List<string>();
        dispatch.Post(() => { calls.Add("close"); dispatch.Clear(); });
        dispatch.Post(() => calls.Add("stale window access"));
        dispatch.Drain();
        Assert.Equal(new[] { "close" }, calls);
    }

    [Fact]
    public void WorkPostedDuringDispatchRunsOnTheNextDrain()
    {
        var dispatch = new UiThreadDispatch(0);
        var calls = new List<int>();
        dispatch.Post(() => { calls.Add(1); dispatch.Post(() => calls.Add(3)); });
        dispatch.Post(() => calls.Add(2));
        dispatch.Drain();
        Assert.Equal(new[] { 1, 2 }, calls);
        dispatch.Drain();
        Assert.Equal(new[] { 1, 2, 3 }, calls);
    }

    [Fact]
    public void StationaryMoveDoesNotDiscardRedo()
    {
        var d = DocumentWithRedo();
        d.SelectAnnotation(d.Annotations[0]);
        d.BeginGesture(new(150, 150));
        d.ContinueGesture(new(150, 150));
        d.EndGesture(new(150, 150));
        Assert.True(d.CanRedo);
        d.Redo();
        Assert.Equal(2, d.Annotations.Count);
    }

    [Fact]
    public void DiscardedDegenerateShapePreservesRedo()
    {
        var d = DocumentWithRedo();
        d.ActiveTool = EditorTool.Rectangle;
        d.BeginGesture(new(800, 600));
        d.EndGesture(new(801, 601));
        Assert.True(d.CanRedo);
        d.Redo();
        Assert.Equal(2, d.Annotations.Count);
    }

    [Fact]
    public void ErasingEmptySpacePreservesRedo()
    {
        var d = DocumentWithRedo();
        Stroke(d, EditorTool.Eraser, [new(800, 600), new(850, 650)]);
        Assert.True(d.CanRedo);
        d.Redo();
        Assert.Equal(2, d.Annotations.Count);
    }

    [Fact]
    public void SnapshotDoesNotShareNestedEraserMaskPoints()
    {
        var d = NewDocument();
        var pen = Stroke(d, EditorTool.Pen, [new(100, 200), new(400, 200)]);
        Stroke(d, EditorTool.Eraser, [new(150, 200), new(250, 200)]);
        var snapshot = pen.Clone();
        var before = Assert.Single(snapshot.Erasures).Points.ToArray();
        pen.Translate(100, 100);
        pen.Erasures[0].Points.Add(new(999, 999));
        Assert.Equal(before, Assert.Single(snapshot.Erasures).Points);
        Assert.NotEqual(pen.Erasures[0].Points[0], snapshot.Erasures[0].Points[0]);
    }

    private static EditorDocument DocumentWithRedo()
    {
        var d = NewDocument();
        Draw(d, EditorTool.Rectangle, new(100, 100), new(200, 200));
        Draw(d, EditorTool.Ellipse, new(400, 400), new(500, 500));
        d.Undo();
        d.SelectAnnotation(null);
        Assert.True(d.CanRedo);
        return d;
    }

    [Theory]
    [InlineData(EditorTool.Rectangle)]
    [InlineData(EditorTool.Text)]
    public void SeparateSliderDragsHaveSeparateUndoAndRedoSteps(EditorTool tool)
    {
        var d = NewDocument();
        var original = tool == EditorTool.Text ? 20 : 4;
        Draw(d, tool, new(100, 100), new(500, 300), thickness: original);
        d.SetStrokeThickness(25, isAdjusting: true);
        d.SetStrokeThickness(30, isAdjusting: true);
        d.EndThicknessAdjustment();
        d.SetStrokeThickness(35, isAdjusting: true);
        d.SetStrokeThickness(40, isAdjusting: true);
        d.EndThicknessAdjustment();
        d.Undo();
        Assert.Equal(30, Value());
        d.Undo();
        Assert.Equal(original, Value());
        d.Redo();
        Assert.Equal(30, Value());
        d.Redo();
        Assert.Equal(40, Value());

        double Value()
        {
            var annotation = Assert.Single(d.Annotations);
            return tool == EditorTool.Text ? annotation.FontSize : annotation.StrokeThickness;
        }
    }

    [Theory]
    [InlineData(EditorTool.Rectangle)]
    [InlineData(EditorTool.Eraser)]
    public void DiscardedGestureDoesNotEvictTheOldestUndoAtCapacity(EditorTool tool)
    {
        var d = NewDocument();
        for (var i = 0; i < 100; i++)
            Draw(d, EditorTool.Rectangle, new(100, 100), new(200, 200));
        d.SelectAnnotation(null);
        d.ActiveTool = tool;
        d.BeginGesture(new(800, 700));
        d.EndGesture(new(800, 700));
        for (var i = 0; i < 100; i++)
        {
            Assert.True(d.CanUndo);
            d.Undo();
            Assert.Equal(99 - i, d.Annotations.Count);
        }
        Assert.False(d.CanUndo);
    }
}

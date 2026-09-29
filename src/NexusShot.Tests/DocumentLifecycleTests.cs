using NexusShot.Core;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

/// <summary>Document-wide state: what survives a save, what a tool switch does, and the notify
/// contract the view repaints on.</summary>
public class DocumentLifecycleTests
{
    [Fact]
    public void SavingKeepsAnnotationsCropAndHistory()
    {
        var document = NewDocument();
        Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));
        document.BeginCropSession();
        Drag(document, new Point(0, 0), new Point(50, 50));
        document.CommitCrop();

        document.MarkSaved(document.Revision);

        Assert.Single(document.Annotations);
        Assert.NotNull(document.CropBounds);
        Assert.True(document.CanUndo);
        Assert.False(document.HasUnsavedChanges);
    }

    [Fact]
    public void UndoingPastASaveIsDirtyAndRedoingBackIsClean()
    {
        var document = NewDocument();
        Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));
        document.MarkSaved(document.Revision);

        document.Undo();
        Assert.Empty(document.Annotations);
        Assert.True(document.HasUnsavedChanges);

        document.Redo();
        Assert.False(document.HasUnsavedChanges);
    }

    [Fact]
    public void AnEditMadeWhileASaveWritesStaysDirty()
    {
        var document = NewDocument();
        Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));
        var exported = document.Revision;

        Draw(document, EditorTool.Ellipse, new Point(400, 400), new Point(500, 500));
        document.MarkSaved(exported);

        Assert.True(document.HasUnsavedChanges);
    }

    [Fact]
    public void ACancelledCreationReturnsToTheSavedRevision()
    {
        var document = NewDocument();
        Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));
        document.MarkSaved(document.Revision);

        Draw(document, EditorTool.Rectangle, new Point(600, 600), new Point(600, 600));

        Assert.Single(document.Annotations);
        Assert.False(document.HasUnsavedChanges);
    }

    [Fact]
    public void FoundRedactionsAreOneUndoStepClippedToTheImage()
    {
        var document = NewDocument();
        document.ColorHex = "#FF0000";

        document.AddRedactions([new Rect(10, 10, 50, 20), new Rect(ImageWidth - 5, 0, 50, 20), new Rect(-100, -100, 10, 10)]);

        Assert.Equal(2, document.Annotations.Count);
        Assert.All(document.Annotations, redaction => Assert.Equal(Annotation.RedactColor, redaction.ColorHex));
        Assert.Equal(new Rect(ImageWidth - 5, 0, 5, 20), document.Annotations[1].Bounds);
        document.Undo();
        Assert.Empty(document.Annotations);
    }

    [Fact]
    public void ChangesRaiseTheNotification()
    {
        var document = NewDocument();
        var changes = 0;
        document.Changed += (_, _) => changes++;

        Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));

        Assert.True(changes > 0);
    }


    [Fact]
    public void OnlyBoxShapesAreResizable()
    {
        Assert.True(EditorDocument.IsBoxResizable(new Annotation { Tool = EditorTool.Rectangle }));
        Assert.True(EditorDocument.IsBoxResizable(new Annotation { Tool = EditorTool.Text }));
        Assert.True(EditorDocument.IsBoxResizable(new Annotation { Tool = EditorTool.Highlight }));

        Assert.False(EditorDocument.IsBoxResizable(new Annotation { Tool = EditorTool.Pen }));
        Assert.False(EditorDocument.IsBoxResizable(new Annotation { Tool = EditorTool.Blur }));
        Assert.False(EditorDocument.IsBoxResizable(new Annotation { Tool = EditorTool.Counter }));
    }

    [Fact]
    public void ALineIsResizedByItsEndpoints()
    {
        var document = NewDocument();
        var line = Draw(document, EditorTool.Line, new Point(100, 100), new Point(400, 400));
        document.SelectAnnotation(line);

        Drag(document, new Point(400, 400), new Point(500, 300));

        Assert.Equal(100, line.Start.X, 3);
        Assert.Equal(500, line.End.X, 3);
        Assert.Equal(300, line.End.Y, 3);
    }

    [Fact]
    public void TheDrawGestureFlagTracksTheActiveGesture()
    {
        var document = NewDocument();
        document.ActiveTool = EditorTool.Rectangle;

        Assert.False(document.IsDrawGestureActive);
        document.BeginGesture(new Point(100, 100));
        Assert.True(document.IsDrawGestureActive);
        document.EndGesture(new Point(300, 300));
        Assert.False(document.IsDrawGestureActive);
    }

    [Fact]
    public void BrushStrokesAreLeftUnselectedAfterDrawing()
    {
        var document = NewDocument();
        Stroke(document, EditorTool.Pen, [new Point(100, 100), new Point(200, 200)]);

        Assert.Null(document.Selected);
    }

    [Fact]
    public void ShapesStaySelectedAfterDrawingSoTheirHandlesAreReady()
    {
        var document = NewDocument();
        var rectangle = Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));

        Assert.Same(rectangle, document.Selected);
    }

}

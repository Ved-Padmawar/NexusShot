using NexusShot.Core;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

/// <summary>Document-wide state: what survives a save, what a tool switch does, and the notify
/// contract the view repaints on.</summary>
public class DocumentLifecycleTests
{
    [Fact]
    public void SavingClearsTheDocumentButKeepsTheTypingDefaults()
    {
        var document = NewDocument();
        document.ColorHex = "#00FF00";
        document.TextBold = true;
        Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));

        document.ResetAfterSave();

        Assert.Empty(document.Annotations);
        Assert.Null(document.Selected);
        Assert.Null(document.CropBounds);
        Assert.False(document.CanUndo);
        Assert.False(document.CanRedo);

        Assert.Equal("#00FF00", document.ColorHex);
        Assert.True(document.TextBold);
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

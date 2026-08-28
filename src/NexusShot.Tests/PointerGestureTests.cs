using NexusShot.Core;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

/// <summary>
/// What a press on the canvas does. The rule under test: a grab on the selection moves it whatever
/// tool is active, and a press that misses it still draws.
/// </summary>
public class PointerGestureTests
{

    [Fact]
    public void PressingAwayFromTheSelectionStillDraws()
    {
        var document = NewDocument();
        Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(200, 200));

        Drag(document, new Point(500, 500), new Point(650, 620));

        Assert.Equal(2, document.Annotations.Count);
    }



    [Fact]
    public void HandlesResizeRatherThanMoveWhenGrabbed()
    {
        var document = NewDocument();
        var rectangle = Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));
        document.SelectAnnotation(rectangle);

        // The bottom-right handle, dragged outward.
        Drag(document, new Point(300, 300), new Point(400, 380));

        Assert.Equal(100, rectangle.Bounds.X, 3);
        Assert.Equal(100, rectangle.Bounds.Y, 3);
        Assert.Equal(300, rectangle.Bounds.Width, 3);
        Assert.Equal(280, rectangle.Bounds.Height, 3);
    }

    [Fact]
    public void SelectToolPicksTheTopmostAnnotation()
    {
        var document = NewDocument();
        Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(400, 400));
        var above = Draw(document, EditorTool.Ellipse, new Point(150, 150), new Point(350, 350));

        document.SelectAnnotation(null);
        document.ActiveTool = EditorTool.Select;
        document.BeginGesture(new Point(250, 250));

        Assert.Same(above, document.Selected);
    }

    [Fact]
    public void AMoveIsClampedInsideTheImage()
    {
        var document = NewDocument();
        var rectangle = Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));
        document.SelectAnnotation(rectangle);

        Drag(document, new Point(200, 200), new Point(5000, 5000));

        Assert.True(rectangle.Bounds.Right <= ImageWidth + 0.001);
        Assert.True(rectangle.Bounds.Bottom <= ImageHeight + 0.001);
    }

    [Fact]
    public void ADegenerateShapeIsDiscardedWithItsUndoEntry()
    {
        var document = NewDocument();
        document.ActiveTool = EditorTool.Rectangle;
        document.BeginGesture(new Point(100, 100));
        document.EndGesture(new Point(101, 101));

        Assert.Empty(document.Annotations);
        Assert.False(document.CanUndo);
    }



    [Fact]
    public void DeleteRemovesTheSelectionAndIsUndoable()
    {
        var document = NewDocument();
        var rectangle = Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));
        document.SelectAnnotation(rectangle);

        document.DeleteSelected();
        Assert.Empty(document.Annotations);

        document.Undo();
        Assert.Single(document.Annotations);
    }
}

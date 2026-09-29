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
        document.SetFill(ShapeFill.Solid);
        Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(400, 400));
        var above = Draw(document, EditorTool.Ellipse, new Point(150, 150), new Point(350, 350));

        document.SelectAnnotation(null);
        document.ActiveTool = EditorTool.Select;
        document.BeginGesture(new Point(250, 250));

        Assert.Same(above, document.Selected);
    }

    [Fact]
    public void AnOutlineDrawnAroundAShapeDoesNotHideIt()
    {
        var document = NewDocument();
        var inside = Draw(document, EditorTool.Arrow, new Point(200, 250), new Point(300, 250));
        Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(400, 400));

        document.SelectAnnotation(null);
        document.ActiveTool = EditorTool.Select;
        document.BeginGesture(new Point(250, 250));

        Assert.Same(inside, document.Selected);
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

    [Fact]
    public void ACancelledDrawLeavesNoShapeAndNoUndoEntry()
    {
        var document = NewDocument();
        Draw(document, EditorTool.Rectangle, new Point(10, 10), new Point(80, 80));
        var revision = document.Revision;

        document.ActiveTool = EditorTool.Ellipse;
        document.SelectAnnotation(null);
        document.BeginGesture(new Point(300, 300));
        document.ContinueGesture(new Point(400, 400));
        document.CancelGesture();

        Assert.Single(document.Annotations);
        Assert.Equal(revision, document.Revision);
        document.Undo();
        Assert.Empty(document.Annotations);
    }

    [Fact]
    public void ACancelledMovePutsTheShapeBack()
    {
        var document = NewDocument();
        var shape = Draw(document, EditorTool.Rectangle, new Point(10, 10), new Point(80, 80));

        document.BeginGesture(new Point(40, 40));
        document.ContinueGesture(new Point(200, 200));
        document.CancelGesture();

        Assert.Equal(new Rect(10, 10, 70, 70), document.Annotations[0].Bounds);
        Assert.Equal(shape.Id, document.Selected?.Id);
        document.Undo();
        Assert.Empty(document.Annotations);
    }

    [Fact]
    public void ACancelledEraseRestoresWhatItErased()
    {
        var document = NewDocument();
        Stroke(document, EditorTool.Pen, [new Point(100, 100), new Point(300, 100)]);
        var revision = document.Revision;

        document.ActiveTool = EditorTool.Eraser;
        document.BeginGesture(new Point(200, 60));
        document.ContinueGesture(new Point(200, 140));
        Assert.NotEmpty(document.Annotations[0].Erasures);
        document.CancelGesture();

        Assert.Empty(document.Annotations[0].Erasures);
        Assert.Equal(revision, document.Revision);
    }
}

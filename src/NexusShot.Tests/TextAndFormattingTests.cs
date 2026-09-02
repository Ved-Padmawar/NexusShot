using NexusShot.Core;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

/// <summary>Text placement and the formatting writers, which apply both to new annotations and to
/// the current selection.</summary>
public class TextAndFormattingTests
{
    [Fact]
    public void AClickPlacesAWorkableTextBox()
    {
        var document = NewDocument();
        document.ActiveTool = EditorTool.Text;
        document.BeginGesture(new Point(100, 100));
        document.EndGesture(new Point(100, 100));

        var text = Assert.Single(document.Annotations);
        Assert.True(text.Bounds.Width > 0);
        Assert.True(text.Bounds.Height > 0);
    }

    [Fact]
    public void ADraggedTextBoxKeepsTheAreaTheUserFramed()
    {
        var document = NewDocument();
        var text = Draw(document, EditorTool.Text, new Point(100, 100), new Point(500, 300));

        Assert.Equal(400, text.Bounds.Width, 3);
        Assert.Equal(200, text.Bounds.Height, 3);
    }

    [Fact]
    public void SettingTextContentIsOneUndoStep()
    {
        var document = NewDocument();
        var text = Draw(document, EditorTool.Text, new Point(100, 100), new Point(500, 300));

        document.SetTextContent(text, "hello", text.Bounds);
        Assert.Equal("hello", text.Text);

        document.Undo();
        Assert.Equal(string.Empty, document.Annotations[0].Text);
    }

    [Fact]
    public void TextBoundsAreClampedIntoTheImage()
    {
        var document = NewDocument();
        var text = Draw(document, EditorTool.Text, new Point(100, 100), new Point(300, 200));

        document.SetTextContent(text, "x", new Rect(ImageWidth + 500, ImageHeight + 500, 200, 100));

        Assert.True(text.Bounds.Right <= ImageWidth + 0.001);
        Assert.True(text.Bounds.Bottom <= ImageHeight + 0.001);
    }

    [Fact]
    public void CancellingAnAnnotationRemovesItAndItsUndoEntry()
    {
        var document = NewDocument();
        var text = Draw(document, EditorTool.Text, new Point(100, 100), new Point(300, 200));

        document.CancelAnnotation(text);

        Assert.Empty(document.Annotations);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void CancellingDoesNotDiscardLaterHistory()
    {
        var document = NewDocument();
        var text = Draw(document, EditorTool.Text, new Point(100, 100), new Point(300, 200));

        // Another edit lands after the text was created, so its undo entry is no longer the newest.
        Draw(document, EditorTool.Rectangle, new Point(500, 500), new Point(700, 700));

        document.CancelAnnotation(text);

        // The rectangle survives, and the history entry for its creation is still on the stack:
        // cancelling the text must not have popped it.
        Assert.Single(document.Annotations);
        Assert.True(document.CanUndo);

        document.Undo();
        Assert.Contains(document.Annotations, a => a.Tool == EditorTool.Text);
    }


    [Fact]
    public void ThicknessRoutesByTool()
    {
        var document = NewDocument();

        document.ActiveTool = EditorTool.Brush;
        document.SetStrokeThickness(64);
        Assert.Equal(64, document.ActiveThickness, 3);

        document.ActiveTool = EditorTool.Eraser;
        document.SetStrokeThickness(32);
        Assert.Equal(32, document.ActiveThickness, 3);

        // The brush keeps its own size rather than taking the eraser's.
        document.ActiveTool = EditorTool.Brush;
        Assert.Equal(64, document.ActiveThickness, 3);
    }

    [Fact]
    public void ColourAppliesToTheSelectionAsOneUndoStep()
    {
        var document = NewDocument();
        var rectangle = Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));
        document.SelectAnnotation(rectangle);

        document.SetColor("#00FF00");
        Assert.Equal("#00FF00", rectangle.ColorHex);

        document.Undo();
        Assert.Equal("#FF3B30", document.Annotations[0].ColorHex);
    }

    [Fact]
    public void SlidingThicknessDoesNotFillTheUndoStack()
    {
        var document = NewDocument();
        var rectangle = Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));
        document.SelectAnnotation(rectangle);
        document.ActiveTool = EditorTool.Rectangle;

        for (var i = 5; i < 20; i++)
            document.SetStrokeThickness(i, isAdjusting: true);

        // The drag is one edit: undo should restore thickness, not delete the shape.
        document.Undo();
        Assert.Equal(4, Assert.Single(document.Annotations).StrokeThickness);
        document.Undo();
        Assert.Empty(document.Annotations);
        Assert.False(document.CanUndo);
    }







    [Fact]
    public void CountersNumberThemselvesInSequence()
    {
        var document = NewDocument();

        Assert.Equal(1, Counter(document, new Point(100, 100)).CounterValue);
        Assert.Equal(2, Counter(document, new Point(300, 100)).CounterValue);
        Assert.Equal(3, Counter(document, new Point(500, 100)).CounterValue);
    }
}

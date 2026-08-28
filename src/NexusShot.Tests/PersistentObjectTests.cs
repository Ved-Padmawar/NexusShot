using NexusShot.Core;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

/// <summary>
/// Text boxes and shapes as persistent objects: create → select → move → resize → deselect →
/// reselect, with the same object surviving every step. The rule under test is that a press on an
/// object the user has selected grabs it, and only a press on empty canvas creates something new.
/// </summary>
public class PersistentObjectTests
{
    [Fact]
    public void DraggingASelectedShapeMovesItRatherThanDrawingAnother()
    {
        var document = NewDocument();
        var rectangle = Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));

        Drag(document, new Point(200, 200), new Point(260, 240));

        Assert.Single(document.Annotations);
        Assert.Same(rectangle, document.Selected);
        Assert.Equal(160, rectangle.Bounds.X, 3);
        Assert.Equal(140, rectangle.Bounds.Y, 3);
        Assert.Equal(200, rectangle.Bounds.Width, 3);
        Assert.Equal(200, rectangle.Bounds.Height, 3);
    }

    [Fact]
    public void DraggingASelectedTextBoxMovesItRatherThanCreatingAnother()
    {
        var document = NewDocument();
        var text = PlaceText(document, new Point(100, 100), new Point(300, 180), "hello");
        var size = text.Bounds;

        Drag(document, new Point(200, 140), new Point(250, 200));

        Assert.Single(document.Annotations);
        Assert.Same(text, document.Selected);
        Assert.Equal("hello", text.Text);
        Assert.Equal(150, text.Bounds.X, 3);
        Assert.Equal(160, text.Bounds.Y, 3);
        Assert.Equal(size.Width, text.Bounds.Width, 3);
        Assert.Equal(size.Height, text.Bounds.Height, 3);
    }

    [Fact]
    public void ResizingATextBoxKeepsItAliveWithItsTextAndStyling()
    {
        var document = NewDocument();
        var text = PlaceText(document, new Point(100, 100), new Point(300, 180), "hello");
        text.IsBold = true;
        text.FontSize = 28;

        Drag(document, new Point(300, 180), new Point(240, 150));
        Drag(document, text.Bounds.BottomRight(), new Point(360, 260));

        Assert.Single(document.Annotations);
        Assert.Same(text, document.Selected);
        Assert.Equal("hello", text.Text);
        Assert.True(text.IsBold);
        Assert.Equal(28, text.FontSize, 3);
        Assert.Equal(100, text.Bounds.X, 3);
        Assert.Equal(100, text.Bounds.Y, 3);
        Assert.Equal(260, text.Bounds.Width, 3);
        Assert.Equal(160, text.Bounds.Height, 3);
    }

    [Fact]
    public void AnEllipseKeepsItsGeometryAcrossAResize()
    {
        var document = NewDocument();
        var ellipse = Draw(document, EditorTool.Ellipse, new Point(200, 200), new Point(400, 300));

        Drag(document, new Point(200, 200), new Point(150, 160));

        Assert.Single(document.Annotations);
        Assert.Equal(150, ellipse.Bounds.X, 3);
        Assert.Equal(160, ellipse.Bounds.Y, 3);
        Assert.Equal(250, ellipse.Bounds.Width, 3);
        Assert.Equal(140, ellipse.Bounds.Height, 3);
    }

    [Fact]
    public void AShapeSurvivesDeselectAndReselectAndMovesAgain()
    {
        var document = NewDocument();
        var rectangle = Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));

        // Deselect by pressing empty canvas with the select tool.
        document.ActiveTool = EditorTool.Select;
        document.BeginGesture(new Point(800, 700));
        document.EndGesture(new Point(800, 700));
        Assert.Null(document.Selected);
        Assert.Single(document.Annotations);

        Drag(document, new Point(200, 200), new Point(230, 250));

        Assert.Same(rectangle, document.Selected);
        Assert.Single(document.Annotations);
        Assert.Equal(130, rectangle.Bounds.X, 3);
        Assert.Equal(150, rectangle.Bounds.Y, 3);
    }

    [Fact]
    public void PressingEmptyCanvasWithAShapeToolStillCreatesOne()
    {
        var document = NewDocument();
        Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));

        document.ActiveTool = EditorTool.Rectangle;
        Drag(document, new Point(600, 600), new Point(750, 700));

        Assert.Equal(2, document.Annotations.Count);
    }

    [Fact]
    public void TheTextToolOnEmptyCanvasStillCreatesABox()
    {
        var document = NewDocument();
        PlaceText(document, new Point(100, 100), new Point(300, 180), "first");

        document.ActiveTool = EditorTool.Text;
        Drag(document, new Point(500, 400), new Point(700, 480));

        Assert.Equal(2, document.Annotations.Count);
    }

    [Fact]
    public void TheFullSequenceLeavesOneStableTextBox()
    {
        var document = NewDocument();

        // Create → type.
        var text = PlaceText(document, new Point(100, 100), new Point(300, 180), "hello");
        var id = text.Id;

        // Click outside → deselect.
        document.ActiveTool = EditorTool.Select;
        document.BeginGesture(new Point(800, 700));
        document.EndGesture(new Point(800, 700));
        Assert.Null(document.Selected);

        // Select → move → resize.
        Drag(document, new Point(200, 140), new Point(220, 160));
        Drag(document, document.Selected!.Bounds.BottomRight(), new Point(400, 260));

        // Edit the text on the same object.
        document.SetTextContent(text, "hello world", text.Bounds);

        // Move again → resize again.
        Drag(document, new Point(200, 180), new Point(210, 190));
        Drag(document, document.Selected!.Bounds.BottomRight(), new Point(420, 280));

        // Deselect → reselect → move again.
        document.BeginGesture(new Point(900, 750));
        document.EndGesture(new Point(900, 750));
        Assert.Null(document.Selected);
        Drag(document, new Point(200, 190), new Point(215, 205));

        Assert.Single(document.Annotations);
        Assert.Same(text, document.Selected);
        Assert.Equal(id, document.Annotations[0].Id);
        Assert.Equal("hello world", text.Text);
        Assert.True(text.Bounds.Width > 0 && text.Bounds.Height > 0);
    }

    [Fact]
    public void AHandleOnTheSelectionResizesRatherThanMoves()
    {
        var document = NewDocument();
        var text = PlaceText(document, new Point(100, 100), new Point(300, 180), "hello");

        // The bottom-right handle also lies inside the bounds, so the interior must not claim it.
        Drag(document, new Point(300, 180), new Point(340, 220));

        Assert.Same(text, document.Selected);
        Assert.Equal(100, text.Bounds.X, 3);
        Assert.Equal(100, text.Bounds.Y, 3);
        Assert.Equal(240, text.Bounds.Width, 3);
        Assert.Equal(120, text.Bounds.Height, 3);
    }

    [Fact]
    public void DeleteRemovesOnlyTheSelectedObject()
    {
        var document = NewDocument();
        var first = Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(200, 200));
        var second = Draw(document, EditorTool.Rectangle, new Point(400, 400), new Point(500, 500));

        document.SelectAnnotation(second);
        document.DeleteSelected();

        Assert.Single(document.Annotations);
        Assert.Same(first, document.Annotations[0]);
    }

    /// <summary>Places a text box by dragging it out, then writing its content back the way the
    /// inline editor's commit does.</summary>
    private static Annotation PlaceText(EditorDocument document, Point from, Point to, string content)
    {
        document.SelectAnnotation(null);
        document.ActiveTool = EditorTool.Text;
        document.BeginGesture(from);
        document.ContinueGesture(to);
        document.EndGesture(to);

        var text = document.Annotations[^1];
        document.SetTextContent(text, content, text.Bounds);
        return text;
    }
}

internal static class RectCorners
{
    internal static Point BottomRight(this Rect rect) => new(rect.Right, rect.Bottom);
}

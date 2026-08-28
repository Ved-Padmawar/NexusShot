using NexusShot.Core;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

/// <summary>
/// How a press on an open text box is split between the box and the text in it. The rule under
/// test: handles and the band inside the edge grab the box, and only the middle types - testing the
/// bounds alone is what swallowed every move and resize.
/// </summary>
public class TextBoxInteractionTests
{
    private static readonly Rect Box = new(100, 100, 200, 80);
    private const double Tolerance = 9;

    [Fact]
    public void TheMiddleOfABoxReachesItsText()
    {
        Assert.False(BoxGeometry.GrabsBox(Box, new Point(200, 140), Tolerance));
        Assert.True(BoxGeometry.Interior(Box, Tolerance).Contains(new Point(200, 140)));
    }

    [Fact]
    public void EveryHandleGrabsTheBoxRatherThanItsText()
    {
        foreach (var handle in BoxGeometry.Handles)
        {
            var position = BoxGeometry.HandlePosition(Box, handle);
            Assert.True(BoxGeometry.GrabsBox(Box, position, Tolerance), $"{handle} should grab the box.");
        }
    }

    [Fact]
    public void TheBandInsideTheEdgeMovesTheBox()
    {
        Assert.True(BoxGeometry.GrabsBox(Box, new Point(200, 103), Tolerance));
        Assert.True(BoxGeometry.GrabsBox(Box, new Point(103, 140), Tolerance));
    }

    [Fact]
    public void APointOutsideTheBoxGrabsNothing()
    {
        Assert.False(BoxGeometry.GrabsBox(Box, new Point(400, 400), Tolerance));
    }

    [Fact]
    public void ATinyBoxKeepsSomeTextArea()
    {
        var tiny = new Rect(10, 10, 12, 12);
        Assert.False(BoxGeometry.Interior(tiny, Tolerance).IsEmpty);
    }

    [Fact]
    public void AnOpenBoxStaysTheSelectionSoItsGripsAreLive()
    {
        var document = NewDocument();
        document.ActiveTool = EditorTool.Text;
        document.BeginGesture(new Point(100, 100));
        document.ContinueGesture(new Point(300, 180));
        document.EndGesture(new Point(300, 180));

        var text = document.Annotations[^1];
        Assert.Same(text, document.Selected);
        Assert.NotNull(document.GetResizeHandleAt(text, new Point(300, 180), Tolerance));
    }

    [Fact]
    public void TextSurvivesAResizeThatCrossesItsOwnEdge()
    {
        var document = NewDocument();
        document.ActiveTool = EditorTool.Text;
        document.BeginGesture(new Point(100, 100));
        document.ContinueGesture(new Point(300, 180));
        document.EndGesture(new Point(300, 180));

        var text = document.Annotations[^1];
        document.SetTextContent(text, "hello", text.Bounds);

        // Drag the bottom-right handle past the top-left corner and back.
        Drag(document, new Point(300, 180), new Point(60, 60));

        Assert.Single(document.Annotations);
        Assert.Equal("hello", text.Text);
        Assert.False(text.Bounds.IsEmpty);
    }

    [Fact]
    public void ADefaultBoxIsTallEnoughForItsLineAndPadding()
    {
        var document = NewDocument();
        document.ActiveTool = EditorTool.Text;
        document.BeginGesture(new Point(100, 100));
        document.EndGesture(new Point(100, 100));

        // A line box is about 1.2x the font; the renderer pads 3px above and below.
        var text = document.Annotations[^1];
        Assert.True(text.Bounds.Height >= text.FontSize * 1.2 + 6);
    }

    [Fact]
    public void DeletingTheSelectedBoxLeavesTheOthers()
    {
        var document = NewDocument();
        document.ActiveTool = EditorTool.Text;
        document.BeginGesture(new Point(100, 100));
        document.EndGesture(new Point(300, 180));
        var first = document.Annotations[^1];
        document.SetTextContent(first, "keep", first.Bounds);

        document.SelectAnnotation(null);
        document.ActiveTool = EditorTool.Text;
        document.BeginGesture(new Point(500, 400));
        document.EndGesture(new Point(700, 480));
        var second = document.Annotations[^1];
        document.SetTextContent(second, "drop", second.Bounds);

        document.SelectAnnotation(second);
        document.DeleteSelected();

        Assert.Single(document.Annotations);
        Assert.Same(first, document.Annotations[0]);
    }
}

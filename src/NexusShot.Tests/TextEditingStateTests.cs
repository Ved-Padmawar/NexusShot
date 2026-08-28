using NexusShot.Core;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

/// <summary>
/// The invariant that editing is a sub-state of selection: EditingText is always either null or the
/// selection. The view used to own this alone, so render, input and the cursor each reached their
/// own conclusion about whether a box was open.
/// </summary>
public class TextEditingStateTests
{
    private static Annotation PlaceText(EditorDocument document, Point from, Point to)
    {
        document.SelectAnnotation(null);
        document.ActiveTool = EditorTool.Text;
        document.BeginGesture(from);
        document.ContinueGesture(to);
        document.EndGesture(to);
        return document.Annotations[^1];
    }

    [Fact]
    public void OpeningABoxSelectsIt()
    {
        var document = NewDocument();
        var text = PlaceText(document, new Point(100, 100), new Point(300, 180));
        document.SelectAnnotation(null);

        document.BeginTextEdit(text);

        Assert.Same(text, document.EditingText);
        Assert.Same(text, document.Selected);
    }

    [Fact]
    public void SelectingSomethingElseClosesTheOpenBox()
    {
        var document = NewDocument();
        var text = PlaceText(document, new Point(100, 100), new Point(300, 180));
        var other = Draw(document, EditorTool.Rectangle, new Point(500, 500), new Point(600, 600));

        document.BeginTextEdit(text);
        document.SelectAnnotation(other);

        Assert.Null(document.EditingText);
        Assert.Same(other, document.Selected);
    }

    [Fact]
    public void DeselectingClosesTheOpenBox()
    {
        var document = NewDocument();
        var text = PlaceText(document, new Point(100, 100), new Point(300, 180));
        document.BeginTextEdit(text);

        document.SelectAnnotation(null);

        Assert.Null(document.EditingText);
    }

    [Fact]
    public void DeletingTheEditedBoxClosesTheEditor()
    {
        var document = NewDocument();
        var text = PlaceText(document, new Point(100, 100), new Point(300, 180));
        document.BeginTextEdit(text);

        document.DeleteSelected();

        Assert.Null(document.EditingText);
        Assert.Null(document.Selected);
    }

    [Fact]
    public void CancellingTheEditedBoxClosesTheEditor()
    {
        var document = NewDocument();
        var text = PlaceText(document, new Point(100, 100), new Point(300, 180));
        document.BeginTextEdit(text);

        document.CancelAnnotation(text);

        Assert.Null(document.EditingText);
        Assert.Empty(document.Annotations);
    }

    [Fact]
    public void UndoClosesTheEditorRatherThanLeavingAStaleClone()
    {
        var document = NewDocument();
        var text = PlaceText(document, new Point(100, 100), new Point(300, 180));
        document.SetTextContent(text, "hello", text.Bounds);
        document.BeginTextEdit(text);

        document.Undo();

        Assert.Null(document.EditingText);
        Assert.DoesNotContain(document.Annotations, a => ReferenceEquals(a, text));
    }

    [Fact]
    public void OnlyATextAnnotationCanBeEdited()
    {
        var document = NewDocument();
        var rectangle = Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));

        document.BeginTextEdit(rectangle);

        Assert.Null(document.EditingText);
    }

    [Fact]
    public void EndingTheEditKeepsTheBoxSelected()
    {
        var document = NewDocument();
        var text = PlaceText(document, new Point(100, 100), new Point(300, 180));
        document.BeginTextEdit(text);

        document.EndTextEdit();

        Assert.Null(document.EditingText);
        Assert.Same(text, document.Selected);
    }

    [Fact]
    public void MovingTheEditedBoxKeepsItOpen()
    {
        var document = NewDocument();
        var text = PlaceText(document, new Point(100, 100), new Point(300, 180));
        document.BeginTextEdit(text);

        Drag(document, new Point(200, 140), new Point(230, 170));

        Assert.Same(text, document.EditingText);
        Assert.Same(text, document.Selected);
    }

    [Fact]
    public void AnAnnotationOutsideTheDocumentCannotBeEdited()
    {
        var document = NewDocument();
        var orphan = new Annotation
        {
            Tool = EditorTool.Text,
            Start = new Point(0, 0),
            End = new Point(100, 40),
        };

        document.BeginTextEdit(orphan);

        Assert.Null(document.EditingText);
    }
}

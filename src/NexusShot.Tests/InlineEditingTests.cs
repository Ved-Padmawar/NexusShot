using DirectN;
using NexusShot.Core;
using NexusShot.Views;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

// These exercise the actual caret/string/controller implementation, rather than just the
// document's "which annotation is being edited" state.
public class InlineEditingTests
{
    [Fact]
    public void EnterKeyAndItsCharacterMessageInsertOnlyOneNewline()
    {
        var (_, controller, _) = Open("a");
        controller.Editor!.MoveTo(1);
        controller.HandleKey(VIRTUAL_KEY.VK_RETURN, false, false);
        controller.HandleChar('\r');
        Assert.Equal("a\n", controller.Editor.Text);
        Assert.Equal(2, controller.Editor.Caret);
    }

    [Fact]
    public void ClearingTextThenKeepingItsBoxDoesNotResurrectOldText()
    {
        var (document, controller, annotation) = Open("old text");
        controller.Editor!.SelectAll();
        controller.Editor.Delete();
        controller.End(commit: true); // preserve the box while moving/resizing it
        Assert.Same(annotation, Assert.Single(document.Annotations));
        Assert.Equal(string.Empty, annotation.Text);
        controller.Begin(annotation);
        Assert.Equal(string.Empty, controller.Editor!.Text);
    }

    [Fact]
    public void HomeOnTheFirstEmptyLineStaysAtTheBeginning()
    {
        var editor = Editor("\nsecond line");
        editor.MoveTo(0);
        editor.MoveToLineEdge(end: false, extend: false);
        Assert.Equal(0, editor.Caret);
    }

    [Fact]
    public void TypingOverASelectionUndoesAsOneRun()
    {
        var editor = Editor("replace me");
        editor.Insert("n");
        editor.Insert("ew");
        Assert.Equal("new", editor.Text);
        editor.Undo();
        Assert.Equal("replace me", editor.Text);
        Assert.Equal("replace me", editor.SelectedText);
        Assert.False(editor.CanUndo);
        editor.Redo();
        Assert.Equal("new", editor.Text);
        Assert.Equal(3, editor.Caret);
        Assert.False(editor.HasSelection);
    }

    [Fact]
    public void MovingTheCaretSeparatesTypingUndoSteps()
    {
        var editor = Editor("");
        editor.Insert("ab");
        editor.MoveTo(0);
        editor.Insert("x");
        editor.Undo();
        Assert.Equal("ab", editor.Text);
        Assert.Equal(0, editor.Caret);
        editor.Undo();
        Assert.Equal("", editor.Text);
    }

    [Fact]
    public void UndoSelectionDeletionRestoresTheSelectionAndCaret()
    {
        var editor = Editor("abcd");
        editor.MoveTo(1);
        editor.MoveTo(3, extend: true);
        editor.Delete();
        Assert.Equal("ad", editor.Text);
        editor.Undo();
        Assert.Equal("abcd", editor.Text);
        Assert.Equal("bc", editor.SelectedText);
        Assert.Equal(3, editor.Caret);
    }

    [Fact]
    public void BackspaceRemovesAnEmojiWithoutLeavingASurrogateHalf()
    {
        var editor = Editor("A😀B");
        editor.MoveTo(3);
        editor.Backspace();
        Assert.Equal("AB", editor.Text);
        Assert.Equal(1, editor.Caret);
        editor.Undo();
        Assert.Equal("A😀B", editor.Text);
    }

    [Fact]
    public void DeleteRemovesAnEmojiWithoutLeavingASurrogateHalf()
    {
        var editor = Editor("A😀B");
        editor.MoveTo(1);
        editor.Delete();
        Assert.Equal("AB", editor.Text);
        Assert.Equal(1, editor.Caret);
        editor.Undo();
        Assert.Equal("A😀B", editor.Text);
    }

    [Fact]
    public void ArrowKeysAndPointerSelectionDoNotSplitAnEmoji()
    {
        var editor = Editor("A😀B");
        editor.MoveTo(1);
        editor.Move(1, extend: true, byWord: false);
        Assert.Equal("😀", editor.SelectedText);
        Assert.Equal(3, editor.Caret);
        editor.Move(-1, extend: false, byWord: false);
        Assert.Equal(1, editor.Caret);
        editor.MoveTo(2);
        Assert.Equal(1, editor.Caret);
    }

    [Fact]
    public void BackspaceRemovesAnEntireCombiningCharacter()
    {
        var editor = Editor("Ae\u0301B");
        editor.MoveTo(3);
        editor.Backspace();
        Assert.Equal("AB", editor.Text);
        editor.Undo();
        Assert.Equal("Ae\u0301B", editor.Text);
        Assert.Equal(3, editor.Caret);
    }

    private static TextEditor Editor(string text) => new(new Annotation { Tool = EditorTool.Text, Text = text });

    private static (EditorDocument Document, TextBoxController Controller, Annotation Annotation) Open(string text)
    {
        var document = NewDocument();
        var annotation = Draw(document, EditorTool.Text, new(100, 100), new(400, 250), thickness: 20);
        document.SetTextContent(annotation, text, annotation.Bounds);
        var controller = new TextBoxController(document);
        controller.Begin(annotation);
        return (document, controller, annotation);
    }
}

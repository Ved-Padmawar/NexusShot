using NexusShot.Core;
using NexusShot.Platform;

namespace NexusShot.Views;

/// <summary>
/// The inline text box: opening it, routing keys into it, and writing it back.
///
/// The document owns which annotation is being edited; this owns the caret, the live string and the
/// box's own undo. That split is what keeps render, input and the cursor reading one answer.
/// </summary>
internal sealed class TextBoxController(EditorDocument document)
{
    private TextEditor? _editor;

    /// <summary>The open editor, or null. Null whenever the document says nothing is being edited,
    /// so a document-side change (undo, delete, a new selection) closes the box with it.</summary>
    public TextEditor? Editor
    {
        get
        {
            if (_editor is not null && !ReferenceEquals(_editor.Annotation, document.EditingText)) _editor = null;
            return _editor;
        }
    }

    public bool IsOpen => Editor is not null;
    public Annotation? Annotation => Editor?.Annotation;

    /// <summary>Opens the box over an annotation, committing whatever was open before.</summary>
    public void Begin(Annotation annotation)
    {
        if (ReferenceEquals(Annotation, annotation)) return;

        End(commit: false);
        document.BeginTextEdit(annotation);
        if (document.EditingText is null) return;
        _editor = new TextEditor(annotation);
    }

    /// <summary>Writes the text back and closes the box. <paramref name="commit"/> keeps an empty
    /// box alive: it is being grabbed to move or resize, and deleting what is under the pointer
    /// mid-gesture is what made text boxes vanish when dragged.</summary>
    public void End(bool commit)
    {
        if (Editor is not { } editor) return;
        _editor = null;
        document.EndTextEdit();

        var annotation = editor.Annotation;
        if (string.IsNullOrWhiteSpace(editor.Text))
        {
            if (!commit) document.CancelAnnotation(annotation);
            return;
        }
        document.SetTextContent(annotation, editor.Text, annotation.Bounds);
    }

    /// <summary>Undo inside the box, if it has anything of its own to unwind.</summary>
    public bool Undo()
    {
        if (Editor is not { CanUndo: true } editor) return false;
        editor.Undo();
        return true;
    }

    public bool Redo()
    {
        if (Editor is not { CanRedo: true } editor) return false;
        editor.Redo();
        return true;
    }

    /// <summary>Drops the box without writing it back, for an undo that is about to remove it.</summary>
    public void Abandon()
    {
        _editor = null;
        document.EndTextEdit();
    }

    /// <summary>A printable character. Control characters are the business of <see cref="HandleKey"/>.</summary>
    public bool HandleChar(char character)
    {
        if (Editor is not { } editor) return false;
        if (char.IsControl(character) && character != '\r') return false;

        editor.Insert(character == '\r' ? "\n" : character.ToString());
        return true;
    }

    /// <summary>
    /// Editing keys. An open box owns the keyboard, so everything is swallowed except Escape, which
    /// closes it, and undo/redo, which the window routes.
    /// </summary>
    public TextKeyResult HandleKey(VIRTUAL_KEY key, bool control, bool shift)
    {
        if (Editor is not { } editor) return TextKeyResult.NotHandled;

        switch (key)
        {
            case VIRTUAL_KEY.VK_BACK:
                editor.Backspace();
                break;
            case VIRTUAL_KEY.VK_DELETE:
                editor.Delete();
                break;
            case VIRTUAL_KEY.VK_LEFT:
                editor.Move(-1, shift, control);
                break;
            case VIRTUAL_KEY.VK_RIGHT:
                editor.Move(1, shift, control);
                break;
            case VIRTUAL_KEY.VK_HOME:
                editor.MoveToLineEdge(end: false, shift);
                break;
            case VIRTUAL_KEY.VK_END:
                editor.MoveToLineEdge(end: true, shift);
                break;

            case VIRTUAL_KEY.VK_A when control:
                editor.SelectAll();
                break;

            case VIRTUAL_KEY.VK_C when control:
                if (editor.HasSelection) ClipboardText.Copy(editor.SelectedText);
                break;

            case VIRTUAL_KEY.VK_X when control:
                if (editor.HasSelection)
                {
                    ClipboardText.Copy(editor.SelectedText);
                    editor.Backspace();
                }
                break;

            case VIRTUAL_KEY.VK_V when control:
                if (ClipboardText.Paste() is { Length: > 0 } pasted) editor.Insert(pasted);
                break;

            // Enter is a newline in a text box, not a crop commit.
            case VIRTUAL_KEY.VK_RETURN:
                editor.Insert("\n");
                break;

            case VIRTUAL_KEY.VK_Z when control:
                return TextKeyResult.Undo;
            case VIRTUAL_KEY.VK_Y when control:
                return TextKeyResult.Redo;

            // Escape closes the box, so it is the one key that goes on to the window.
            case VIRTUAL_KEY.VK_ESCAPE:
                return TextKeyResult.NotHandled;

            // Everything else is swallowed: the printable keys arrive again as WM_CHAR and are
            // inserted there, and letting them through would run the tool shortcuts too.
            default:
                return TextKeyResult.Handled;
        }

        return TextKeyResult.Handled;
    }
}

/// <summary>What the window must do after the box has seen a key.</summary>
internal enum TextKeyResult
{
    NotHandled,
    Handled,
    Undo,
    Redo,
}

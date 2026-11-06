using NexusShot.Core;
using NexusShot.Platform;

namespace NexusShot.Views;

/// <summary>
/// The editing keys every <see cref="TextBuffer"/> answers the same way: the canvas text box and each
/// chrome field route here, so the arrows, Home/End, Delete, word moves and the clipboard cannot work
/// in one and not the other. What is left over - Enter, Escape, Up/Down - means something different
/// per control, and is the caller's.
/// </summary>
internal static class TextKeys
{
    /// <summary>True when the key edited or moved within <paramref name="text"/>. Undo and redo
    /// report false once the buffer has nothing left, so the caller can pass them on.</summary>
    public static bool Apply(TextBuffer text, VIRTUAL_KEY key, bool control, bool shift)
    {
        switch (key)
        {
            case VIRTUAL_KEY.VK_BACK:
                text.Backspace();
                return true;
            case VIRTUAL_KEY.VK_DELETE:
                text.Delete();
                return true;
            case VIRTUAL_KEY.VK_LEFT:
                text.Move(-1, shift, control);
                return true;
            case VIRTUAL_KEY.VK_RIGHT:
                text.Move(1, shift, control);
                return true;
            case VIRTUAL_KEY.VK_HOME:
                text.MoveToLineEdge(end: false, shift);
                return true;
            case VIRTUAL_KEY.VK_END:
                text.MoveToLineEdge(end: true, shift);
                return true;

            case VIRTUAL_KEY.VK_A when control:
                text.SelectAll();
                return true;

            case VIRTUAL_KEY.VK_C when control:
                if (text.HasSelection) Copy(text);
                return true;

            // Only removed once it is safely on the clipboard: a cut that failed must not lose it.
            case VIRTUAL_KEY.VK_X when control:
                if (text.HasSelection && Copy(text)) text.Backspace();
                return true;

            case VIRTUAL_KEY.VK_V when control:
                if (ClipboardText.Paste() is { Length: > 0 } pasted) text.Insert(pasted);
                return true;

            case VIRTUAL_KEY.VK_Z when control && !shift && text.CanUndo:
                text.Undo();
                return true;
            case VIRTUAL_KEY.VK_Z when control && shift && text.CanRedo:
            case VIRTUAL_KEY.VK_Y when control && text.CanRedo:
                text.Redo();
                return true;

            default:
                return false;
        }
    }

    /// <summary>A key for the focused chrome field: its text takes the shared keys, the field the rest.
    /// Every key is swallowed, so a letter typed into a field never reaches a shortcut.</summary>
    public static void Field(Render.Ui ui, VIRTUAL_KEY key, bool control, bool shift)
    {
        if (ui.FocusedText is not { } text || !Apply(text, key, control, shift)) ui.Key(key, shift);
    }

    /// <summary>A busy clipboard fails this one keystroke; it must not take the editor with it.</summary>
    private static bool Copy(TextBuffer text)
    {
        try
        {
            ClipboardText.Copy(text.SelectedText);
            return true;
        }
        catch (InvalidOperationException exception)
        {
            Log.Error("text.copy", exception);
            return false;
        }
    }
}

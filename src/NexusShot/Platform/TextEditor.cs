using NexusShot.Core;

namespace NexusShot.Views;

/// <summary>
/// Inline text entry state: the string, the caret, the selection. The window draws it in the same
/// D2D pass as everything else.
///
/// This was a real Win32 EDIT parked over the canvas, which gets a caret and selection for free. It
/// does not work here: a child HWND and a Direct2D surface have no defined paint order, so the two
/// invalidate each other every frame - the box flickered and its glyphs lagged a keystroke. Drawing
/// the text ourselves is what Paint.NET and Greenshot do, for the same reason.
/// </summary>
internal sealed class TextEditor
{
    public Annotation Annotation { get; }

    /// <summary>The live text, not written back to the annotation until the edit ends.</summary>
    public string Text { get; private set; }

    /// <summary>Caret and selection anchor, as indices into <see cref="Text"/>.</summary>
    public int Caret { get; private set; }
    public int Anchor { get; private set; }

    public int SelectionStart => Math.Min(Anchor, Caret);
    public int SelectionEnd => Math.Max(Anchor, Caret);
    public bool HasSelection => Anchor != Caret;
    public string SelectedText => HasSelection ? Text[SelectionStart..SelectionEnd] : string.Empty;

    public bool CaretVisible => (Environment.TickCount64 - _caretEpoch) % (BlinkMs * 2) < BlinkMs;

    private const long BlinkMs = 530;
    private long _caretEpoch = Environment.TickCount64;

    public TextEditor(Annotation annotation)
    {
        Annotation = annotation;
        Text = annotation.Text;

        // Everything selected, so typing replaces a placeholder.
        Anchor = 0;
        Caret = Text.Length;
    }

    /// <summary>Solid while typing, rather than winking out mid-keystroke.</summary>
    private void Wake() => _caretEpoch = Environment.TickCount64;

    // The box owns its own history: while it is open the text lives here and never reaches the
    // document, so the document's undo stack has nothing of it to restore.

    private readonly Stack<(string Text, int Caret, int Anchor)> _undo = new();
    private readonly Stack<(string Text, int Caret, int Anchor)> _redo = new();

    private EditKind _lastEdit = EditKind.None;

    private enum EditKind { None, Insert, Delete }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Snapshots before a mutation. Consecutive edits of the same kind coalesce, so a run
    /// of typing is one undo entry rather than one per keystroke.</summary>
    private void PushUndo(EditKind kind)
    {
        if (kind != _lastEdit || _undo.Count == 0)
            _undo.Push((Text, Caret, Anchor));

        _lastEdit = kind;
        _redo.Clear();
    }

    /// <summary>Moving the caret ends the run, so the next edit starts a fresh entry.</summary>
    private void BreakRun() => _lastEdit = EditKind.None;

    public void Undo()
    {
        if (!_undo.TryPop(out var previous)) return;

        _redo.Push((Text, Caret, Anchor));
        (Text, Caret, Anchor) = previous;
        BreakRun();
        Wake();
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var next)) return;

        _undo.Push((Text, Caret, Anchor));
        (Text, Caret, Anchor) = next;
        BreakRun();
        Wake();
    }

    public void MoveTo(int index, bool extend = false)
    {
        Caret = Math.Clamp(index, 0, Text.Length);
        if (!extend) Anchor = Caret;
        BreakRun();
        Wake();
    }

    public void SelectAll()
    {
        Anchor = 0;
        Caret = Text.Length;
        BreakRun();
        Wake();
    }

    public void Insert(string text)
    {
        if (text.Length == 0) return;

        PushUndo(EditKind.Insert);
        DeleteSelectionCore();
        Text = Text.Insert(Caret, text);
        Caret += text.Length;
        Anchor = Caret;
        Wake();
    }

    public void Backspace()
    {
        if (!HasSelection && Caret == 0) return;

        PushUndo(EditKind.Delete);
        if (DeleteSelectionCore()) { Wake(); return; }

        Text = Text.Remove(Caret - 1, 1);
        Caret--;
        Anchor = Caret;
        Wake();
    }

    public void Delete()
    {
        if (!HasSelection && Caret >= Text.Length) return;

        PushUndo(EditKind.Delete);
        if (DeleteSelectionCore()) { Wake(); return; }

        Text = Text.Remove(Caret, 1);
        Anchor = Caret;
        Wake();
    }

    /// <summary>Removes the selected span. Takes no snapshot: the caller has already pushed one for
    /// the edit this is part of.</summary>
    private bool DeleteSelectionCore()
    {
        if (!HasSelection) return false;

        var start = SelectionStart;
        Text = Text.Remove(start, SelectionEnd - start);
        Caret = start;
        Anchor = start;
        return true;
    }

    public void Move(int direction, bool extend, bool byWord)
    {
        // A bare arrow collapses a selection to its edge rather than moving from the caret.
        if (HasSelection && !extend)
        {
            MoveTo(direction < 0 ? SelectionStart : SelectionEnd);
            return;
        }

        MoveTo(byWord ? WordBoundary(direction) : Caret + direction, extend);
    }

    private int WordBoundary(int direction)
    {
        var index = Caret;

        if (direction < 0)
        {
            while (index > 0 && char.IsWhiteSpace(Text[index - 1])) index--;
            while (index > 0 && !char.IsWhiteSpace(Text[index - 1])) index--;
            return index;
        }

        while (index < Text.Length && !char.IsWhiteSpace(Text[index])) index++;
        while (index < Text.Length && char.IsWhiteSpace(Text[index])) index++;
        return index;
    }

    /// <summary>Home/End, within the caret's own line - a text annotation can be several.</summary>
    public void MoveToLineEdge(bool end, bool extend)
    {
        var line = end
            ? Text.IndexOf('\n', Caret)
            : Text.LastIndexOf('\n', Math.Max(0, Caret - 1));

        MoveTo(end
            ? line < 0 ? Text.Length : line
            : line < 0 ? 0 : line + 1,
            extend);
    }
}

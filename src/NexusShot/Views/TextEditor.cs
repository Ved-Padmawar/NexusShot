using System.Globalization;
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

    /// <summary>The live text and its formatting, not written back to the annotation until the edit
    /// ends.</summary>
    public string Text { get; private set; }
    public TextStyle Style { get; private set; }
    public TextRun[] Runs { get; private set; }

    /// <summary>Caret and selection anchor, as indices into <see cref="Text"/>.</summary>
    public int Caret { get; private set; }
    private int Anchor { get; set; }

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
        Style = annotation.Style;
        Runs = annotation.Runs;

        // Everything selected, so typing replaces a placeholder.
        Anchor = 0;
        Caret = Text.Length;
    }

    /// <summary>Solid while typing, rather than winking out mid-keystroke.</summary>
    private void Wake() => _caretEpoch = Environment.TickCount64;

    // The box owns its own history: while it is open the text lives here and never reaches the
    // document, so the document's undo stack has nothing of it to restore.

    private readonly Stack<(string Text, TextStyle Style, TextRun[] Runs, int Caret, int Anchor)> _undo = new();
    private readonly Stack<(string Text, TextStyle Style, TextRun[] Runs, int Caret, int Anchor)> _redo = new();

    private EditKind _lastEdit = EditKind.None;

    private enum EditKind { None, Insert, Delete, Format }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Snapshots before a mutation. Consecutive edits of the same kind coalesce, so a run
    /// of typing is one undo entry rather than one per keystroke.</summary>
    private void PushUndo(EditKind kind)
    {
        if (kind != _lastEdit || _undo.Count == 0)
            _undo.Push((Text, Style, Runs, Caret, Anchor));

        _lastEdit = kind;
        _redo.Clear();
    }

    /// <summary>Moving the caret ends the run, so the next edit starts a fresh entry.</summary>
    private void BreakRun() => _lastEdit = EditKind.None;

    public void Undo()
    {
        if (!_undo.TryPop(out var previous)) return;

        _redo.Push((Text, Style, Runs, Caret, Anchor));
        (Text, Style, Runs, Caret, Anchor) = previous;
        BreakRun();
        Wake();
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var next)) return;

        _undo.Push((Text, Style, Runs, Caret, Anchor));
        (Text, Style, Runs, Caret, Anchor) = next;
        BreakRun();
        Wake();
    }

    public void MoveTo(int index, bool extend = false)
    {
        Caret = Math.Clamp(index, 0, Text.Length);
        if (Caret < Text.Length)
        {
            var boundaries = StringInfo.ParseCombiningCharacters(Text);
            var position = Array.BinarySearch(boundaries, Caret);
            if (position < 0) Caret = boundaries[~position - 1];
        }
        if (!extend) Anchor = Caret;
        _goalX = null;
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
        var start = SelectionStart;
        Replace(start, SelectionEnd - start, text);
        Caret = Anchor = start + text.Length;
        Wake();
    }

    public void Backspace()
    {
        if (!HasSelection && Caret == 0) return;

        PushUndo(EditKind.Delete);
        if (DeleteSelectionCore()) { Wake(); return; }

        var previous = TextBoundary(-1);
        Replace(previous, Caret - previous, "");
        Caret = previous;
        Anchor = Caret;
        Wake();
    }

    public void Delete()
    {
        if (!HasSelection && Caret >= Text.Length) return;

        PushUndo(EditKind.Delete);
        if (DeleteSelectionCore()) { Wake(); return; }

        Replace(Caret, TextBoundary(1) - Caret, "");
        Anchor = Caret;
        Wake();
    }

    /// <summary>Removes the selected span. Takes no snapshot: the caller has already pushed one for
    /// the edit this is part of.</summary>
    private bool DeleteSelectionCore()
    {
        if (!HasSelection) return false;

        var start = SelectionStart;
        Replace(start, SelectionEnd - start, "");
        Caret = start;
        Anchor = start;
        return true;
    }

    /// <summary>The one writer of the text, so its runs can never cover a different length.</summary>
    private void Replace(int start, int removed, string inserted)
    {
        (Style, Runs) = TextRuns.Splice(Style, Runs, Text.Length, start, removed, inserted.Length);
        Text = Text.Remove(start, removed).Insert(start, inserted);
        _goalX = null;
    }

    /// <summary>The styles shared by the selection, or by the whole box when nothing is selected - what
    /// the Bold, Italic and Underline buttons show as on.</summary>
    public TextStyle ActiveStyle => HasSelection
        ? TextRuns.Common(Style, Runs, Text.Length, SelectionStart, SelectionEnd)
        : TextRuns.Common(Style, Runs, Text.Length, 0, Text.Length);

    /// <summary>Turns a style on across the selection, or the whole box, unless all of it already has
    /// it - then off. One undo step.</summary>
    public void Toggle(TextStyle flag)
    {
        var (start, end) = HasSelection ? (SelectionStart, SelectionEnd) : (0, Text.Length);
        BreakRun();
        PushUndo(EditKind.Format);
        (Style, Runs) = TextRuns.Apply(Style, Runs, Text.Length, start, end, flag, !ActiveStyle.HasFlag(flag));
        Wake();
    }

    /// <summary>The x Up and Down aim for, kept across a run of them so passing a short line does not
    /// pull the caret left for good. Any other move or edit forgets it.</summary>
    private double? _goalX;

    /// <summary>
    /// Up or Down one visual line, wrapped lines included. <paramref name="caretAt"/> and
    /// <paramref name="indexAt"/> are the renderer's layout, so this lands where the text is drawn.
    /// Past the first or last line, the caret goes to that end of the text.
    /// </summary>
    public void MoveLine(int direction, bool extend, Func<int, Rect> caretAt, Func<Point, int> indexAt)
    {
        var caret = caretAt(Caret);
        var goal = _goalX ?? caret.X;
        var target = indexAt(new Point(goal, direction < 0 ? caret.Top - caret.Height / 2 : caret.Bottom + caret.Height / 2));
        if (target == Caret) target = direction < 0 ? 0 : Text.Length;
        MoveTo(target, extend);
        _goalX = goal;
    }

    public void Move(int direction, bool extend, bool byWord)
    {
        // A bare arrow collapses a selection to its edge rather than moving from the caret.
        if (HasSelection && !extend)
        {
            MoveTo(direction < 0 ? SelectionStart : SelectionEnd);
            return;
        }

        MoveTo(byWord ? WordBoundary(direction) : TextBoundary(direction), extend);
    }

    private int TextBoundary(int direction)
    {
        var boundaries = StringInfo.ParseCombiningCharacters(Text);
        var position = Array.BinarySearch(boundaries, Caret);
        var insertion = position < 0 ? ~position : position;
        var target = direction < 0 ? insertion - 1 : position < 0 ? insertion : position + 1;
        return target < 0 ? 0 : target >= boundaries.Length ? Text.Length : boundaries[target];
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
            : Caret == 0 ? -1 : Text.LastIndexOf('\n', Caret - 1);

        MoveTo(end
            ? line < 0 ? Text.Length : line
            : line < 0 ? 0 : line + 1,
            extend);
    }
}

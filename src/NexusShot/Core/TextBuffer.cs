using System.Globalization;

namespace NexusShot.Core;

/// <summary>
/// Editable text: the string, its formatting, the caret, the selection and its own undo. The one
/// editing model behind every place the user types - the canvas text box and every chrome field -
/// so a key does the same thing wherever the caret is.
///
/// <paramref name="accept"/> and <paramref name="maxLength"/> are enforced by <see cref="Insert"/>
/// itself, so typing and pasting cannot disagree about what a field takes.
/// </summary>
public class TextBuffer(string text, TextFormat format = default, TextRun[]? runs = null,
    Func<char, bool>? accept = null, int maxLength = int.MaxValue)
{
    public string Text { get; private set; } = text;
    public TextFormat Format { get; private set; } = format;
    public TextRun[] Runs { get; private set; } = runs ?? [];

    /// <summary>Caret and selection anchor, as indices into <see cref="Text"/>. Everything starts
    /// selected, so the first keystroke replaces what was there.</summary>
    public int Caret { get; private set; } = text.Length;
    private int Anchor { get; set; }

    public int SelectionStart => Math.Min(Anchor, Caret);
    public int SelectionEnd => Math.Max(Anchor, Caret);
    public bool HasSelection => Anchor != Caret;
    public string SelectedText => HasSelection ? Text[SelectionStart..SelectionEnd] : string.Empty;

    public bool CaretVisible => (Environment.TickCount64 - _caretEpoch) % (BlinkMs * 2) < BlinkMs;

    private const long BlinkMs = 530;
    private long _caretEpoch = Environment.TickCount64;

    /// <summary>Solid while typing, rather than winking out mid-keystroke.</summary>
    private void Wake() => _caretEpoch = Environment.TickCount64;

    private readonly Stack<(string Text, TextFormat Format, TextRun[] Runs, int Caret, int Anchor)> _undo = new();
    private readonly Stack<(string Text, TextFormat Format, TextRun[] Runs, int Caret, int Anchor)> _redo = new();

    private EditKind _lastEdit = EditKind.None;

    private enum EditKind { None, Insert, Delete, Style, Resize }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Snapshots before a mutation. Consecutive edits of the same kind coalesce, so a run
    /// of typing is one undo entry rather than one per keystroke.</summary>
    private void PushUndo(EditKind kind)
    {
        if (kind != _lastEdit || _undo.Count == 0)
            _undo.Push((Text, Format, Runs, Caret, Anchor));

        _lastEdit = kind;
        _redo.Clear();
    }

    /// <summary>Moving the caret ends the run, so the next edit starts a fresh entry.</summary>
    private void BreakRun() => _lastEdit = EditKind.None;

    public void Undo()
    {
        if (!_undo.TryPop(out var previous)) return;

        _redo.Push((Text, Format, Runs, Caret, Anchor));
        (Text, Format, Runs, Caret, Anchor) = previous;
        BreakRun();
        Wake();
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var next)) return;

        _undo.Push((Text, Format, Runs, Caret, Anchor));
        (Text, Format, Runs, Caret, Anchor) = next;
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

    /// <summary>Replaces the selection with <paramref name="text"/>, less any characters the buffer
    /// refuses and whatever would run past its maximum length. Nothing left, nothing changes - a
    /// refused key must not delete the selection it would have replaced.</summary>
    public void Insert(string text)
    {
        if (accept is not null) text = string.Concat(text.Where(accept));
        var room = maxLength - (Text.Length - (SelectionEnd - SelectionStart));
        if (text.Length > room)
        {
            text = text[..Math.Max(0, room)];
            if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text = text[..^1];
        }
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
        (Format, Runs) = TextRuns.Splice(Format, Runs, Text.Length, start, removed, inserted.Length);
        Text = Text.Remove(start, removed).Insert(start, inserted);
        _goalX = null;
    }

    /// <summary>What formatting applies to: the selection, or all of the text when nothing is
    /// selected.</summary>
    private (int Start, int End) FormatRange => HasSelection ? (SelectionStart, SelectionEnd) : (0, Text.Length);

    /// <summary>The styles shared by the formatting range - what the Bold, Italic and Underline
    /// buttons show as on.</summary>
    public TextStyle ActiveStyle
    {
        get
        {
            var (start, end) = FormatRange;
            return TextRuns.Common(Format, Runs, Text.Length, start, end);
        }
    }

    /// <summary>The size the size control shows: where the formatting range begins.</summary>
    public double ActiveSize
    {
        get
        {
            var (start, end) = FormatRange;
            return TextRuns.SizeAt(Format, Runs, Text.Length, start, end);
        }
    }

    /// <summary>Turns a style on across the formatting range, unless all of it already has it - then
    /// off. One undo step.</summary>
    public void Toggle(TextStyle flag)
    {
        var (start, end) = FormatRange;
        BreakRun();
        PushUndo(EditKind.Style);
        (Format, Runs) = TextRuns.Apply(Format, Runs, Text.Length, start, end, flag, !ActiveStyle.HasFlag(flag));
        Wake();
    }

    /// <summary>Sets the font size across the formatting range. Consecutive resizes coalesce, so a
    /// slider drag is one undo step.</summary>
    public void Resize(double size)
    {
        var (start, end) = FormatRange;
        PushUndo(EditKind.Resize);
        (Format, Runs) = TextRuns.Resize(Format, Runs, Text.Length, start, end, size);
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

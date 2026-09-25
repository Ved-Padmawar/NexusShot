namespace NexusShot.Core;

[Flags]
public enum TextStyle { None = 0, Bold = 1, Italic = 2, Underline = 4 }

/// <summary><paramref name="Length"/> characters in one style.</summary>
public readonly record struct TextRun(int Length, TextStyle Style);

/// <summary>
/// Formatting for part of a text box. A box has a base style; its runs are empty while every
/// character shares that style, and otherwise cover the text exactly. Every function returns that
/// canonical form, so two boxes that look the same compare the same.
/// </summary>
public static class TextRuns
{
    /// <summary>The flags every character in [start, end) carries. An empty range reports the style
    /// typing there would get.</summary>
    public static TextStyle Common(TextStyle style, TextRun[] runs, int length, int start, int end)
    {
        var characters = Expand(style, runs, length);
        if (start >= end) return TypingStyle(style, characters, start);

        var common = characters[start];
        for (var i = start + 1; i < end; i++) common &= characters[i];
        return common;
    }

    /// <summary>Sets or clears <paramref name="flag"/> over [start, end). Covering the whole text
    /// changes the base too, so an emptied box keeps what was chosen for it.</summary>
    public static (TextStyle Style, TextRun[] Runs) Apply(
        TextStyle style, TextRun[] runs, int length, int start, int end, TextStyle flag, bool on)
    {
        var characters = Expand(style, runs, length);
        for (var i = start; i < end; i++) characters[i] = on ? characters[i] | flag : characters[i] & ~flag;
        if (start == 0 && end == length) style = on ? style | flag : style & ~flag;
        return Compress(style, characters);
    }

    /// <summary>Replaces <paramref name="removed"/> characters at <paramref name="start"/> with
    /// <paramref name="inserted"/> new ones, which take the style of what they replace or follow.</summary>
    public static (TextStyle Style, TextRun[] Runs) Splice(
        TextStyle style, TextRun[] runs, int length, int start, int removed, int inserted)
    {
        var characters = Expand(style, runs, length);
        var typed = removed > 0 ? characters[start] : TypingStyle(style, characters, start);
        var result = new TextStyle[length - removed + inserted];
        Array.Copy(characters, result, start);
        Array.Fill(result, typed, start, inserted);
        Array.Copy(characters, start + removed, result, start + inserted, length - start - removed);
        return Compress(style, result);
    }

    /// <summary>The text as (start, length, style) spans, for the renderer.</summary>
    public static IEnumerable<(int Start, int Length, TextStyle Style)> Spans(TextStyle style, TextRun[] runs, int length)
    {
        if (runs.Length == 0)
        {
            if (length > 0) yield return (0, length, style);
            yield break;
        }

        var start = 0;
        foreach (var run in runs)
        {
            yield return (start, run.Length, run.Style);
            start += run.Length;
        }
    }

    /// <summary>New text continues the character before it; at the very start, the one after it.</summary>
    private static TextStyle TypingStyle(TextStyle style, TextStyle[] characters, int at) =>
        characters.Length == 0 ? style : characters[Math.Clamp(at - 1, 0, characters.Length - 1)];

    private static TextStyle[] Expand(TextStyle style, TextRun[] runs, int length)
    {
        var characters = new TextStyle[length];
        if (runs.Length == 0)
        {
            Array.Fill(characters, style);
            return characters;
        }

        var at = 0;
        foreach (var run in runs)
        {
            Array.Fill(characters, run.Style, at, run.Length);
            at += run.Length;
        }
        return characters;
    }

    private static (TextStyle Style, TextRun[] Runs) Compress(TextStyle style, TextStyle[] characters)
    {
        if (characters.Length == 0) return (style, []);
        if (Array.TrueForAll(characters, character => character == characters[0])) return (characters[0], []);

        var runs = new List<TextRun>();
        var start = 0;
        for (var i = 1; i <= characters.Length; i++)
        {
            if (i < characters.Length && characters[i] == characters[start]) continue;
            runs.Add(new TextRun(i - start, characters[start]));
            start = i;
        }
        return (style, [.. runs]);
    }
}

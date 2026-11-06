namespace NexusShot.Core;

[Flags]
public enum TextStyle { None = 0, Bold = 1, Italic = 2, Underline = 4 }

/// <summary>How one character is drawn: its style flags and its font size, in image pixels.</summary>
public readonly record struct TextFormat(TextStyle Style, double Size);

/// <summary><paramref name="Length"/> characters in one format.</summary>
public readonly record struct TextRun(int Length, TextFormat Format);

/// <summary>
/// Formatting for part of a text box. A box has a base format; its runs are empty while every
/// character shares that format, and otherwise cover the text exactly. Every function returns that
/// canonical form, so two boxes that look the same compare the same.
/// </summary>
public static class TextRuns
{
    /// <summary>The flags every character in [start, end) carries. An empty range reports the style
    /// typing there would get.</summary>
    public static TextStyle Common(TextFormat format, TextRun[] runs, int length, int start, int end)
    {
        var characters = Expand(format, runs, length);
        if (start >= end) return TypingFormat(format, characters, start).Style;

        var common = characters[start].Style;
        for (var i = start + 1; i < end; i++) common &= characters[i].Style;
        return common;
    }

    /// <summary>The size the size control shows for [start, end): its first character's, or for an
    /// empty range the size typing there would get.</summary>
    public static double SizeAt(TextFormat format, TextRun[] runs, int length, int start, int end)
    {
        var characters = Expand(format, runs, length);
        return start >= end ? TypingFormat(format, characters, start).Size : characters[start].Size;
    }

    /// <summary>The largest size anywhere in the text - what the box must at least fit.</summary>
    public static double Largest(TextFormat format, TextRun[] runs) =>
        runs.Length == 0 ? format.Size : runs.Max(run => run.Format.Size);

    public static (TextFormat Format, TextRun[] Runs) Apply(
        TextFormat format, TextRun[] runs, int length, int start, int end, TextStyle flag, bool on) =>
        Change(format, runs, length, start, end,
            character => character with { Style = on ? character.Style | flag : character.Style & ~flag });

    public static (TextFormat Format, TextRun[] Runs) Resize(
        TextFormat format, TextRun[] runs, int length, int start, int end, double size) =>
        Change(format, runs, length, start, end, character => character with { Size = size });

    /// <summary>Covering the whole text changes the base too, so an emptied box keeps what was chosen
    /// for it.</summary>
    private static (TextFormat Format, TextRun[] Runs) Change(
        TextFormat format, TextRun[] runs, int length, int start, int end, Func<TextFormat, TextFormat> change)
    {
        var characters = Expand(format, runs, length);
        for (var i = start; i < end; i++) characters[i] = change(characters[i]);
        if (start == 0 && end == length) format = change(format);
        return Compress(format, characters);
    }

    /// <summary>Replaces <paramref name="removed"/> characters at <paramref name="start"/> with
    /// <paramref name="inserted"/> new ones, which take the format of what they replace or follow.</summary>
    public static (TextFormat Format, TextRun[] Runs) Splice(
        TextFormat format, TextRun[] runs, int length, int start, int removed, int inserted)
    {
        var characters = Expand(format, runs, length);
        var typed = removed > 0 ? characters[start] : TypingFormat(format, characters, start);
        var result = new TextFormat[length - removed + inserted];
        Array.Copy(characters, result, start);
        Array.Fill(result, typed, start, inserted);
        Array.Copy(characters, start + removed, result, start + inserted, length - start - removed);
        return Compress(format, result);
    }

    /// <summary>The text as (start, length, format) spans, for the renderer.</summary>
    public static IEnumerable<(int Start, int Length, TextFormat Format)> Spans(TextFormat format, TextRun[] runs, int length)
    {
        if (runs.Length == 0)
        {
            if (length > 0) yield return (0, length, format);
            yield break;
        }

        var start = 0;
        foreach (var run in runs)
        {
            yield return (start, run.Length, run.Format);
            start += run.Length;
        }
    }

    /// <summary>New text continues the character before it; at the very start, the one after it.</summary>
    private static TextFormat TypingFormat(TextFormat format, TextFormat[] characters, int at) =>
        characters.Length == 0 ? format : characters[Math.Clamp(at - 1, 0, characters.Length - 1)];

    private static TextFormat[] Expand(TextFormat format, TextRun[] runs, int length)
    {
        var characters = new TextFormat[length];
        if (runs.Length == 0)
        {
            Array.Fill(characters, format);
            return characters;
        }

        var at = 0;
        foreach (var run in runs)
        {
            Array.Fill(characters, run.Format, at, run.Length);
            at += run.Length;
        }
        return characters;
    }

    private static (TextFormat Format, TextRun[] Runs) Compress(TextFormat format, TextFormat[] characters)
    {
        if (characters.Length == 0) return (format, []);
        if (Array.TrueForAll(characters, character => character == characters[0])) return (characters[0], []);

        var runs = new List<TextRun>();
        var start = 0;
        for (var i = 1; i <= characters.Length; i++)
        {
            if (i < characters.Length && characters[i] == characters[start]) continue;
            runs.Add(new TextRun(i - start, characters[start]));
            start = i;
        }
        return (format, [.. runs]);
    }
}

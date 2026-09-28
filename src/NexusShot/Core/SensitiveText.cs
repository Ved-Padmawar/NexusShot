using System.Text.RegularExpressions;

namespace NexusShot.Core;

/// <summary>A recognised word and where it sits, in image pixels.</summary>
public sealed record TextWord(string Text, Rect Bounds);

/// <summary>
/// Finds what is usually secret in a screenshot's text - email addresses, card numbers, phone numbers,
/// IP addresses and long keys or tokens - and the areas that cover them. A suggestion, not a promise:
/// recognition misses things, so the user still looks before sharing.
/// </summary>
public static partial class SensitiveText
{
    /// <summary>Covers <paramref name="lines"/>' matches, each padded by <paramref name="padding"/>
    /// pixels so antialiased glyph edges do not show around the block.</summary>
    public static List<Rect> Find(IReadOnlyList<IReadOnlyList<TextWord>> lines, double padding = 2)
    {
        var areas = new List<Rect>();
        foreach (var words in lines)
        {
            if (words.Count == 0) continue;

            // Word starts are kept so a match across words - a grouped card number - covers them all.
            var starts = new int[words.Count];
            var line = new System.Text.StringBuilder();
            for (var i = 0; i < words.Count; i++)
            {
                if (i > 0) line.Append(' ');
                starts[i] = line.Length;
                line.Append(words[i].Text);
            }

            foreach (Match match in Pattern().Matches(line.ToString()))
            {
                if (!IsSensitive(match)) continue;
                Rect? cover = null;
                for (var i = 0; i < words.Count; i++)
                {
                    var end = starts[i] + words[i].Text.Length;
                    if (end <= match.Index || starts[i] >= match.Index + match.Length) continue;
                    var bounds = words[i].Bounds;
                    cover = cover is { } soFar
                        ? Rect.FromEdges(Math.Min(soFar.X, bounds.X), Math.Min(soFar.Y, bounds.Y),
                            Math.Max(soFar.Right, bounds.Right), Math.Max(soFar.Bottom, bounds.Bottom))
                        : bounds;
                }
                if (cover is { } area)
                    areas.Add(new Rect(area.X - padding, area.Y - padding, area.Width + padding * 2, area.Height + padding * 2));
            }
        }
        return areas;
    }

    private static bool IsSensitive(Match match)
    {
        if (match.Groups["card"].Success) return PassesLuhn(match.Value);
        // A bare run of digits is an order or ticket number far more often than a phone number.
        if (match.Groups["phone"].Success)
            return match.Value.Count(char.IsDigit) is >= 9 and <= 15 && (match.Value[0] == '+' || match.Value.Any(" -()".Contains));
        if (match.Groups["key"].Success) return match.Value.Any(char.IsDigit) && match.Value.Any(char.IsLetter);
        return true;
    }

    /// <summary>The card-number checksum: without it any long run of digits - an order number, a
    /// timestamp - would be blacked out.</summary>
    private static bool PassesLuhn(string text)
    {
        var digits = text.Where(char.IsDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19) return false;
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var digit = digits[^(i + 1)];
            if (i % 2 == 1) digit = digit * 2 > 9 ? digit * 2 - 9 : digit * 2;
            sum += digit;
        }
        return sum % 10 == 0;
    }

    /// <summary>Alternatives in priority order: a card number is tried before the phone number it
    /// would also look like.</summary>
    [GeneratedRegex("""
        (?<email>[\w.+-]+@[\w-]+(?:\.[\w-]+)+)
        |(?<ip>\b(?:\d{1,3}\.){3}\d{1,3}\b)
        |(?<card>\b\d(?:[ -]?\d){12,18}\b)
        |(?<phone>(?<!\w)\+?\(?\d[\d ()-]{7,}\d\b)
        |(?<key>\b[A-Za-z0-9_-]{24,}\b)
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

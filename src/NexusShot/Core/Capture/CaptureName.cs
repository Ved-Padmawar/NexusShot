using System.Globalization;

namespace NexusShot.Core;

/// <summary>
/// The capture filename format, owned in one place because it is written and read back.
///
/// The name carries the capture time, which is the only record of it that survives the file moving:
/// a folder copied, restored from a backup, or synced to another machine gets its creation time
/// rewritten to when it arrived, so a history rebuilt from the file system alone would reorder
/// itself. The name travels with the bytes.
/// </summary>
public static class CaptureName
{
    private const string Prefix = "NexusShot ";
    private const string Stamp = "yyyy-MM-dd HH.mm.ss";

    /// <summary>Between the stamp and an app's name. A dash no stamp contains, so reading back is exact.</summary>
    private const string AppSeparator = " - ";

    /// <summary>The base name for a capture taken at <paramref name="when"/>, without an extension,
    /// optionally naming the <paramref name="app"/> it was taken from. A collision is resolved by the
    /// caller appending a counter, which <see cref="TryParseTime"/> tolerates.</summary>
    public static string For(DateTime when, string? app = null)
    {
        var name = Prefix + when.ToString(Stamp, CultureInfo.InvariantCulture);
        return Clean(app) is { Length: > 0 } label ? name + AppSeparator + label : name;
    }

    /// <summary>An app's name fit for a file name: characters Windows refuses dropped, and cut short,
    /// since a window's product name can run long.</summary>
    private static string? Clean(string? app)
    {
        if (app is null) return null;
        var invalid = Path.GetInvalidFileNameChars();
        var kept = new string([.. app.Where(character => !invalid.Contains(character))]).Trim().TrimEnd('.');
        return kept.Length > 40 ? kept[..40].TrimEnd() : kept;
    }

    /// <summary>
    /// Reads the capture time back out of a file name, ignoring any extension, app name and `_001`
    /// counter a collision added.
    ///
    /// False for a file the app did not name - a screenshot dropped into the folder by another
    /// tool - which leaves the caller to fall back to the file system.
    /// </summary>
    public static bool TryParseTime(string fileName, out DateTime captured)
    {
        captured = default;

        var name = Path.GetFileNameWithoutExtension(fileName);
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || name.Length < Prefix.Length + Stamp.Length) return false;

        var stamp = name.Substring(Prefix.Length, Stamp.Length);
        var rest = name[(Prefix.Length + Stamp.Length)..];

        // After the stamp only an app's name and a `_001` counter are ours.
        var underscore = rest.LastIndexOf('_');
        if (underscore >= 0 && rest.Length > underscore + 1 && rest[(underscore + 1)..].All(char.IsAsciiDigit))
            rest = rest[..underscore];
        if (rest.Length > 0 && !(rest.StartsWith(AppSeparator, StringComparison.Ordinal) && rest.Length > AppSeparator.Length))
            return false;

        return DateTime.TryParseExact(
            stamp, Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out captured);
    }
}

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

    /// <summary>The base name for a capture taken at <paramref name="when"/>, without an extension.
    /// A collision is resolved by the caller appending a counter, which <see cref="TryParseTime"/>
    /// tolerates.</summary>
    public static string For(DateTime when) =>
        Prefix + when.ToString(Stamp, CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads the capture time back out of a file name, ignoring any extension and any `_001`
    /// counter a collision added.
    ///
    /// False for a file the app did not name - a screenshot dropped into the folder by another
    /// tool - which leaves the caller to fall back to the file system.
    /// </summary>
    public static bool TryParseTime(string fileName, out DateTime captured)
    {
        captured = default;

        var name = Path.GetFileNameWithoutExtension(fileName);
        if (!name.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        var stamp = name[Prefix.Length..];

        // A collision suffix is `_` plus digits; anything else after the stamp is a different name
        // that happens to share the prefix.
        var underscore = stamp.IndexOf('_');
        if (underscore >= 0)
        {
            foreach (var character in stamp[(underscore + 1)..])
                if (!char.IsAsciiDigit(character)) return false;

            stamp = stamp[..underscore];
        }

        return DateTime.TryParseExact(
            stamp, Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out captured);
    }
}

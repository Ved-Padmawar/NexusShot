using System.Globalization;

namespace NexusShot.Core;

/// <summary>A file size as people read it: "840 KB", "1.2 GB". Decimal units, as Explorer's
/// neighbours on the web and in cloud storage count them.</summary>
public static class ByteSize
{
    public static string Format(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        var format = unit == 0 || value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {units[unit]}";
    }
}

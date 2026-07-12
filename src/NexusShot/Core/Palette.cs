namespace NexusShot.Core;

/// <summary>
/// Colour parsing and the app's fixed swatches. Kept framework-free so the renderer, the
/// exporter and the chrome all agree on what a hex string means.
/// </summary>
public static class Palette
{
    /// <summary>The annotation swatches, in toolbar order.</summary>
    public static readonly string[] Swatches =
        ["#FF3B30", "#FFCC00", "#34C759", "#0A84FF", "#FFFFFF", "#1C1C1E"];

    public static readonly int[] FontSizes = [12, 14, 16, 20, 24, 28, 32, 40, 48, 64];

    /// <summary>Selection blue, shared by adorners, the crop frame and focus rings.</summary>
    public static readonly Rgba Selection = new(10, 132, 255, 255);

    public static Rgba Parse(string hex)
    {
        var value = hex.AsSpan().TrimStart('#');
        if (value.Length == 6
            && int.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return new Rgba((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF), 255);
        return new Rgba(255, 59, 48, 255);
    }

    /// <summary>Perceptual lightness test, used to pick readable text over a filled badge.</summary>
    public static bool IsLight(Rgba color) =>
        color.R * 0.299 + color.G * 0.587 + color.B * 0.114 > 150;
}

/// <summary>A straight (non-premultiplied) 8-bit colour.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba White => new(255, 255, 255);
    public static Rgba Black => new(0, 0, 0);

    public Rgba WithAlpha(byte alpha) => this with { A = alpha };

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}

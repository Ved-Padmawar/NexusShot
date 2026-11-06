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

    /// <summary>What a malformed hex string resolves to. Named, so the fallback is a deliberate
    /// value rather than whichever swatch happens to be first.</summary>
    public static readonly Rgba Fallback = new(255, 59, 48, 255);

    /// <summary>Accepts <c>#RRGGBB</c> and <c>#RRGGBBAA</c>, with or without the hash. Alpha is the
    /// trailing pair, as CSS writes it, so a colour copied out of a design tool pastes as-is.</summary>
    public static bool TryParse(string? hex, out Rgba color)
    {
        color = Fallback;
        if (hex is null) return false;

        var value = hex.AsSpan().Trim().TrimStart('#');
        if (value.Length is not (6 or 8)) return false;

        const System.Globalization.NumberStyles style = System.Globalization.NumberStyles.HexNumber;
        if (!byte.TryParse(value[..2], style, null, out var r)
            || !byte.TryParse(value[2..4], style, null, out var g)
            || !byte.TryParse(value[4..6], style, null, out var b))
            return false;

        byte a = 255;
        if (value.Length == 8 && !byte.TryParse(value[6..8], style, null, out a)) return false;

        color = new Rgba(r, g, b, a);
        return true;
    }

    /// <summary>
    /// Parses a hex colour, falling back rather than throwing: this is on the paint path, and a bad
    /// string in settings must not take the editor down mid-frame.
    ///
    /// Reported once per distinct string - the same annotation repaints every frame, so logging
    /// unconditionally would fill the log at the display rate.
    /// </summary>
    public static Rgba Parse(string hex)
    {
        if (TryParse(hex, out var color)) return color;

        lock (ReportedBadHex)
            if (ReportedBadHex.Add(hex ?? "<null>")) Log.Info("palette.bad-hex", hex);

        return Fallback;
    }

    private static readonly HashSet<string> ReportedBadHex = [];

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

    /// <summary><c>#RRGGBB</c>, or <c>#RRGGBBAA</c> when the colour is not opaque - so an opaque
    /// colour keeps the six-digit form every swatch and older settings file uses.</summary>
    public string ToHex() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    /// <summary>Moves toward <paramref name="other"/> by <paramref name="amount"/> (0..1), in straight
    /// RGB and keeping this colour's alpha - how a hover tone is derived from an accent.</summary>
    public Rgba Mix(Rgba other, double amount) => new(
        Lerp(R, other.R, amount), Lerp(G, other.G, amount), Lerp(B, other.B, amount), A);

    /// <summary>Composites a translucent colour over this one, so a tint can lift an opaque surface
    /// rather than replace it.</summary>
    public Rgba Under(Rgba tint) => Mix(tint with { A = A }, tint.A / 255.0);

    private static byte Lerp(byte from, byte to, double amount) =>
        (byte)Math.Round(from + (to - from) * Math.Clamp(amount, 0, 1));
}

/// <summary>
/// A colour as hue, saturation, value and alpha: the picker's source of truth. Black and white carry
/// no hue, so a picker that stored RGB would lose the rail position the moment it touched a corner.
/// Every other model the picker shows is a view onto this one value.
/// </summary>
public readonly record struct Hsva(double Hue, double Saturation, double Value, double Alpha = 1)
{
    public Rgba ToRgba()
    {
        var hue = ((Hue % 360) + 360) % 360;
        var c = Value * Saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = Value - c;

        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return new Rgba(Byte(r + m), Byte(g + m), Byte(b + m), Byte(Alpha));
    }

    /// <summary>Converts an RGB colour, keeping <paramref name="keepHue"/> when the colour is a grey
    /// that has none of its own - otherwise the rail snaps to red on every black or white.</summary>
    public static Hsva From(Rgba color, double keepHue = 0)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var delta = max - Math.Min(r, Math.Min(g, b));

        var hue = keepHue;
        if (delta > 0.0001)
        {
            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
            hue = ((hue % 360) + 360) % 360;
        }

        var saturation = max <= 0.0001 ? 0 : delta / max;
        return new Hsva(hue, saturation, max, color.A / 255.0);
    }

    /// <summary>HSL saturation and lightness for the same colour. The hue is shared.</summary>
    public (double Saturation, double Lightness) ToHsl()
    {
        var lightness = Value * (1 - Saturation / 2);
        var saturation = lightness is <= 0 or >= 1
            ? 0
            : (Value - lightness) / Math.Min(lightness, 1 - lightness);
        return (saturation, lightness);
    }

    public static Hsva FromHsl(double hue, double saturation, double lightness, double alpha)
    {
        var value = lightness + saturation * Math.Min(lightness, 1 - lightness);
        var hsvSaturation = value <= 0 ? 0 : 2 * (1 - lightness / value);
        return new Hsva(hue, hsvSaturation, value, alpha);
    }

    private static byte Byte(double unit) => (byte)Math.Round(Math.Clamp(unit, 0, 1) * 255);
}

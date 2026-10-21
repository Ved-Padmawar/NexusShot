namespace NexusShot.Core;

/// <summary>
/// Brings an HDR frame down to the 8-bit sRGB an image file holds.
///
/// A frame from an HDR display is linear scRGB, where 1.0 is 80 nits and SDR content sits at the
/// user's SDR white level. Clipping at 1.0 flattens everything brighter than the interface to pure
/// white. Instead the colour is scaled so SDR white is 1.0, left alone up to a knee, and rolled off
/// above it by one factor for all three channels, which keeps the hue. The cost is that SDR white
/// lands just under 255, which is what leaves room for the highlights.
/// </summary>
public static class HdrToneMap
{
    private const float ScRgbWhiteNits = 80f;

    /// <summary>Where the roll-off starts, as a share of SDR white.</summary>
    private const float Knee = 0.85f;

    /// <summary>A guard against infinities from a driver: far brighter than any display.</summary>
    private const float MaximumLinear = 64f;

    private const int EncodeSteps = 4096;

    private static readonly byte[] Encoded = BuildEncodeTable();

    /// <summary>
    /// One row of premultiplied RGBA half-float pixels to premultiplied BGRA bytes. The two spans must
    /// hold the same number of pixels. The colour is mapped straight, not premultiplied: the roll-off is
    /// not linear, so mapping a premultiplied colour would leave it brighter than its own alpha, and a
    /// translucent edge would glow.
    /// </summary>
    public static void ConvertRow(ReadOnlySpan<Half> scRgb, Span<byte> bgra, float sdrWhiteNits)
    {
        var gain = ScRgbWhiteNits / sdrWhiteNits;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var alpha = Math.Min((float)scRgb[i + 3], 1f);
            if (!(alpha > 0f))
            {
                bgra.Slice(i, 4).Clear();
                continue;
            }

            var straightGain = gain / alpha;
            var red = Linear(scRgb[i], straightGain);
            var green = Linear(scRgb[i + 1], straightGain);
            var blue = Linear(scRgb[i + 2], straightGain);

            var peak = Math.Max(red, Math.Max(green, blue));
            var scale = peak > Knee ? Rolloff(peak) / peak : 1f;

            bgra[i] = Premultiply(Encode(blue * scale), alpha);
            bgra[i + 1] = Premultiply(Encode(green * scale), alpha);
            bgra[i + 2] = Premultiply(Encode(red * scale), alpha);
            bgra[i + 3] = (byte)(alpha * 255f + 0.5f);
        }
    }

    /// <summary>Negative (outside sRGB) and non-numeric values count as black.</summary>
    private static float Linear(Half value, float gain)
    {
        var scaled = (float)value * gain;
        return scaled > 0f ? Math.Min(scaled, MaximumLinear) : 0f;
    }

    /// <summary>Identity up to the knee, then approaches 1 without reaching it; the slope is 1 at the
    /// knee, so there is no visible edge where the curve starts.</summary>
    private static float Rolloff(float value)
    {
        var excess = (value - Knee) / (1f - Knee);
        return Knee + (1f - Knee) * excess / (1f + excess);
    }

    private static byte Premultiply(byte colour, float alpha) => (byte)(colour * alpha + 0.5f);

    private static byte Encode(float linear) =>
        Encoded[(int)(Math.Min(linear, 1f) * EncodeSteps + 0.5f)];

    private static byte[] BuildEncodeTable()
    {
        var table = new byte[EncodeSteps + 1];
        for (var i = 0; i <= EncodeSteps; i++)
        {
            var linear = (double)i / EncodeSteps;
            var encoded = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
            table[i] = (byte)(encoded * 255 + 0.5);
        }
        return table;
    }
}

namespace NexusShot.Core;

/// <summary>
/// A box-filter shrink, for the library's blurred tile previews: blur is wanted at that size, and it
/// runs on pixels the worker already has, with no second decode.
/// </summary>
public static class Downsample
{
    public static int FactorFor(int width, int target) => Math.Max(1, width / Math.Max(1, target));

    /// <summary>Tightly packed pixels; a partial edge block folds into the last output pixel.</summary>
    public static byte[] Box(ReadOnlySpan<byte> pixels, int width, int height, int factor, out int outWidth, out int outHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        outWidth = Math.Max(1, width / factor);
        outHeight = Math.Max(1, height / factor);
        var result = new byte[outWidth * outHeight * 4];

        for (var oy = 0; oy < outHeight; oy++)
        {
            var top = oy * factor;
            var bottom = oy == outHeight - 1 ? height : top + factor;
            for (var ox = 0; ox < outWidth; ox++)
            {
                var left = ox * factor;
                var right = ox == outWidth - 1 ? width : left + factor;
                int b = 0, g = 0, r = 0, a = 0;
                for (var y = top; y < bottom; y++)
                {
                    var row = pixels.Slice((y * width + left) * 4, (right - left) * 4);
                    for (var i = 0; i < row.Length; i += 4)
                    {
                        b += row[i];
                        g += row[i + 1];
                        r += row[i + 2];
                        a += row[i + 3];
                    }
                }

                var count = (bottom - top) * (right - left);
                var at = (oy * outWidth + ox) * 4;
                result[at] = (byte)(b / count);
                result[at + 1] = (byte)(g / count);
                result[at + 2] = (byte)(r / count);
                result[at + 3] = (byte)(a / count);
            }
        }
        return result;
    }
}

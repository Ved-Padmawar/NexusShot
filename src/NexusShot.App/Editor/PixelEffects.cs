using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace NexusShot.App.Editor;

/// <summary>
/// Redaction effects over raw BGRA32 pixel buffers. Shared by the export flattener and the
/// editor's live preview, so the pixels the user sees while editing are the pixels the export
/// produces. Alpha is left untouched throughout.
/// </summary>
public static class PixelEffects
{
    public const int PixelateBlockSize = 12;
    public const int BlurRadius = 6;

    /// <summary>
    /// The pixels a painted redaction stroke produces: the effect applied to the stroke's
    /// bounding region, masked to the brush path so only painted pixels are affected.
    /// Returns premultiplied BGRA sized to the region, or null for an empty stroke.
    /// The mask is rasterised by GDI+ with round caps and joins, so strokes read as one
    /// continuous brush mark rather than a chain of stamps.
    /// </summary>
    public static (Rectangle Region, byte[] PremultipliedBgra)? BrushStroke(
        byte[] pixels, int stride, IReadOnlyList<PointF> path, float radius, bool pixelate)
    {
        if (path.Count == 0) return null;
        var width = stride / 4;
        var height = pixels.Length / stride;

        var minX = path.Min(p => p.X);
        var minY = path.Min(p => p.Y);
        var maxX = path.Max(p => p.X);
        var maxY = path.Max(p => p.Y);

        // The blur kernel samples beyond the stroke, so pad the region enough that the
        // painted pixels never sample uninitialised block edges.
        var margin = (int)MathF.Ceiling(radius) + (pixelate ? 0 : BlurRadius);
        var region = Rectangle.Intersect(
            Rectangle.FromLTRB((int)minX - margin, (int)minY - margin, (int)MathF.Ceiling(maxX) + margin, (int)MathF.Ceiling(maxY) + margin),
            new Rectangle(0, 0, width, height));
        if (region.Width <= 0 || region.Height <= 0) return null;

        var blockStride = region.Width * 4;
        var block = new byte[blockStride * region.Height];
        for (var row = 0; row < region.Height; row++)
        {
            Buffer.BlockCopy(
                pixels, (region.Top + row) * stride + region.Left * 4,
                block, row * blockStride, blockStride);
        }

        var full = new Rectangle(0, 0, region.Width, region.Height);
        if (pixelate) Pixelate(block, blockStride, full);
        else BoxBlur(block, blockStride, full);

        ApplyStrokeMask(block, blockStride, region, path, radius);
        return (region, block);
    }

    /// <summary>Turns the effect block into a stroke-shaped cut-out: alpha follows the
    /// rasterised brush path, and colours are premultiplied to match.</summary>
    private static void ApplyStrokeMask(byte[] block, int blockStride, Rectangle region, IReadOnlyList<PointF> path, float radius)
    {
        using var mask = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(mask))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var local = path.Select(p => new PointF(p.X - region.X, p.Y - region.Y)).ToArray();
            if (local.Length == 1)
            {
                using var dot = new SolidBrush(Color.White);
                graphics.FillEllipse(dot, local[0].X - radius, local[0].Y - radius, radius * 2, radius * 2);
            }
            else
            {
                using var pen = new Pen(Color.White, radius * 2)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round,
                };
                graphics.DrawLines(pen, local);
            }
        }

        var data = mask.LockBits(new Rectangle(0, 0, region.Width, region.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var maskBytes = new byte[data.Stride * region.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, maskBytes, 0, maskBytes.Length);
            for (var y = 0; y < region.Height; y++)
            {
                var maskRow = y * data.Stride;
                var blockRow = y * blockStride;
                for (var x = 0; x < region.Width; x++)
                {
                    var alpha = maskBytes[maskRow + x * 4 + 3];
                    var offset = blockRow + x * 4;
                    block[offset] = (byte)(block[offset] * alpha / 255);
                    block[offset + 1] = (byte)(block[offset + 1] * alpha / 255);
                    block[offset + 2] = (byte)(block[offset + 2] * alpha / 255);
                    block[offset + 3] = alpha;
                }
            }
        }
        finally
        {
            mask.UnlockBits(data);
        }
    }

    /// <summary>Replaces each block in <paramref name="region"/> with its average colour.
    /// The block grid is anchored at the region's top-left corner.</summary>
    public static void Pixelate(byte[] pixels, int stride, Rectangle region)
    {
        for (var y = region.Top; y < region.Bottom; y += PixelateBlockSize)
        {
            for (var x = region.Left; x < region.Right; x += PixelateBlockSize)
            {
                var blockWidth = Math.Min(PixelateBlockSize, region.Right - x);
                var blockHeight = Math.Min(PixelateBlockSize, region.Bottom - y);

                long b = 0, g = 0, r = 0;
                for (var by = y; by < y + blockHeight; by++)
                {
                    for (var bx = x; bx < x + blockWidth; bx++)
                    {
                        var offset = by * stride + bx * 4;
                        b += pixels[offset];
                        g += pixels[offset + 1];
                        r += pixels[offset + 2];
                    }
                }

                var count = blockWidth * blockHeight;
                byte averageB = (byte)(b / count), averageG = (byte)(g / count), averageR = (byte)(r / count);
                for (var by = y; by < y + blockHeight; by++)
                {
                    for (var bx = x; bx < x + blockWidth; bx++)
                    {
                        var offset = by * stride + bx * 4;
                        pixels[offset] = averageB;
                        pixels[offset + 1] = averageG;
                        pixels[offset + 2] = averageR;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Box blur over <paramref name="region"/>, sampling from the surrounding pixels so the
    /// blurred area does not read as a hard-edged tile.
    ///
    /// Separable: a horizontal pass into a scratch band, then a vertical pass back into the
    /// buffer. A clamped box average factors exactly into per-axis averages, so this matches the
    /// naive 2D kernel while doing O(radius) work per pixel instead of O(radius²).
    /// </summary>
    public static void BoxBlur(byte[] pixels, int stride, Rectangle region, int radius = BlurRadius)
    {
        var width = stride / 4;
        var height = pixels.Length / stride;
        region = Rectangle.Intersect(region, new Rectangle(0, 0, width, height));
        if (region.Width <= 0 || region.Height <= 0) return;

        // The vertical pass reads horizontally blurred rows up to `radius` outside the region.
        var bandTop = Math.Max(0, region.Top - radius);
        var bandBottom = Math.Min(height, region.Bottom + radius);
        var band = new byte[(bandBottom - bandTop) * stride];

        for (var y = bandTop; y < bandBottom; y++)
        {
            var sourceRow = y * stride;
            var bandRow = (y - bandTop) * stride;
            for (var x = region.Left; x < region.Right; x++)
            {
                var minX = Math.Max(0, x - radius);
                var maxX = Math.Min(width - 1, x + radius);

                long b = 0, g = 0, r = 0;
                for (var sx = minX; sx <= maxX; sx++)
                {
                    var offset = sourceRow + sx * 4;
                    b += pixels[offset];
                    g += pixels[offset + 1];
                    r += pixels[offset + 2];
                }

                var samples = maxX - minX + 1;
                var target = bandRow + x * 4;
                band[target] = (byte)(b / samples);
                band[target + 1] = (byte)(g / samples);
                band[target + 2] = (byte)(r / samples);
            }
        }

        for (var y = region.Top; y < region.Bottom; y++)
        {
            var minY = Math.Max(bandTop, y - radius);
            var maxY = Math.Min(bandBottom - 1, y + radius);
            var targetRow = y * stride;
            for (var x = region.Left; x < region.Right; x++)
            {
                long b = 0, g = 0, r = 0;
                for (var sy = minY; sy <= maxY; sy++)
                {
                    var offset = (sy - bandTop) * stride + x * 4;
                    b += band[offset];
                    g += band[offset + 1];
                    r += band[offset + 2];
                }

                var samples = maxY - minY + 1;
                var target = targetRow + x * 4;
                pixels[target] = (byte)(b / samples);
                pixels[target + 1] = (byte)(g / samples);
                pixels[target + 2] = (byte)(r / samples);
            }
        }
    }
}

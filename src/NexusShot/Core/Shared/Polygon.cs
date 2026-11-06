namespace NexusShot.Core;

/// <summary>Scanline filling of a closed outline, for cutting a freeform lasso out of a capture.</summary>
public static class Polygon
{
    /// <summary>
    /// The pixels inside <paramref name="outline"/> as runs [Start, End) per row, for a
    /// <paramref name="width"/> x <paramref name="height"/> image. A pixel is inside when its centre is,
    /// by the even-odd rule, so a lasso that crosses itself leaves its overlaps out as the eye expects.
    /// </summary>
    public static IEnumerable<(int Y, int Start, int End)> InsideRuns(IReadOnlyList<Point> outline, int width, int height)
    {
        if (outline.Count < 3) yield break;
        var crossings = new List<double>();
        for (var y = 0; y < height; y++)
        {
            var centre = y + 0.5;
            crossings.Clear();
            for (var i = 0; i < outline.Count; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % outline.Count];
                if (a.Y <= centre == b.Y <= centre) continue;
                crossings.Add(a.X + (centre - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
            crossings.Sort();

            for (var k = 0; k + 1 < crossings.Count; k += 2)
            {
                var start = Math.Clamp((int)Math.Ceiling(crossings[k] - 0.5), 0, width);
                var end = Math.Clamp((int)Math.Ceiling(crossings[k + 1] - 0.5), 0, width);
                if (end > start) yield return (y, start, end);
            }
        }
    }
}

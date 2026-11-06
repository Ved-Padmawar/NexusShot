namespace NexusShot.Core;

/// <summary>Where two same-sized frames of 32-bit pixels differ, so a GIF stores only what changed.</summary>
public static class FrameDiff
{
    /// <summary>The smallest rectangle holding every changed pixel, or null when the frames match.</summary>
    public static (int X, int Y, int Width, int Height)? ChangedBounds(ReadOnlySpan<uint> before, ReadOnlySpan<uint> after, int width)
    {
        var height = after.Length / width;
        var top = 0;
        while (top < height && Row(before, top, width).SequenceEqual(Row(after, top, width))) top++;
        if (top == height) return null;

        var bottom = height - 1;
        while (Row(before, bottom, width).SequenceEqual(Row(after, bottom, width))) bottom--;

        var left = width;
        var right = 0;
        for (var y = top; y <= bottom; y++)
        {
            var old = Row(before, y, width);
            var now = Row(after, y, width);
            var prefix = old.CommonPrefixLength(now);
            if (prefix == width) continue;
            var last = width - 1;
            while (old[last] == now[last]) last--;
            left = Math.Min(left, prefix);
            right = Math.Max(right, last);
        }
        return (left, top, right - left + 1, bottom - top + 1);
    }

    private static ReadOnlySpan<uint> Row(ReadOnlySpan<uint> frame, int y, int width) => frame.Slice(y * width, width);
}

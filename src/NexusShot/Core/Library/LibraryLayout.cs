namespace NexusShot.Core;

/// <summary>
/// The library grid's geometry: how many columns fit, how wide a tile is, and where each group and
/// tile sits. Pure arithmetic in design units times the display scale, so input, drawing and the
/// scroll extent all read one answer.
/// </summary>
public sealed record LibraryLayout(double Width, double Scale)
{
    /// <summary>A tile is never narrower than this; spare width is shared out across a row.</summary>
    public double MinTile => 196 * Scale;
    public double ColumnGap => 14 * Scale;
    public double RowGap => 16 * Scale;
    public double HeaderHeight => 18 * Scale + 16 * Scale + 10 * Scale;
    public double CaptionHeight => 8 * Scale + 18 * Scale;

    public int Columns => Math.Max(1, (int)Math.Floor((Width + ColumnGap) / (MinTile + ColumnGap)));
    public double TileWidth => (Width - ColumnGap * (Columns - 1)) / Columns;

    /// <summary>The image's height: captures are shown at 16:10, cropped from the top.</summary>
    public double ImageHeight => TileWidth * 10 / 16;
    public double TileHeight => ImageHeight + CaptionHeight;

    /// <summary>The width a thumbnail is decoded at: the tile's, rounded up to a 64-pixel step so
    /// dragging the window's edge does not re-decode the grid at every pixel.</summary>
    public int DecodeWidth => (int)Math.Ceiling(TileWidth / 64) * 64;

    /// <summary>How many thumbnails to keep: the tiles on screen and one screen either side, which is
    /// what the grid preloads. Anything smaller evicts tiles that are about to scroll back in.</summary>
    public int CacheCapacity(double viewportHeight) =>
        ((int)Math.Ceiling(viewportHeight * 3 / (TileHeight + RowGap)) + 2) * Columns;

    /// <summary>The height of a group of <paramref name="count"/> tiles, header included.</summary>
    public double GroupHeight(int count)
    {
        var rows = (count + Columns - 1) / Columns;
        return HeaderHeight + rows * TileHeight + Math.Max(0, rows - 1) * RowGap;
    }

    /// <summary>A tile's rectangle within its group, from the group's top-left.</summary>
    public Rect Tile(double groupX, double groupY, int index)
    {
        var column = index % Columns;
        var row = index / Columns;
        return new Rect(
            groupX + column * (TileWidth + ColumnGap),
            groupY + HeaderHeight + row * (TileHeight + RowGap),
            TileWidth, TileHeight);
    }
}

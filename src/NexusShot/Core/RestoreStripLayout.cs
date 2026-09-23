namespace NexusShot.Core;

/// <summary>The strip's tiles, the History button, and the message shown when nothing is closed.</summary>
public readonly record struct StripArrangement(IReadOnlyList<Rect> Tiles, Rect History, Rect EmptyMessage);

/// <summary>
/// The restore strip, in design units: a band across the screen with as many closed captures as fit,
/// newest first, then History, centred as a group. It never scrolls, and tiles are the cards' size.
/// </summary>
public static class RestoreStripLayout
{
    public const double TileWidth = CardLayout.Width;
    public const double TileHeight = CardLayout.Height;
    public const double TileRadius = 10;
    public const double Gap = 12;
    public const double Padding = 16;
    public const double HistoryWidth = 76;
    public const double ButtonHeight = 26;
    public const double PillWidth = 84;
    public const double PillHeight = 22;
    public const double EmptyMessageWidth = 150;

    /// <summary>Tiles centred with equal room above and below; the padding also holds the half of
    /// the Restore pill that hangs below a tile.</summary>
    public const double Height = Padding + TileHeight + Padding;

    public static StripArrangement Arrange(int closed, double width)
    {
        var fit = (int)Math.Max(0, Math.Floor((width - Padding * 2 - HistoryWidth) / (TileWidth + Gap)));
        var count = Math.Min(closed, fit);

        var leading = count > 0 ? count * (TileWidth + Gap) : EmptyMessageWidth + Gap;
        var x = (width - leading - HistoryWidth) / 2;

        var tiles = new Rect[count];
        for (var i = 0; i < count; i++)
            tiles[i] = new Rect(x + i * (TileWidth + Gap), Padding, TileWidth, TileHeight);

        var message = new Rect(x, Padding, EmptyMessageWidth, TileHeight);
        var history = new Rect(x + leading, Padding + (TileHeight - ButtonHeight) / 2, HistoryWidth, ButtonHeight);
        return new StripArrangement(tiles, history, message);
    }

    /// <summary>The Restore pill, centred on a hovered tile's bottom edge.</summary>
    public static Rect RestorePill(Rect tile) =>
        new(tile.Center.X - PillWidth / 2, tile.Bottom - PillHeight / 2, PillWidth, PillHeight);
}

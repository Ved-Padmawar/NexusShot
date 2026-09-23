using NexusShot.Core;

namespace NexusShot.Tests;

public class RestoreStripLayoutTests
{
    [Fact]
    public void ShowsOnlyWhatFitsAndNeverDropsTheHistoryButton()
    {
        var strip = RestoreStripLayout.Arrange(closed: 20, width: 800);

        Assert.InRange(strip.Tiles.Count, 1, 19);
        Assert.True(strip.History.Left >= strip.Tiles[^1].Right);
        Assert.True(strip.History.Right <= 800);
    }

    [Fact]
    public void NeverShowsMoreTilesThanThereAreCaptures()
    {
        Assert.Equal(3, RestoreStripLayout.Arrange(closed: 3, width: 5000).Tiles.Count);
    }

    [Fact]
    public void ContentIsCentredInTheBand()
    {
        var strip = RestoreStripLayout.Arrange(closed: 3, width: 2000);

        var left = strip.Tiles[0].Left;
        var right = 2000 - strip.History.Right;
        Assert.Equal(left, right, 3);
    }

    [Fact]
    public void TilesDoNotOverlap()
    {
        var tiles = RestoreStripLayout.Arrange(closed: 10, width: 5000).Tiles;
        for (var i = 1; i < tiles.Count; i++)
            Assert.True(tiles[i].Left >= tiles[i - 1].Right);
    }

    [Fact]
    public void TilesAreTheCardsSize()
    {
        var tile = RestoreStripLayout.Arrange(closed: 1, width: 800).Tiles[0];
        Assert.Equal(CardLayout.Width, tile.Width);
        Assert.Equal(CardLayout.Height, tile.Height);
    }

    [Fact]
    public void RestorePillStraddlesTheBottomEdgeOfItsTile()
    {
        var tile = RestoreStripLayout.Arrange(closed: 2, width: 800).Tiles[1];
        var pill = RestoreStripLayout.RestorePill(tile);

        Assert.Equal(tile.Center.X, pill.Center.X);
        Assert.Equal(tile.Bottom, pill.Center.Y);
    }

    [Fact]
    public void RestorePillStaysInsideTheStrip()
    {
        var tile = RestoreStripLayout.Arrange(closed: 1, width: 800).Tiles[0];
        Assert.True(RestoreStripLayout.RestorePill(tile).Bottom < RestoreStripLayout.Height);
    }

    [Fact]
    public void TilesAreVerticallyCentred()
    {
        var tile = RestoreStripLayout.Arrange(closed: 1, width: 800).Tiles[0];
        Assert.Equal(tile.Top, RestoreStripLayout.Height - tile.Bottom);
    }

    [Fact]
    public void RestorePillNeverOverhangsItsTile()
    {
        var tile = RestoreStripLayout.Arrange(closed: 1, width: 800).Tiles[0];
        var pill = RestoreStripLayout.RestorePill(tile);

        Assert.True(pill.Left >= tile.Left && pill.Right <= tile.Right);
    }

    [Fact]
    public void HistoryButtonIsAButtonAlignedWithTheTiles()
    {
        var strip = RestoreStripLayout.Arrange(closed: 2, width: 800);

        Assert.Equal(RestoreStripLayout.ButtonHeight, strip.History.Height);
        Assert.Equal(strip.Tiles[0].Center.Y, strip.History.Center.Y);
    }

    [Fact]
    public void EmptyListShowsTheMessageThenHistory()
    {
        var strip = RestoreStripLayout.Arrange(closed: 0, width: 800);

        Assert.Empty(strip.Tiles);
        Assert.True(strip.History.Left >= strip.EmptyMessage.Right);
    }
}

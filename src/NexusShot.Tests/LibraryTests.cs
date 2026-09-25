using System.Globalization;
using NexusShot.Core;

namespace NexusShot.Tests;

/// <summary>The library's day groups, tile captions and grid extent, and the scroll and zoom state
/// the Library and the editor keep.</summary>
public class LibraryTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 17, 0, 0);   // a Friday

    [Theory]
    [InlineData(0, "Today")]
    [InlineData(1, "Yesterday")]
    [InlineData(6, "This week")]
    [InlineData(7, "September 2026")]
    [InlineData(-1, "Today")]           // a clock that ran ahead is not a group of its own
    public void GroupTitlesFollowCalendarDays(int daysAgo, string title)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { Assert.Equal(title, LibraryGroups.Title(Now.Date.AddDays(-daysAgo).AddHours(12), Now)); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(0, 0, 30, "Just now")]
    [InlineData(0, 0, 5 * 60, "5 min ago")]
    [InlineData(0, 3, 0, "3 h ago")]
    public void RecentCaptionsCountTime(int days, int hours, int seconds, string caption) =>
        Assert.Equal(caption, Caption(Now - new TimeSpan(days, hours, 0, seconds)));

    [Fact]
    public void OlderCaptionsNameTheDay()
    {
        Assert.Equal("Yesterday 18:42", Caption(new DateTime(2026, 9, 24, 18, 42, 0)));
        Assert.Equal("Mon 09:12", Caption(new DateTime(2026, 9, 21, 9, 12, 0)));
        Assert.Equal("3 Aug", Caption(new DateTime(2026, 8, 3, 9, 12, 0)));
    }

    [Fact]
    public void MinutesAcrossMidnightAreStillMinutes() =>
        Assert.Equal("6 min ago", LibraryGroups.Ago(new DateTime(2026, 9, 24, 23, 59, 0), new DateTime(2026, 9, 25, 0, 5, 0)));

    private static string Caption(DateTime captured)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return LibraryGroups.Ago(captured, Now); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void AnEmptySearchResultHasNoGroups()
    {
        List<ScreenshotHistoryItem> history = [new() { FilePath = @"C:\s\a.png", CapturedAt = Now }];
        Assert.Empty(LibraryGroups.Build(history, "zzz", Now));
    }

    [Fact]
    public void AGroupIsTallEnoughForEveryRowItHolds()
    {
        var layout = new LibraryLayout(868, 1);
        var columns = layout.Columns;

        Assert.Equal(layout.HeaderHeight + layout.TileHeight, layout.GroupHeight(1));
        Assert.Equal(layout.GroupHeight(1), layout.GroupHeight(columns));
        Assert.Equal(layout.GroupHeight(columns) + layout.TileHeight + layout.RowGap, layout.GroupHeight(columns + 1));

        var last = layout.Tile(0, 0, columns * 3 - 1);
        Assert.Equal(layout.GroupHeight(columns * 3), last.Bottom, 6);
    }

    [Fact]
    public void ANarrowWindowStillHasOneColumn()
    {
        var layout = new LibraryLayout(50, 1);
        Assert.Equal(1, layout.Columns);
        Assert.Equal(50, layout.TileWidth);
    }

    [Fact]
    public void TilesFillTheRowEdgeToEdge()
    {
        var layout = new LibraryLayout(1000, 1.25);
        Assert.Equal(1000, layout.Tile(0, 0, layout.Columns - 1).Right, 6);
    }

    // ---- scrolling ----

    [Fact]
    public void AWheelPastEitherEndStopsThere()
    {
        var scroll = new ScrollState();
        scroll.SetRange(300);

        scroll.Wheel(100);
        Assert.Equal(0, scroll.Target);
        scroll.Wheel(-1000);
        Assert.Equal(300, scroll.Target);
    }

    [Fact]
    public void ANegativeRangeMeansNothingToScroll()
    {
        var scroll = new ScrollState();
        scroll.SetRange(-50);
        scroll.Pan(-100);
        Assert.Equal((0.0, 0.0, 0.0), (scroll.Max, scroll.Position, scroll.Target));
    }

    [Fact]
    public void ResetReturnsToTheTopAtOnce()
    {
        var scroll = new ScrollState();
        scroll.SetRange(1000);
        scroll.Pan(-400);
        scroll.Reset();
        Assert.Equal((0.0, 0.0), (scroll.Position, scroll.Target));
        Assert.False(scroll.Step(16));
    }

    // ---- editor zoom ----

    private static readonly Rect Well = new(0, 0, 800, 600);

    [Fact]
    public void AnImageThatFitsIsCentredHoweverItIsPanned()
    {
        var viewport = new Viewport();
        var image = new Size(400, 300);
        viewport.PanBy(500, -500);
        Assert.Equal(new Rect(200, 150, 400, 300), viewport.Place(Well, image));
    }

    [Fact]
    public void FitForgetsTheZoomAndThePan()
    {
        var viewport = new Viewport();
        var image = new Size(1600, 1200);
        viewport.Actual(Well, image, new Point(10, 10));
        viewport.PanBy(100, 100);

        viewport.Fit();

        Assert.True(viewport.IsFit);
        Assert.Equal(new Rect(0, 0, 800, 600), viewport.Place(Well, image));
    }

    [Fact]
    public void ActualSizeShowsOneImagePixelPerScreenPixel()
    {
        var viewport = new Viewport();
        var image = new Size(1600, 1200);
        viewport.Actual(Well, image, Well.Center);

        Assert.Equal(1, viewport.Scale(Well, image));
        Assert.Equal(new Rect(-400, -300, 1600, 1200), viewport.Place(Well, image));
    }

    [Fact]
    public void ZoomingOutStopsAtTheMinimum()
    {
        var viewport = new Viewport();
        for (var i = 0; i < 50; i++) viewport.ZoomBy(1 / Viewport.Step, Well, new Size(400, 300), Well.Center);
        Assert.Equal(Viewport.MinZoom, viewport.Zoom);
    }
}

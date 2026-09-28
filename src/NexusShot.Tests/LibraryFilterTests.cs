using NexusShot.Core;

namespace NexusShot.Tests;

/// <summary>What the Library shows and keeps: search over names and read text, favourites, periods,
/// the age limit, and the text index behind the search.</summary>
public class LibraryFilterTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 17, 0, 0);

    private static ScreenshotHistoryItem Item(string name, double daysAgo, bool favorite = false) => new()
    {
        FilePath = $@"C:\shots\{name}",
        CapturedAt = new DateTimeOffset(Now.AddDays(-daysAgo)),
        Favorite = favorite,
    };

    [Fact]
    public void TheSearchFindsACaptureByTheTextInIt()
    {
        var invoice = Item("a.png", 0);
        var chat = Item("b.png", 0);
        var texts = new Dictionary<string, string> { [invoice.FilePath] = "Invoice total 42.00" };

        var filter = new LibraryFilter("TOTAL");

        Assert.True(filter.Shows(invoice, Now, path => texts.GetValueOrDefault(path)));
        Assert.False(filter.Shows(chat, Now, path => texts.GetValueOrDefault(path)));
        Assert.True(new LibraryFilter("b.PNG").Shows(chat, Now));
    }

    [Fact]
    public void FavoritesOnlyHidesTheRest()
    {
        var filter = new LibraryFilter(FavoritesOnly: true);
        Assert.True(filter.Shows(Item("a.png", 0, favorite: true), Now));
        Assert.False(filter.Shows(Item("b.png", 0), Now));
    }

    [Theory]
    [InlineData(LibraryPeriod.Today, 0.2, true)]
    [InlineData(LibraryPeriod.Today, 1, false)]
    [InlineData(LibraryPeriod.Week, 6, true)]
    [InlineData(LibraryPeriod.Week, 7, false)]
    [InlineData(LibraryPeriod.Month, 29, true)]
    [InlineData(LibraryPeriod.Month, 31, false)]
    [InlineData(LibraryPeriod.All, 900, true)]
    public void APeriodCountsCalendarDaysBack(LibraryPeriod period, double daysAgo, bool shown) =>
        Assert.Equal(shown, new LibraryFilter(Period: period).Shows(Item("a.png", daysAgo), Now));

    [Fact]
    public void RetentionTakesOnlyOldCapturesThatAreNotFavorites()
    {
        List<ScreenshotHistoryItem> history = [Item("new.png", 5), Item("old.png", 40), Item("kept.png", 400, favorite: true)];

        Assert.Equal(["old.png"], Retention.Expired(history, 30, new DateTimeOffset(Now)).Select(item => item.FileName));
        Assert.Empty(Retention.Expired(history, 0, new DateTimeOffset(Now)));
    }

    [Fact]
    public void AnIndexEntryIsStaleOnceTheFileChanges()
    {
        var index = new TextIndex();
        var first = new FileVersion(10, new DateTime(2026, 1, 1));
        index.Set(@"C:\shots\a.png", first, "hello");

        Assert.True(index.IsCurrent(@"C:\SHOTS\A.PNG", first));
        Assert.False(index.IsCurrent(@"C:\shots\a.png", first with { Length = 11 }));
        Assert.Equal("hello", index.TextOf(@"C:\shots\a.png"));

        var generation = index.Generation;
        index.Retain([]);
        Assert.Null(index.TextOf(@"C:\shots\a.png"));
        Assert.Equal(generation, index.Generation);
    }

    [Theory]
    [InlineData(512, "512 bytes")]
    [InlineData(1_500, "1.5 KB")]
    [InlineData(84_000_000, "84 MB")]
    [InlineData(1_234_000_000, "1.23 GB")]
    public void SizesReadTheWayExplorerShowsThem(long bytes, string text) => Assert.Equal(text, ByteSize.Format(bytes));
}

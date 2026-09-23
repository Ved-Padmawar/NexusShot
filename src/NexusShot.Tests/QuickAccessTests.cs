using NexusShot.Core;

namespace NexusShot.Tests;

public class QuickAccessTests
{
    private static ScreenshotHistoryItem Capture(string name) =>
        new() { FilePath = $@"C:\captures\{name}.png", CapturedAt = DateTimeOffset.Now };

    [Fact]
    public void NewestCardIsFirst()
    {
        var stack = new QuickAccess(new AppSettings());
        stack.Show(Capture("a"));
        stack.Show(Capture("b"));

        Assert.Equal(["b", "a"], stack.Open.Select(card => Path.GetFileNameWithoutExtension(card.Item.FilePath)));
    }

    [Fact]
    public void ShowingAnOpenCaptureUpdatesItInsteadOfStackingASecondCard()
    {
        var stack = new QuickAccess(new AppSettings());
        var first = stack.Show(Capture("a"));
        var resaved = Capture("A");
        resaved.Width = 640;

        Assert.Same(first, stack.Show(resaved));
        Assert.Single(stack.Open);
        Assert.Equal(640, first.Item.Width);
    }

    [Fact]
    public void ClosedCardsCanBeRestoredNewestFirst()
    {
        var stack = new QuickAccess(new AppSettings());
        var a = stack.Show(Capture("a"));
        var b = stack.Show(Capture("b"));

        stack.Close(a);
        stack.Close(b);
        Assert.Empty(stack.Open);
        Assert.Equal([b.Item, a.Item], stack.RecentlyClosed);

        stack.Show(a.Item);
        Assert.Single(stack.Open);
        Assert.Equal([b.Item], stack.RecentlyClosed);
    }

    [Fact]
    public void ClosingTwiceDoesNotListTheCaptureTwice()
    {
        var stack = new QuickAccess(new AppSettings());
        var card = stack.Show(Capture("a"));
        stack.Close(card);
        stack.Close(card);
        stack.Close(stack.Show(Capture("a")));

        Assert.Single(stack.RecentlyClosed);
    }

    [Fact]
    public void RestoreListIsBounded()
    {
        var stack = new QuickAccess(new AppSettings());
        for (var i = 0; i < QuickAccess.ClosedCapacity + 5; i++) stack.Close(stack.Show(Capture($"c{i}")));

        Assert.Equal(QuickAccess.ClosedCapacity, stack.RecentlyClosed.Count);
        Assert.EndsWith($"c{QuickAccess.ClosedCapacity + 4}.png", stack.RecentlyClosed[0].FilePath);
    }

    [Fact]
    public void ForgottenCapturesLeaveTheRestoreList()
    {
        var stack = new QuickAccess(new AppSettings());
        var card = stack.Show(Capture("a"));
        stack.Close(card);

        stack.Forget(card.Item.FilePath.ToUpperInvariant());
        Assert.Empty(stack.RecentlyClosed);
    }

    [Fact]
    public void CountdownClosesTheCardWhenItRunsOut()
    {
        var stack = new QuickAccess(new AppSettings { PreviewDismissSeconds = 2 });
        var card = stack.Show(Capture("a"));

        Assert.False(stack.Tick(card, held: false));
        Assert.True(stack.Tick(card, held: false));
    }

    [Fact]
    public void HoldingOrPinningRestartsTheCountdown()
    {
        var stack = new QuickAccess(new AppSettings { PreviewDismissSeconds = 2 });
        var card = stack.Show(Capture("a"));

        Assert.False(stack.Tick(card, held: false));
        // A drag or a hover mid-countdown must not leave the card one tick from closing.
        Assert.False(stack.Tick(card, held: true));
        Assert.False(stack.Tick(card, held: false));
        Assert.True(stack.Tick(card, held: false));

        stack.TogglePin(card);
        for (var i = 0; i < 10; i++) Assert.False(stack.Tick(card, held: false));
    }

    [Fact]
    public void ZeroSecondsKeepsCardsUntilActedOnAndAppliesToCardsAlreadyUp()
    {
        var settings = new AppSettings { PreviewDismissSeconds = 0 };
        var stack = new QuickAccess(settings);
        var card = stack.Show(Capture("a"));

        for (var i = 0; i < 10; i++) Assert.False(stack.Tick(card, held: false));

        settings.PreviewDismissSeconds = 1;
        stack.ResetCountdown(card);
        Assert.True(stack.Tick(card, held: false));
    }
}

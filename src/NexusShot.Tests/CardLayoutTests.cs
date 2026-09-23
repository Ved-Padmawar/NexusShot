using NexusShot.Core;

namespace NexusShot.Tests;

public class CardLayoutTests
{
    [Fact]
    public void ButtonsStayInsideTheCardAndNeverOverlap()
    {
        var buttons = CardLayout.Actions.Select(CardLayout.Button).ToArray();

        foreach (var button in buttons)
        {
            Assert.True(button.Left >= 0 && button.Top >= 0);
            Assert.True(button.Right <= CardLayout.Width && button.Bottom <= CardLayout.Height);
        }

        for (var i = 0; i < buttons.Length; i++)
            for (var j = i + 1; j < buttons.Length; j++)
                Assert.False(Overlaps(buttons[i], buttons[j]), $"{CardLayout.Actions[i]} overlaps {CardLayout.Actions[j]}");
    }

    [Fact]
    public void EverythingBesideTheCentrePillsDrags()
    {
        var pills = CardLayout.Button(CardAction.Copy);
        var top = CardLayout.Inset + CardLayout.ButtonSize;
        var bottom = CardLayout.Height - top;

        // Both side bands, the full height between the corner buttons.
        for (var y = top + 1; y < bottom - 1; y += 2)
        {
            for (var x = 0.0; x < pills.Left; x += 4)
                Assert.Null(CardLayout.ButtonAt(new Point(x, y)));
            for (var x = pills.Right + 1; x <= CardLayout.Width; x += 4)
                Assert.Null(CardLayout.ButtonAt(new Point(x, y)));
        }
    }

    [Fact]
    public void CopyPillsSitStackedInTheCentre()
    {
        var copy = CardLayout.Button(CardAction.Copy);
        var text = CardLayout.Button(CardAction.CopyText);

        Assert.Equal(CardLayout.Width / 2, copy.Center.X);
        Assert.Equal(CardLayout.Width / 2, text.Center.X);
        Assert.True(copy.Bottom < text.Top);
        Assert.Equal(CardLayout.Height / 2, (copy.Top + text.Bottom) / 2);
    }

    [Fact]
    public void CornersHoldThePlannedActions()
    {
        Assert.Equal(CardAction.Pin, CardLayout.ButtonAt(new Point(10, 10)));
        Assert.Equal(CardAction.Close, CardLayout.ButtonAt(new Point(CardLayout.Width - 10, 10)));
        Assert.Equal(CardAction.Edit, CardLayout.ButtonAt(new Point(10, CardLayout.Height - 10)));
        Assert.Equal(CardAction.SaveAs, CardLayout.ButtonAt(new Point(CardLayout.Width - 10, CardLayout.Height - 10)));
    }

    [Fact]
    public void EveryButtonIsFoundAtItsOwnCentre()
    {
        foreach (var action in CardLayout.Actions)
            Assert.Equal(action, CardLayout.ButtonAt(CardLayout.Button(action).Center));
    }

    [Theory]
    [InlineData(2880, 1800)]
    [InlineData(300, 2000)]
    [InlineData(4000, 120)]
    [InlineData(40, 30)]
    public void AnyCaptureShapeFillsTheSameCardEdgeToEdge(int width, int height)
    {
        var image = CardLayout.Image(new Size(width, height));

        Assert.True(image.Left <= 0 && image.Top <= 0);
        Assert.True(image.Right >= CardLayout.Width - 0.001 && image.Bottom >= CardLayout.Height - 0.001);
    }

    private static bool Overlaps(Rect a, Rect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
}

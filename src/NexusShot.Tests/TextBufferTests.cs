using NexusShot.Core;

namespace NexusShot.Tests;

/// <summary>The editing model every text input shares: what it refuses, and where it stops.</summary>
public class TextBufferTests
{
    [Fact]
    public void AnInsertDropsRefusedCharactersAndStopsAtTheMaximumLength()
    {
        var text = new TextBuffer("", accept: char.IsAsciiDigit, maxLength: 3);
        text.Insert("1a2b3c4");

        Assert.Equal("123", text.Text);
        Assert.Equal(3, text.Caret);
    }

    [Fact]
    public void ARefusedKeyLeavesTheSelectionItWouldHaveReplaced()
    {
        var text = new TextBuffer("42", accept: char.IsAsciiDigit);
        text.Insert("x");

        Assert.Equal("42", text.Text);
        Assert.Equal("42", text.SelectedText);
        Assert.False(text.CanUndo);
    }

    [Fact]
    public void ReplacingASelectionOnlyCountsWhatIsLeftAgainstTheMaximum()
    {
        var text = new TextBuffer("123", maxLength: 3);
        text.Insert("98765");

        Assert.Equal("987", text.Text);
    }

    [Fact]
    public void TheMaximumNeverSplitsASurrogatePair()
    {
        var text = new TextBuffer("", maxLength: 2);
        text.Insert("a\U0001F600");

        Assert.Equal("a", text.Text);
    }
}

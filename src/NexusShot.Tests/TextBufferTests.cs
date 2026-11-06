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

    [Fact]
    public void UndoAndRedoWalkEveryStepOfEditsMadeAllOverTheText()
    {
        var text = new TextBuffer("");
        List<string> states = [""];
        for (var i = 0; i < 60; i++)
        {
            text.MoveTo(i * 7 % (text.Text.Length + 1));
            text.Insert(((char)('a' + i % 3)).ToString());
            states.Add(text.Text);
        }

        for (var i = states.Count - 1; i > 0; i--)
        {
            Assert.Equal(states[i], text.Text);
            text.Undo();
        }
        Assert.Equal("", text.Text);
        Assert.False(text.CanUndo);
        for (var i = 1; i < states.Count; i++)
        {
            text.Redo();
            Assert.Equal(states[i], text.Text);
        }
    }

    [Fact]
    public void UndoRestoresTheTextFormattingAndCaretOfEachStep()
    {
        var text = new TextBuffer("");
        text.Insert("hello");
        text.Backspace();
        text.Backspace();
        text.Toggle(TextStyle.Bold);

        text.Undo();
        Assert.Equal("hel", text.Text);
        Assert.Equal(TextStyle.None, text.ActiveStyle);
        text.Undo();
        Assert.Equal("hello", text.Text);
        Assert.Equal(5, text.Caret);
        text.Undo();
        Assert.Equal("", text.Text);
        text.Redo();
        text.Redo();
        text.Redo();
        Assert.Equal("hel", text.Text);
        Assert.Equal(TextStyle.Bold, text.ActiveStyle);
    }
}

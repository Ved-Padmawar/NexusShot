using NexusShot.Core;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

/// <summary>Formatting part of a text box: the runs stay canonical through every edit, so a box that
/// looks uniform is stored as uniform.</summary>
public class TextRunsTests
{
    private const TextStyle Bold = TextStyle.Bold;

    [Fact]
    public void BoldingTheMiddleSplitsTheTextIntoThreeRuns()
    {
        var (style, runs) = TextRuns.Apply(TextStyle.None, [], 11, 4, 7, Bold, on: true);

        Assert.Equal(TextStyle.None, style);
        Assert.Equal([new TextRun(4, TextStyle.None), new TextRun(3, Bold), new TextRun(4, TextStyle.None)], runs);
    }

    [Fact]
    public void UndoingTheOnlyDifferenceCollapsesBackToNoRuns()
    {
        var (style, runs) = TextRuns.Apply(TextStyle.None, [], 11, 4, 7, Bold, on: true);
        (style, runs) = TextRuns.Apply(style, runs, 11, 4, 7, Bold, on: false);

        Assert.Equal((TextStyle.None, 0), (style, runs.Length));
    }

    [Fact]
    public void FormattingEverythingChangesTheBaseSoAnEmptiedBoxKeepsIt()
    {
        var (style, runs) = TextRuns.Apply(TextStyle.Italic, [], 5, 0, 5, Bold, on: true);
        (style, runs) = TextRuns.Splice(style, runs, 5, 0, 5, 0);

        Assert.Equal(TextStyle.Italic | Bold, style);
        Assert.Empty(runs);
    }

    [Fact]
    public void TypingContinuesThePreviousCharacterAndAtTheStartTheNextOne()
    {
        // "ab" with only "b" bold.
        TextRun[] runs = [new(1, TextStyle.None), new(1, Bold)];

        var (_, afterB) = TextRuns.Splice(TextStyle.None, runs, 2, 2, 0, 3);
        Assert.Equal([new TextRun(1, TextStyle.None), new TextRun(4, Bold)], afterB);

        var (_, beforeA) = TextRuns.Splice(TextStyle.None, runs, 2, 0, 0, 2);
        Assert.Equal([new TextRun(3, TextStyle.None), new TextRun(1, Bold)], beforeA);
    }

    [Fact]
    public void TypingOverASelectionTakesTheSelectionsStyle()
    {
        // "abcd" with "bc" bold; "bc" replaced by five characters.
        TextRun[] runs = [new(1, TextStyle.None), new(2, Bold), new(1, TextStyle.None)];

        var (_, replaced) = TextRuns.Splice(TextStyle.None, runs, 4, 1, 2, 5);

        Assert.Equal([new TextRun(1, TextStyle.None), new TextRun(5, Bold), new TextRun(1, TextStyle.None)], replaced);
    }

    [Fact]
    public void CommonReportsOnlyTheStylesTheWholeRangeShares()
    {
        TextRun[] runs = [new(2, Bold | TextStyle.Italic), new(2, Bold)];

        Assert.Equal(Bold | TextStyle.Italic, TextRuns.Common(TextStyle.None, runs, 4, 0, 2));
        Assert.Equal(Bold, TextRuns.Common(TextStyle.None, runs, 4, 0, 4));
        Assert.Equal(Bold, TextRuns.Common(TextStyle.None, runs, 4, 3, 3));   // typing at 3 continues "Bold"
    }

    [Fact]
    public void PlainTextWrittenBackWithoutFormattingDropsRunsThatNoLongerFit()
    {
        var document = NewDocument();
        var text = Draw(document, EditorTool.Text, new Point(100, 100), new Point(400, 200));
        document.SetTextContent(text, "abcd", text.Bounds, (TextStyle.None, [new(2, Bold), new(2, TextStyle.None)]));

        document.SetTextContent(text, "abcdef", text.Bounds);

        Assert.Empty(text.Runs);
    }
}

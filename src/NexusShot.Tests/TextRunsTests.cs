using NexusShot.Core;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

/// <summary>Formatting part of a text box: the runs stay canonical through every edit, so a box that
/// looks uniform is stored as uniform.</summary>
public class TextRunsTests
{
    private const TextStyle Bold = TextStyle.Bold;

    private static readonly TextFormat Plain = new(TextStyle.None, 20);

    private static TextFormat F(TextStyle style, double size = 20) => new(style, size);

    [Fact]
    public void BoldingTheMiddleSplitsTheTextIntoThreeRuns()
    {
        var (format, runs) = TextRuns.Apply(Plain, [], 11, 4, 7, Bold, on: true);

        Assert.Equal(Plain, format);
        Assert.Equal([new TextRun(4, Plain), new TextRun(3, F(Bold)), new TextRun(4, Plain)], runs);
    }

    [Fact]
    public void UndoingTheOnlyDifferenceCollapsesBackToNoRuns()
    {
        var (format, runs) = TextRuns.Apply(Plain, [], 11, 4, 7, Bold, on: true);
        (format, runs) = TextRuns.Apply(format, runs, 11, 4, 7, Bold, on: false);

        Assert.Equal((Plain, 0), (format, runs.Length));
    }

    [Fact]
    public void FormattingEverythingChangesTheBaseSoAnEmptiedBoxKeepsIt()
    {
        var (format, runs) = TextRuns.Apply(F(TextStyle.Italic), [], 5, 0, 5, Bold, on: true);
        (format, runs) = TextRuns.Splice(format, runs, 5, 0, 5, 0);

        Assert.Equal(F(TextStyle.Italic | Bold), format);
        Assert.Empty(runs);
    }

    [Fact]
    public void TypingContinuesThePreviousCharacterAndAtTheStartTheNextOne()
    {
        // "ab" with only "b" bold.
        TextRun[] runs = [new(1, Plain), new(1, F(Bold))];

        var (_, afterB) = TextRuns.Splice(Plain, runs, 2, 2, 0, 3);
        Assert.Equal([new TextRun(1, Plain), new TextRun(4, F(Bold))], afterB);

        var (_, beforeA) = TextRuns.Splice(Plain, runs, 2, 0, 0, 2);
        Assert.Equal([new TextRun(3, Plain), new TextRun(1, F(Bold))], beforeA);
    }

    [Fact]
    public void TypingOverASelectionTakesTheSelectionsFormat()
    {
        // "abcd" with "bc" bold; "bc" replaced by five characters.
        TextRun[] runs = [new(1, Plain), new(2, F(Bold)), new(1, Plain)];

        var (_, replaced) = TextRuns.Splice(Plain, runs, 4, 1, 2, 5);

        Assert.Equal([new TextRun(1, Plain), new TextRun(5, F(Bold)), new TextRun(1, Plain)], replaced);
    }

    [Fact]
    public void CommonReportsOnlyTheStylesTheWholeRangeShares()
    {
        TextRun[] runs = [new(2, F(Bold | TextStyle.Italic)), new(2, F(Bold))];

        Assert.Equal(Bold | TextStyle.Italic, TextRuns.Common(Plain, runs, 4, 0, 2));
        Assert.Equal(Bold, TextRuns.Common(Plain, runs, 4, 0, 4));
        Assert.Equal(Bold, TextRuns.Common(Plain, runs, 4, 3, 3));   // typing at 3 continues "Bold"
    }

    [Fact]
    public void ResizingARangeKeepsItsStyleAndLeavesTheRestAlone()
    {
        // "one two" with "one" bold; "two" enlarged.
        TextRun[] runs = [new(3, F(Bold)), new(4, Plain)];

        var (format, resized) = TextRuns.Resize(Plain, runs, 7, 4, 7, 40);

        Assert.Equal(Plain, format);
        Assert.Equal([new TextRun(3, F(Bold)), new TextRun(1, Plain), new TextRun(3, F(TextStyle.None, 40))], resized);
        Assert.Equal(40, TextRuns.SizeAt(format, resized, 7, 4, 7));
        Assert.Equal(40, TextRuns.Largest(format, resized));
    }

    [Fact]
    public void ResizingEverythingCollapsesMixedSizesIntoTheBase()
    {
        TextRun[] runs = [new(2, F(TextStyle.None, 40)), new(2, Plain)];

        var (format, resized) = TextRuns.Resize(Plain, runs, 4, 0, 4, 30);

        Assert.Equal(F(TextStyle.None, 30), format);
        Assert.Empty(resized);
    }

    [Fact]
    public void PlainTextWrittenBackWithoutFormattingDropsRunsThatNoLongerFit()
    {
        var document = NewDocument();
        var text = Draw(document, EditorTool.Text, new Point(100, 100), new Point(400, 200));
        document.SetTextContent(text, "abcd", text.Bounds, (text.Format, [new(2, text.Format with { Style = Bold }), new(2, text.Format)]));

        document.SetTextContent(text, "abcdef", text.Bounds);

        Assert.Empty(text.Runs);
    }
}

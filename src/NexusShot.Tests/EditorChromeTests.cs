using NexusShot.Core;
using NexusShot.Render;
using NexusShot.Views;
using static NexusShot.Tests.Editing;
using Command = NexusShot.Views.EditorChrome.Command;

namespace NexusShot.Tests;

/// <summary>
/// The editor's floating chrome, drawn offscreen and clicked where it drew itself. Controls are
/// found by sweeping the pointer, not by restating the layout, so these check that what is drawn
/// is what answers the click.
/// </summary>
public sealed class EditorChromeTests : IDisposable
{
    private const int Width = 1000;
    private const int Height = 700;

    private readonly Offscreen _screen = new(Width, Height);
    private readonly Ui _ui;
    private readonly EditorChrome _chrome;
    private readonly AppSettings _settings = new();

    public EditorChromeTests()
    {
        _ui = new Ui(_screen.Resources);
        _chrome = new EditorChrome(_ui);
    }

    public void Dispose()
    {
        _chrome.Dispose();
        _screen.Dispose();
    }

    private EditorChrome.Frame Frame(EditorDocument document, bool busy = false) => new(
        document, _settings, Width, Height, CaptionButtonsWidth: 138, "capture.png", Zoom: 1,
        ImageOnScreen: new Rect(100, 80, ImageWidth / 2, ImageHeight / 2), ImageScale: 0.5, ImageOrigin: new Point(100, 80),
        Toast: null, Busy: busy, TextStyle: TextStyle.None);

    /// <summary>A press frame then a release frame; the previous click's release already left the
    /// pointer up, so no hover frame is needed first.</summary>
    private void Click(EditorDocument document, Point at, bool busy = false)
    {
        _screen.Frame(_ui, at, down: true, () => _chrome.Draw(Frame(document, busy)));
        _screen.Frame(_ui, at, down: false, () => _chrome.Draw(Frame(document, busy)));
    }

    [Fact]
    public void TheRailOffersEveryToolOnceInItsOrder()
    {
        var document = NewDocument();
        var picked = new List<EditorTool>();
        for (var y = 0; y < Height; y += 8)
        {
            Click(document, new Point(34, y));
            if (_chrome.ToolPicked is { } tool && (picked.Count == 0 || picked[^1] != tool)) picked.Add(tool);
        }

        Assert.Equal(Enum.GetValues<EditorTool>().Length, picked.Count);
        Assert.Equal(picked.Count, picked.Distinct().Count());
        Assert.Equal(EditorTool.Select, picked[0]);
        Assert.Equal(EditorTool.Crop, picked[^1]);
    }

    [Fact]
    public void TheChromeCoversItsPillsButLeavesTheCanvasFree()
    {
        _screen.Frame(_ui, new Point(-1, -1), down: false, () => _chrome.Draw(Frame(NewDocument())));

        Assert.True(_chrome.Covers(new Point(34, Height / 2)));        // the rail
        Assert.True(_chrome.Covers(new Point(Width / 2, Height - 38)));  // the style bar
        Assert.False(_chrome.Covers(new Point(Width / 2, Height / 2)));
    }

    [Fact]
    public void UndoIsOfferedOnlyWhenThereIsSomethingToUndo()
    {
        var row = Height - 16 - 18;
        Command[] Sweep(EditorDocument document)
        {
            var seen = new List<Command>();
            for (var x = 0; x < 120; x += 6)
            {
                Click(document, new Point(x, row));
                seen.Add(_chrome.Requested);
            }
            return [.. seen];
        }

        Assert.DoesNotContain(Command.Undo, Sweep(NewDocument()));

        var edited = NewDocument();
        Draw(edited, EditorTool.Rectangle, new Point(10, 10), new Point(80, 80));
        Assert.Contains(Command.Undo, Sweep(edited));
    }

    [Fact]
    public void FileActionsAreRefusedWhileASaveIsRunning()
    {
        var document = NewDocument();
        Command[] Sweep(bool busy)
        {
            var seen = new List<Command>();
            for (var x = Width / 2; x < Width; x += 6)
            {
                Click(document, new Point(x, 22), busy);
                seen.Add(_chrome.Requested);
            }
            return [.. seen];
        }

        Assert.Contains(Command.Save, Sweep(busy: false));
        Assert.Contains(Command.CopyAndClose, Sweep(busy: false));
        Assert.All(Sweep(busy: true), command => Assert.Equal(Command.None, command));
    }

    [Fact]
    public void ASwatchRecoloursTheSelectionAsOneUndoStep()
    {
        var document = NewDocument();
        var shape = Draw(document, EditorTool.Rectangle, new Point(10, 10), new Point(80, 80));
        var row = Height - 16 - 22;

        for (var x = 0; x < Width && shape.ColorHex == "#FF3B30"; x += 6)
            Click(document, new Point(x, row));

        Assert.NotEqual("#FF3B30", shape.ColorHex);
        Assert.Contains(shape.ColorHex, Palette.Swatches);
        document.Undo();
        Assert.Equal("#FF3B30", Assert.Single(document.Annotations).ColorHex);
    }

    [Fact]
    public void TheTextStylesAreReportedForTheWindowToApply()
    {
        var document = NewDocument();
        document.ActiveTool = EditorTool.Text;
        var toggled = new List<TextStyle>();

        for (var x = 0; x < Width; x += 6)
        {
            Click(document, new Point(x, Height - 16 - 22));
            if (_chrome.StyleToggled is { } style && !toggled.Contains(style)) toggled.Add(style);
        }

        Assert.Equal([TextStyle.Bold, TextStyle.Italic, TextStyle.Underline], toggled);
        Assert.Equal(TextStyle.None, document.TextStyle);   // the chrome only reports; the window formats
    }

    [Fact]
    public void TheCropBarAppliesOrCancelsTheSession()
    {
        EditorDocument Session()
        {
            var document = NewDocument();
            document.BeginCropSession();
            Drag(document, new Point(ImageWidth, ImageHeight), new Point(400, 300));
            return document;
        }

        // The frame's bottom edge is at 80 + 300 * 0.5 on screen; the bar hangs 14 below it.
        var row = 80 + 150 + 14 + 18;
        bool applied = false, cancelled = false;
        for (var x = 0; x < Width; x += 6)
        {
            var document = Session();
            Click(document, new Point(x, row));
            if (_chrome.ToolPicked != EditorTool.Select) continue;
            if (document.CropBounds == new Rect(0, 0, 400, 300) && !document.IsCropSessionActive) applied = true;
            else if (document.CropBounds is null) cancelled = true;
        }

        Assert.True(applied, "no Apply button answered on the crop bar");
        Assert.True(cancelled, "no Cancel button answered on the crop bar");
    }
}

using NexusShot.Render;

namespace NexusShot.Tests;

/// <summary>
/// Widget ids were hand-allocated integers, and every collision was a control silently driving
/// another one's hot/active state.
/// </summary>
public class UiIdTests
{
    [Fact]
    public void TheSameNameAlwaysGivesTheSameId()
    {
        Assert.Equal(Ui.Id("main.settings"), Ui.Id("main.settings"));
    }

    [Fact]
    public void DifferentNamesDoNotCollide()
    {
        var names = new[]
        {
            "main.newcapture", "main.settings", "main.edit", "main.copy", "main.remove",
            "main.reveal", "main.share", "main.dismiss", "main.history.clear", "main.history.row",
            "capture.region", "capture.fullscreen", "capture.window",
            "settings.autocopy", "settings.autosave", "settings.startup", "settings.theme",
            "settings.capturemode", "settings.folder.change", "settings.reset",
            "settings.dismissdelay", "settings.timerdelay", "restore.tile", "restore.history",
            "hotkey.CaptureRegion", "hotkey.CaptureFullScreen", "hotkey.CaptureActiveWindow", "hotkey.OpenMainWindow",
            "hotkey.RestoreClosed", "hotkey.CaptureText", "hotkey.TimedCapture",
            "preview.copy", "preview.copytext", "preview.save", "preview.edit", "preview.pin", "preview.close",
            "editor.undo", "editor.redo", "editor.delete", "editor.save", "editor.saveas",
            "editor.copy", "editor.copytext", "editor.zoom.fit", "editor.zoom.actual", "editor.colour.chip",
            "editor.thickness",
            "picker.field", "picker.hue", "picker.hex",
        };

        Assert.Equal(names.Length, names.Select(Ui.Id).Distinct().Count());
    }

    [Fact]
    public void NoIdIsZeroBecauseZeroMeansNoWidget()
    {
        Assert.NotEqual(0, Ui.Id(""));
        Assert.NotEqual(0, Ui.Id(0, 0));
    }

    [Fact]
    public void DerivedIdsDoNotWalkIntoTheNextOwner()
    {
        // `owner + index` was the old scheme: row 7 of one list landed on row 0 of the next.
        var first = Ui.Id("list.a");
        var second = Ui.Id("list.b");

        var ids = Enumerable.Range(0, 200)
            .SelectMany(i => new[] { Ui.Id(first, i), Ui.Id(second, i) })
            .ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void DerivedIdsAreDistinctFromTheirOwner()
    {
        var owner = Ui.Id("hotkey.CaptureRegion");
        Assert.NotEqual(owner, Ui.Id(owner, 1));
    }
}

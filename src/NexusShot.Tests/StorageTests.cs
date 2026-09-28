using NexusShot.Core;

namespace NexusShot.Tests;

public class StorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nexusshot-storage-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void NullSettingsMembersFallBackWithoutCrashingStartup()
    {
        var storage = new Storage(_directory);
        File.WriteAllText(storage.SettingsPath, """
            {"ScreenshotFolder":null,"CaptureRegionHotkey":null,"CaptureFullScreenHotkey":null,
             "CaptureActiveWindowHotkey":null,"OpenMainWindowHotkey":null,"RestoreClosedHotkey":null,
             "CaptureTextHotkey":null,"TimedCaptureHotkey":null,"Theme":999,"PreviewDismissSeconds":-1}
            """);
        var settings = storage.LoadSettings();
        Assert.False(string.IsNullOrWhiteSpace(settings.ScreenshotFolder));
        Assert.NotNull(settings.CaptureRegionHotkey);
        Assert.NotNull(settings.CaptureFullScreenHotkey);
        Assert.NotNull(settings.CaptureActiveWindowHotkey);
        foreach (var id in HotkeyIds.All) Assert.NotNull(settings.Hotkey(id));
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal(0, settings.PreviewDismissSeconds);
    }

    [Fact]
    public void NullHistoryEntriesAreIgnored()
    {
        var storage = new Storage(_directory);
        File.WriteAllText(storage.HistoryPath, """
            [null, {"FilePath":null,"CapturedAt":"2026-09-03T00:00:00Z"}]
            """);
        Assert.Empty(storage.LoadHistory());
    }

    [Fact]
    public void MalformedJsonIsPreservedBeforeFallingBack()
    {
        var storage = new Storage(_directory);
        File.WriteAllText(storage.SettingsPath, "{broken");
        var settings = storage.LoadSettings();
        Assert.Equal((uint)'S', settings.CaptureRegionHotkey.Key);
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal("{broken", File.ReadAllText(storage.SettingsPath + ".bak"));
    }

    [Fact]
    public void SettingsRoundTripPreservesUserChoices()
    {
        var storage = new Storage(_directory);
        storage.SaveSettings(new AppSettings
        {
            ScreenshotFolder = _directory, Theme = AppTheme.Dark, PreviewDismissSeconds = 45,
            CopyToClipboardAutomatically = false,
            CaptureRegionHotkey = new HotkeyBinding { Key = 0x78, Modifiers = 1 },
        });
        var read = new Storage(_directory).LoadSettings();
        Assert.Equal(_directory, read.ScreenshotFolder);
        Assert.Equal(AppTheme.Dark, read.Theme);
        Assert.Equal(45, read.PreviewDismissSeconds);
        Assert.False(read.CopyToClipboardAutomatically);
        Assert.Equal(0x78u, read.CaptureRegionHotkey.Key);
        Assert.Equal(1u, read.CaptureRegionHotkey.Modifiers);
    }

    [Fact]
    public void FavoritesAndReadTextSurviveARestart()
    {
        var storage = new Storage(_directory);
        storage.SaveHistory([new ScreenshotHistoryItem { FilePath = @"C:\s\a.png", CapturedAt = DateTimeOffset.Now, Favorite = true }]);
        var texts = new TextIndex();
        texts.Set(@"C:\s\a.png", new FileVersion(3, new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc)), "line one");
        storage.SaveTextIndex(texts);

        var reopened = new Storage(_directory);
        Assert.True(Assert.Single(reopened.LoadHistory()).Favorite);
        var read = reopened.LoadTextIndex();
        Assert.Equal("line one", read.TextOf(@"C:\S\A.PNG"));
        Assert.True(read.IsCurrent(@"C:\s\a.png", new FileVersion(3, new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc))));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

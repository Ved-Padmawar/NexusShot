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
             "CaptureActiveWindowHotkey":null,"OpenMainWindowHotkey":null,"Theme":999,"PreviewDismissSeconds":-1}
            """);
        var settings = storage.LoadSettings();
        Assert.False(string.IsNullOrWhiteSpace(settings.ScreenshotFolder));
        Assert.NotNull(settings.CaptureRegionHotkey);
        Assert.NotNull(settings.CaptureFullScreenHotkey);
        Assert.NotNull(settings.CaptureActiveWindowHotkey);
        Assert.NotNull(settings.OpenMainWindowHotkey);
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
            SaveAutomatically = false, CopyToClipboardAutomatically = false,
            CaptureRegionHotkey = new HotkeyBinding { Key = 0x78, Modifiers = 1 },
        });
        var read = new Storage(_directory).LoadSettings();
        Assert.Equal(_directory, read.ScreenshotFolder);
        Assert.Equal(AppTheme.Dark, read.Theme);
        Assert.Equal(45, read.PreviewDismissSeconds);
        Assert.False(read.SaveAutomatically);
        Assert.False(read.CopyToClipboardAutomatically);
        Assert.Equal(0x78u, read.CaptureRegionHotkey.Key);
        Assert.Equal(1u, read.CaptureRegionHotkey.Modifiers);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

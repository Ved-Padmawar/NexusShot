using NexusShot.Core;

namespace NexusShot.Tests;

/// <summary>What a settings file may contain and what the app makes of it, the colour lists, and
/// the theme's legibility for every accent in both modes.</summary>
public class SettingsAndThemeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nexusshot-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private AppSettings Load(string json)
    {
        var storage = new Storage(_directory);
        File.WriteAllText(storage.SettingsPath, json);
        return storage.LoadSettings();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 30)]
    [InlineData(10, 10)]
    public void TheTimerIsHeldToItsRange(int stored, int loaded) =>
        Assert.Equal(loaded, Load($$"""{"TimedCaptureSeconds":{{stored}}}""").TimedCaptureSeconds);

    [Fact]
    public void EnumsFromAFutureBuildFallBackToTheirDefaults()
    {
        var settings = Load("""{"CardCorner":9,"CaptureFormat":9,"AfterCapture":9,"DefaultCaptureMode":9}""");

        Assert.Equal(CardCorner.BottomLeft, settings.CardCorner);
        Assert.Equal(ImageFormat.Png, settings.CaptureFormat);
        Assert.Equal(AfterCapture.Card, settings.AfterCapture);
        Assert.Equal(CaptureMode.Region, settings.DefaultCaptureMode);
    }

    [Fact]
    public void AnAccentIsStoredByItsCanonicalName()
    {
        Assert.Equal("Iris", Load("""{"Accent":"iris"}""").Accent);
        Assert.Equal(Accent.Default.Name, Load("""{"Accent":null}""").Accent);
    }

    [Fact]
    public void StoredColourListsLoseJunkAndDuplicatesAndAreCapped()
    {
        var saved = string.Join(",", Enumerable.Range(0, 20).Select(i => $"\"#0000{i:X2}\""));
        var settings = Load($$"""{"RecentColors":["#FF0000","nope",null,"#ff0000","#00FF00"],"SavedColors":[{{saved}}]}""");

        Assert.Equal(["#FF0000", "#00FF00"], settings.RecentColors);
        Assert.Equal(AppSettings.MaxSavedColors, settings.SavedColors.Count);
    }

    [Fact]
    public void HistoryKeepsOneEntryPerFile()
    {
        var storage = new Storage(_directory);
        File.WriteAllText(storage.HistoryPath, """
            [{"FilePath":"C:\\s\\a.png","CapturedAt":"2026-09-03T00:00:00Z","Width":10},
             {"FilePath":"c:\\S\\A.PNG","CapturedAt":"2026-09-02T00:00:00Z","Width":20}]
            """);

        Assert.Equal(10, Assert.Single(storage.LoadHistory()).Width);
    }

    [Fact]
    public void AnUnwritableSettingsPathIsLoggedNotThrown()
    {
        var storage = new Storage(_directory);
        Directory.CreateDirectory(storage.SettingsPath);   // a folder where the file should go

        storage.SaveSettings(new AppSettings());

        Assert.True(Directory.Exists(storage.SettingsPath));
    }

    [Fact]
    public void SavedColoursAreUniqueNewestLastAndDropTheOldestPastTheCap()
    {
        var settings = new AppSettings();
        for (var i = 0; i < AppSettings.MaxSavedColors; i++) settings.SaveColor($"#1000{i:X2}");
        settings.SaveColor("#100000".ToLowerInvariant());
        settings.SaveColor("#ABCDEF");

        Assert.Equal(AppSettings.MaxSavedColors, settings.SavedColors.Count);
        Assert.Equal("#ABCDEF", settings.SavedColors[^1]);
        Assert.Equal("#100000", settings.SavedColors[^2], ignoreCase: true);
        Assert.DoesNotContain("#100001", settings.SavedColors);

        settings.ForgetSavedColor("#abcdef");
        Assert.DoesNotContain("#ABCDEF", settings.SavedColors);
    }

    [Fact]
    public void EveryShortcutHasItsOwnBindingAndTimedCaptureStartsUnbound()
    {
        var settings = new AppSettings();
        var bindings = HotkeyIds.All.Select(settings.Hotkey).ToList();

        Assert.Equal(bindings.Count, bindings.Distinct().Count());
        Assert.Equal(0u, settings.Hotkey(HotkeyId.TimedCapture).Key);
        var bound = bindings.Where(binding => binding.Key != 0).ToList();
        Assert.All(bound, binding => Assert.Single(bound, other => other.IsSameGesture(binding)));
    }

    // ---- theme ----

    /// <summary>The WCAG contrast ratio of two opaque colours.</summary>
    internal static double Contrast(Rgba a, Rgba b)
    {
        static double Luminance(Rgba color)
        {
            static double Channel(byte value)
            {
                var c = value / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }
        var (light, dark) = (Luminance(a), Luminance(b));
        if (light < dark) (light, dark) = (dark, light);
        return (light + 0.05) / (dark + 0.05);
    }

    public static TheoryData<bool, string> Themes()
    {
        var themes = new TheoryData<bool, string>();
        foreach (var dark in new[] { true, false })
            foreach (var accent in Accent.Presets) themes.Add(dark, accent.Name);
        return themes;
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void TextOnEveryAccentAndSurfaceIsReadable(bool dark, string accent)
    {
        var theme = Theme.Resolve(dark, Accent.Named(accent));

        Assert.True(Contrast(theme.TextOnAccent, theme.Accent) >= 4.5, "text on the accent");
        Assert.True(Contrast(theme.TextPrimary, theme.SurfaceWindow) >= 7, "primary text");
        Assert.True(Contrast(theme.TextSecondary, theme.SurfaceRaised) >= 4.5, "secondary text");
        Assert.True(Contrast(theme.AccentText, theme.SurfaceWindow) >= 3, "accent text");
    }

    [Fact]
    public void ThemesDifferOnlyWhereTheModeSaysSo()
    {
        var dark = Theme.Resolve(true, Accent.Default);
        var light = Theme.Resolve(false, Accent.Default);

        Assert.True(dark.IsDark);
        Assert.False(light.IsDark);
        Assert.Equal(dark.Accent, light.Accent);
        Assert.NotEqual(dark.SurfaceWindow, light.SurfaceWindow);
        Assert.Equal(dark, Theme.Dark);
    }

    [Fact]
    public void AnAccentNameIsMatchedWithoutCase() => Assert.Equal("Coral", Accent.Named("CORAL").Name);

    [Fact]
    public void CompositingATintKeepsTheSurfaceOpaque()
    {
        var lifted = Rgba.Black.Under(Rgba.White.WithAlpha(128));
        Assert.Equal(255, lifted.A);
        Assert.InRange(lifted.R, 127, 129);
    }

    [Theory]
    [InlineData("#FFFFFF", true)]
    [InlineData("#FFCC00", true)]
    [InlineData("#0A84FF", false)]
    [InlineData("#1C1C1E", false)]
    public void CounterDigitsPickTheReadableInk(string hex, bool light) =>
        Assert.Equal(light, Palette.IsLight(Palette.Parse(hex)));
}

using NexusShot.Core;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

/// <summary>The Core behind the 0.3.0 interface: colour with alpha, the picker's colour models, shape
/// fills, counter runs, zoom and pan, the library grid, card corners and the icon path parser.</summary>
public class RedesignTests
{
    // ---- colour ----

    [Fact]
    public void HexWithAlphaRoundTrips()
    {
        Assert.True(Palette.TryParse("#FF3B3080", out var color));
        Assert.Equal(new Rgba(0xFF, 0x3B, 0x30, 0x80), color);
        Assert.Equal("#FF3B3080", color.ToHex());
    }

    [Fact]
    public void OpaqueColourKeepsTheSixDigitForm()
    {
        Assert.Equal("#0A84FF", new Rgba(0x0A, 0x84, 0xFF).ToHex());
    }

    [Theory]
    [InlineData("#FF3B3")]
    [InlineData("#FF3B30F")]
    [InlineData("#GG3B30")]
    public void MalformedHexIsRejected(string hex) => Assert.False(Palette.TryParse(hex, out _));

    [Theory]
    [InlineData(255, 59, 48)]
    [InlineData(10, 132, 255)]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(52, 199, 89)]
    public void HsvRoundTripsEveryChannel(byte r, byte g, byte b)
    {
        var color = new Rgba(r, g, b, 200);
        Assert.Equal(color, Hsva.From(color).ToRgba());
    }

    [Fact]
    public void GreyKeepsTheHueItWasGiven()
    {
        // Dragging into the black corner must not snap the rail back to red.
        var grey = Hsva.From(new Rgba(40, 40, 40), keepHue: 210);
        Assert.Equal(210, grey.Hue);
    }

    [Theory]
    [InlineData(210, 0.6, 0.35)]
    [InlineData(0, 1, 0.5)]
    [InlineData(120, 0.25, 0.8)]
    public void HslRoundTripsThroughHsv(double hue, double saturation, double lightness)
    {
        var (s, l) = Hsva.FromHsl(hue, saturation, lightness, 1).ToHsl();
        Assert.Equal(saturation, s, 6);
        Assert.Equal(lightness, l, 6);
    }

    [Fact]
    public void AColourDragIsOneUndoStep()
    {
        var document = NewDocument();
        var rectangle = Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(300, 300));
        document.SelectAnnotation(rectangle);

        document.SetColor("#00FF00", isAdjusting: true);
        document.SetColor("#00EE00", isAdjusting: true);
        document.SetColor("#00DD00", isAdjusting: true);
        document.EndAdjustment();

        Assert.Equal("#00DD00", rectangle.ColorHex);
        document.Undo();
        Assert.Equal("#FF3B30", document.Annotations[0].ColorHex);
    }

    // ---- shapes and sizes ----

    [Fact]
    public void FillAppliesToNewShapesAndTheSelectionUndoably()
    {
        var document = NewDocument();
        document.SetFill(ShapeFill.Tinted);
        var ellipse = Draw(document, EditorTool.Ellipse, new Point(100, 100), new Point(300, 200));
        Assert.Equal(ShapeFill.Tinted, ellipse.Fill);

        document.SelectAnnotation(ellipse);
        document.SetFill(ShapeFill.Solid);
        Assert.Equal(ShapeFill.Solid, ellipse.Fill);

        document.Undo();
        Assert.Equal(ShapeFill.Tinted, document.Annotations[0].Fill);
    }

    [Fact]
    public void FillSurvivesUndoSnapshots()
    {
        var shape = new Annotation { Tool = EditorTool.Rectangle, Fill = ShapeFill.Solid, CounterRun = 3 };
        var clone = shape.Clone();
        Assert.Equal(ShapeFill.Solid, clone.Fill);
        Assert.Equal(3, clone.CounterRun);
    }

    [Fact]
    public void SizeControlEditsTheSelectionsOwnKind()
    {
        // A selected text box with the select tool: the control means its font, not a stroke width.
        var document = NewDocument();
        var text = Draw(document, EditorTool.Text, new Point(100, 100), new Point(400, 200));
        document.ActiveTool = EditorTool.Select;
        document.SelectAnnotation(text);

        Assert.Equal(EditorTool.Text, document.SizingTool);
        document.SetStrokeThickness(48);
        Assert.Equal(48, text.FontSize);
        Assert.Equal(48, document.ActiveThickness);
    }

    [Fact]
    public void CounterResetStartsANewRunAndUndoGivesNumbersBack()
    {
        var document = NewDocument();
        Counter(document, new Point(100, 100));
        Counter(document, new Point(200, 100));
        Assert.Equal(3, document.NextCounter);

        document.ResetCounter();
        Assert.Equal(1, document.NextCounter);
        Assert.Equal(1, Counter(document, new Point(300, 100)).CounterValue);

        document.Undo();
        Assert.Equal(1, document.NextCounter);
    }

    [Fact]
    public void CropFrameResetsToTheWholeImage()
    {
        var document = NewDocument();
        document.BeginCropSession();
        Drag(document, new Point(0, 0), new Point(200, 150));
        document.ResetCropFrame();
        Assert.Equal(new Rect(0, 0, ImageWidth, ImageHeight), document.PendingCrop);
    }

    [Fact]
    public void AnUncroppedDocumentShowsTheWholeImage() =>
        Assert.Equal(new Rect(0, 0, ImageWidth, ImageHeight), NewDocument().VisibleBounds);

    [Fact]
    public void AppliedCropIsWhatIsShownButASessionShowsEverything()
    {
        var document = NewDocument();
        document.BeginCropSession();
        Drag(document, new Point(0, 0), new Point(200, 150));
        document.CommitCrop();
        var crop = document.CropBounds!.Value;
        Assert.Equal(crop, document.VisibleBounds);

        // Reopening the session shows the whole image again, so the frame can grow back out.
        document.BeginCropSession();
        Assert.Equal(new Rect(0, 0, ImageWidth, ImageHeight), document.VisibleBounds);
    }

    // ---- viewport ----

    [Fact]
    public void FitNeverEnlargesAndCentres()
    {
        var viewport = new Viewport();
        var well = new Rect(0, 0, 1000, 800);
        var placed = viewport.Place(well, new Size(400, 200));
        Assert.Equal(new Rect(300, 300, 400, 200), placed);
    }

    [Fact]
    public void ZoomKeepsTheAnchorPointStill()
    {
        var viewport = new Viewport();
        var well = new Rect(0, 0, 800, 600);
        var image = new Size(1600, 1200);

        var before = viewport.Place(well, image);
        var anchor = new Point(200, 150);
        var imageX = (anchor.X - before.X) / (before.Width / image.Width);

        viewport.ZoomBy(2, well, image, anchor);
        var after = viewport.Place(well, image);
        Assert.Equal(imageX, (anchor.X - after.X) / (after.Width / image.Width), 3);
    }

    [Fact]
    public void PanStopsAtTheEdges()
    {
        var viewport = new Viewport();
        var well = new Rect(0, 0, 800, 600);
        var image = new Size(1600, 1200);
        viewport.Actual(well, image, new Point(400, 300));

        viewport.PanBy(10_000, 10_000);
        var placed = viewport.Place(well, image);
        Assert.Equal(0, placed.X);
        Assert.Equal(0, placed.Y);
    }

    [Fact]
    public void ZoomIsClamped()
    {
        var viewport = new Viewport();
        var well = new Rect(0, 0, 800, 600);
        var image = new Size(400, 300);
        for (var i = 0; i < 50; i++) viewport.ZoomBy(Viewport.Step, well, image, well.Center);
        Assert.Equal(Viewport.MaxZoom, viewport.Zoom);
    }

    // ---- library ----

    [Fact]
    public void LibraryGroupsByDayAndFilters()
    {
        var now = new DateTime(2026, 9, 25, 17, 0, 0);
        List<ScreenshotHistoryItem> history =
        [
            Item("a.png", now.AddMinutes(-5)),
            Item("b.png", now.AddHours(-3)),
            Item("c.png", now.AddDays(-1)),
            Item("d.png", now.AddDays(-3)),
            Item("e.png", now.AddDays(-40)),
        ];

        var groups = LibraryGroups.Build(history, "", now);
        Assert.Equal(["Today", "Yesterday", "This week", now.AddDays(-40).ToString("MMMM yyyy")],
            groups.Select(group => group.Title));
        Assert.Equal(2, groups[0].Items.Count);

        var filtered = LibraryGroups.Build(history, "C.PNG", now);
        Assert.Equal("c.png", Assert.Single(Assert.Single(filtered).Items).FileName);
    }

    [Fact]
    public void GridFitsAsManyColumnsAsTheMinimumAllows()
    {
        var layout = new LibraryLayout(868, 1);
        Assert.Equal(4, layout.Columns);
        Assert.True(layout.TileWidth >= layout.MinTile);
        Assert.Equal(layout.TileWidth * 10 / 16, layout.ImageHeight, 6);
        Assert.Equal(new Rect(0, layout.HeaderHeight + layout.TileHeight + layout.RowGap, layout.TileWidth, layout.TileHeight),
            layout.Tile(0, 0, 4));
    }

    [Fact]
    public void ThumbnailsDecodeToCoverTheTileAndSurviveASmallResize()
    {
        var layout = new LibraryLayout(868, 1);
        var nudged = new LibraryLayout(880, 1);

        Assert.True(layout.DecodeWidth >= layout.TileWidth);
        Assert.Equal(0, layout.DecodeWidth % 64);
        Assert.Equal(layout.DecodeWidth, nudged.DecodeWidth);
    }

    [Fact]
    public void PickingATileTwiceUnpicksItButKeepsTheModeOpen()
    {
        var selection = new LibrarySelection();

        selection.Toggle(@"C:\Shots\a.png");
        selection.Toggle(@"C:\SHOTS\b.png");
        Assert.True(selection.Active);
        Assert.Equal(2, selection.Count);

        selection.Toggle(@"c:\shots\A.PNG");
        selection.Toggle(@"C:\Shots\b.png");
        Assert.Equal(0, selection.Count);
        Assert.True(selection.Active);
    }

    [Fact]
    public void ADeletedCaptureLeavesTheCountAndEndingClearsEverything()
    {
        var selection = new LibrarySelection();
        selection.Toggle(@"C:\Shots\a.png");
        selection.Toggle(@"C:\Shots\b.png");

        selection.Forget(@"C:\Shots\a.png");
        Assert.Equal([@"C:\Shots\b.png"], selection.Paths);

        selection.End();
        Assert.False(selection.Active);
        Assert.Equal(0, selection.Count);
    }

    [Fact]
    public void SelectAllPicksEveryShownCaptureOnceAndEntersTheMode()
    {
        var selection = new LibrarySelection();

        selection.SelectAll([@"C:\Shots\a.png", @"C:\Shots\b.png", @"c:\shots\A.PNG"]);

        Assert.True(selection.Active);
        Assert.Equal(2, selection.Count);
    }

    [Fact]
    public void SelectAllKeepsPicksASearchHasHidden()
    {
        var selection = new LibrarySelection();
        selection.Toggle(@"C:\Shots\hidden-by-search.png");

        selection.SelectAll([@"C:\Shots\a.png"]);

        Assert.True(selection.Contains(@"C:\Shots\hidden-by-search.png"));
        Assert.Equal(2, selection.Count);
    }

    [Fact]
    public void WheelStepsGlideToTheirTargetAndLandExactly()
    {
        var scroll = new ScrollState();
        scroll.SetRange(1000);

        scroll.Wheel(-100);
        scroll.Wheel(-100);
        Assert.Equal(200, scroll.Target);

        var last = 0.0;
        var frames = 0;
        while (scroll.Step(8.3))
        {
            Assert.True(scroll.Position > last);
            last = scroll.Position;
            Assert.True(++frames < 120, "the glide never settled");
        }
        Assert.Equal(200, scroll.Position);
    }

    [Fact]
    public void APanCancelsAGlideAndAShrinkingRangePullsBothBack()
    {
        var scroll = new ScrollState();
        scroll.SetRange(1000);
        scroll.Wheel(-500);

        scroll.Pan(-50);
        Assert.Equal((50.0, 50.0), (scroll.Position, scroll.Target));

        scroll.SetRange(20);
        Assert.Equal((20.0, 20.0), (scroll.Position, scroll.Target));
    }

    private const string Downloads = Updates.DownloadPrefix + "v0.4.0/";

    [Fact]
    public void AReleaseNeedsItsInstallerAndSignatureFromThisRepository()
    {
        var release = Updates.Parse($$"""
            { "tag_name": "v0.4.0", "assets": [
              { "name": "NexusShot-0.4.0.exe", "size": 5000, "browser_download_url": "{{Downloads}}NexusShot-0.4.0.exe" },
              { "name": "NexusShot-0.4.0.exe.sig", "browser_download_url": "{{Downloads}}NexusShot-0.4.0.exe.sig" } ] }
            """);
        Assert.Equal(new Version(0, 4, 0), release!.Version);
        Assert.Equal(5000, release.InstallerSize);
        Assert.EndsWith(".exe.sig", release.SignatureUrl);

        Assert.Null(Updates.Parse($$"""
            { "tag_name": "v0.4.0", "assets": [
              { "name": "NexusShot-0.4.0.exe", "browser_download_url": "{{Downloads}}NexusShot-0.4.0.exe" } ] }
            """));
        Assert.Null(Updates.Parse("""
            { "tag_name": "v0.4.0", "assets": [
              { "name": "NexusShot-0.4.0.exe", "browser_download_url": "https://example.com/NexusShot-0.4.0.exe" },
              { "name": "NexusShot-0.4.0.exe.sig", "browser_download_url": "https://example.com/NexusShot-0.4.0.exe.sig" } ] }
            """));
    }

    [Fact]
    public void OnlyAGenuinelyLaterVersionIsAnUpdate()
    {
        Assert.True(Updates.IsNewer(new Version(0, 3, 1), new Version(0, 3, 0, 0)));
        Assert.False(Updates.IsNewer(new Version(0, 3, 0), new Version(0, 3, 0, 0)));
        Assert.False(Updates.IsNewer(new Version(0, 2, 9), new Version(0, 3, 0, 0)));
    }

    [Fact]
    public void AnInstallerSignedWithTheReleaseKeyIsAccepted()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var digest = System.Security.Cryptography.SHA256.HashData("installer bytes"u8);
        var signature = Convert.ToBase64String(key.SignHash(digest));

        Assert.True(Updates.IsSigned(digest, signature, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
    }

    [Fact]
    public void ATamperedOrUnsignedInstallerIsRefused()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var digest = System.Security.Cryptography.SHA256.HashData("installer bytes"u8);
        var signature = Convert.ToBase64String(key.SignHash(digest));

        var tampered = System.Security.Cryptography.SHA256.HashData("installer bytes, altered"u8);
        Assert.False(Updates.IsSigned(tampered, signature, publicKey));
        Assert.False(Updates.IsSigned(digest, "not a signature", publicKey));
        Assert.False(Updates.IsSigned(digest, signature));   // someone else's key, checked against ours
    }

    [Fact]
    public void APreviewPixelIsTheAverageOfTheBlockItCovers()
    {
        // 4x2 pixels, BGRA: the left 2x2 block is black and white, the right one solid red.
        byte[] pixels =
        [
            0, 0, 0, 255,   255, 255, 255, 255,   0, 0, 255, 255,   0, 0, 255, 255,
            255, 255, 255, 255,   0, 0, 0, 255,   0, 0, 255, 255,   0, 0, 255, 255,
        ];

        var shrunk = Downsample.Box(pixels, 4, 2, 2, out var width, out var height);

        Assert.Equal((2, 1), (width, height));
        Assert.Equal(new byte[] { 127, 127, 127, 255, 0, 0, 255, 255 }, shrunk);
    }

    [Fact]
    public void APartialEdgeBlockIsFoldedInRatherThanDropped()
    {
        // 5 pixels wide at factor 2: the fifth column belongs to the last output pixel.
        byte[] pixels = [.. Enumerable.Repeat<byte>(0, 16), 200, 200, 200, 200];

        var shrunk = Downsample.Box(pixels, 5, 1, 2, out var width, out _);

        Assert.Equal(2, width);
        Assert.Equal(66, shrunk[4]);
    }

    [Theory]
    [InlineData(868, 1, 700)]
    [InlineData(1900, 1.5, 1200)]
    [InlineData(400, 2, 300)]
    public void TheCacheHoldsEveryTileTheGridPreloads(double width, double scale, double viewport)
    {
        var layout = new LibraryLayout(width, scale);
        var capacity = layout.CacheCapacity(viewport);

        // The grid keeps the tiles within one screen above and below the visible one, at any scroll.
        for (var scroll = 0.0; scroll < viewport * 10; scroll += viewport / 7)
        {
            var kept = Enumerable.Range(0, 2000).Select(i => layout.Tile(0, 0, i))
                .Count(tile => tile.Bottom >= scroll - viewport && tile.Y <= scroll + viewport * 2);
            Assert.True(kept <= capacity, $"{kept} tiles in reach at scroll {scroll}, capacity {capacity}");
        }
    }

    // ---- cards ----

    [Theory]
    [InlineData(CardCorner.BottomLeft, 18, 832)]
    [InlineData(CardCorner.BottomRight, 1758, 832)]
    [InlineData(CardCorner.TopLeft, 18, 118)]
    [InlineData(CardCorner.TopRight, 1758, 118)]
    public void CardsStackFromTheirCorner(CardCorner corner, double x, double y)
    {
        var slot = CardLayout.Slot(new Rect(0, 0, 1920, 1040), new Size(144, 90), 18, 100, corner);
        Assert.Equal(new Point(x, y), slot);
    }

    [Fact]
    public void NewCardsArePinnedWhenAsked()
    {
        var quick = new QuickAccess(new AppSettings { PinNewCards = true });
        Assert.True(quick.Show(Item("a.png", DateTime.Now)).IsPinned);
    }

    // ---- settings ----

    [Fact]
    public void RecentColoursAreUniqueNewestFirstAndCapped()
    {
        var settings = new AppSettings();
        for (var i = 0; i < 12; i++) settings.RememberColor($"#0000{i:X2}");
        settings.RememberColor("#000003");
        Assert.Equal(AppSettings.MaxRecentColors, settings.RecentColors.Count);
        Assert.Equal("#000003", settings.RecentColors[0]);
        Assert.Single(settings.RecentColors, hex => hex == "#000003");
    }

    [Fact]
    public void UnknownAccentFallsBackToTheDefault() =>
        Assert.Same(Accent.Default, Accent.Named("Chartreuse"));

    // ---- icons ----

    [Fact]
    public void IconPathReadsRelativeCommandsAndCompactNumbers()
    {
        var figures = IconPath.Parse("M4 4h5v5H4zM9.5.5a2 2 0 0 1 2.9 0");
        Assert.Equal(2, figures.Count);
        Assert.True(figures[0].Closed);
        Assert.Equal(new Point(9, 9), ((IconLine)figures[0].Segments[1]).To);

        Assert.Equal(new Point(9.5, 0.5), figures[1].Start);
        var arc = Assert.IsType<IconArc>(Assert.Single(figures[1].Segments));
        Assert.Equal(new Point(12.4, 0.5), arc.To);
        Assert.True(arc.Clockwise);
    }

    [Fact]
    public void IconPathRejectsUnsupportedCommands() =>
        Assert.Throws<FormatException>(() => IconPath.Parse("M0 0Q1 1 2 2"));

    [Fact]
    public void EveryIconParses()
    {
        foreach (var field in typeof(Render.Icons).GetFields())
        {
            var icon = (Render.Icon)field.GetValue(null)!;
            Assert.NotEmpty(icon.Stroke);
        }
    }

    private static ScreenshotHistoryItem Item(string name, DateTime captured) =>
        new() { FilePath = Path.Combine("C:\\shots", name), CapturedAt = captured };
}

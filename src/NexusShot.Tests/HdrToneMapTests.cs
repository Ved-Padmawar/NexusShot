using NexusShot.Core;

namespace NexusShot.Tests;

/// <summary>
/// HDR frames to 8-bit: the interface must come out as it looks, and highlights must stay apart
/// instead of all clipping to the same white.
/// </summary>
public class HdrToneMapTests
{
    private const float ScRgbWhite = 80f;

    private static byte[] Convert(float white, params float[] rgba)
    {
        var bgra = new byte[rgba.Length];
        HdrToneMap.ConvertRow(rgba.Select(value => (Half)value).ToArray(), bgra, white);
        return bgra;
    }

    [Fact]
    public void SdrContentBelowTheKneeIsOnlyEncodedAndFollowsTheWhiteLevel()
    {
        var atReference = Convert(ScRgbWhite, 0.214f, 0.214f, 0.214f, 1f);
        var atBrighterWhite = Convert(ScRgbWhite * 2, 0.428f, 0.428f, 0.428f, 1f);

        Assert.InRange(atReference[0], 127, 129);
        Assert.Equal(atReference, atBrighterWhite);
        Assert.Equal(255, atReference[3]);
    }

    [Fact]
    public void ATranslucentHighlightNeverComesOutBrighterThanItsAlpha()
    {
        var edge = Convert(ScRgbWhite, 2f, 2f, 2f, 0.5f);

        Assert.InRange(edge[3], 127, 128);
        Assert.All(edge[..3], colour => Assert.InRange(colour, 1, edge[3]));
    }

    [Fact]
    public void ATransparentPixelComesOutEmptyWhateverItsColourHolds()
    {
        Assert.All(Convert(ScRgbWhite, 3f, 1f, 0.2f, 0f), value => Assert.Equal(0, value));
    }

    [Fact]
    public void HighlightsAboveSdrWhiteStayDistinctAndKeepTheirHue()
    {
        var white = Convert(ScRgbWhite, 1f, 1f, 1f, 1f)[0];
        var bright = Convert(ScRgbWhite, 2f, 2f, 2f, 1f)[0];
        var brightest = Convert(ScRgbWhite, 4f, 4f, 4f, 1f)[0];
        var tinted = Convert(ScRgbWhite, 2f, 1f, 0.5f, 1f);

        Assert.True(white < bright && bright < brightest);
        Assert.True(tinted[0] < tinted[1] && tinted[1] < tinted[2]);
    }
}

using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot.Tests;

/// <summary>Rectangle maths every layout leans on, the arrow's head, and where the desktop pickers
/// put their badge and loupe.</summary>
public class PlacementTests
{
    [Fact]
    public void EdgesInAnyOrderMakeTheSameRectangle() =>
        Assert.Equal(new Rect(10, 20, 30, 40), Rect.FromEdges(40, 60, 10, 20));

    [Fact]
    public void ContainsIncludesTheEdges()
    {
        var rect = new Rect(0, 0, 10, 10);
        Assert.True(rect.Contains(new Point(10, 10)));
        Assert.False(rect.Contains(new Point(10.01, 5)));
    }

    [Fact]
    public void RectsIntersectInTheirOverlapOrNotAtAll()
    {
        Assert.Equal(new Rect(50, 20, 50, 30), new Rect(0, 0, 100, 50).Intersect(new Rect(50, 20, 200, 200)));
        Assert.True(new Rect(0, 0, 10, 10).Intersect(new Rect(10, 0, 10, 10)).IsEmpty);
    }

    [Fact]
    public void DeflatingStopsAtTheCentre()
    {
        Assert.Equal(new Rect(12, 12, 76, 16), new Rect(10, 10, 80, 20).Deflate(2));
        Assert.Equal(new Rect(50, 20, 0, 0), new Rect(10, 10, 80, 20).Deflate(100));
        Assert.Equal(new Rect(10, 10, 80, 20), new Rect(10, 10, 80, 20).Deflate(-5));
    }

    [Fact]
    public void FitCentresAWholeImageWithoutEnlargingIt()
    {
        var box = new Rect(0, 0, 400, 300);
        Assert.Equal(new Rect(100, 100, 200, 100), box.Fit(new Size(200, 100)));
        Assert.Equal(new Rect(0, 50, 400, 200), box.Fit(new Size(800, 400)));
    }

    [Fact]
    public void FitMayEnlargeWhenAskedAndLandsOnWholePixels()
    {
        var fitted = new Rect(0, 0, 401, 301).Fit(new Size(200, 100), enlarge: true);
        Assert.Equal(401, fitted.Width);
        Assert.Equal(Math.Round(fitted.Y), fitted.Y);
        Assert.Equal(Math.Round(fitted.Height), fitted.Height);
    }

    [Fact]
    public void CoverFillsTheBoxAndOverflowsOneAxisEvenly()
    {
        var covered = new Rect(0, 0, 100, 100).Cover(new Size(200, 100));
        Assert.Equal(new Rect(-50, 0, 200, 100), covered);
    }

    [Fact]
    public void DegenerateContentLeavesTheBoxAsItIs()
    {
        var box = new Rect(5, 5, 100, 50);
        Assert.Equal(box, box.Fit(Size.Empty));
        Assert.Equal(box, box.Cover(new Size(0, 10)));
    }

    // ---- arrows ----

    private static Annotation Arrow(Point from, Point to, double thickness) =>
        new() { Tool = EditorTool.Arrow, Start = from, End = to, StrokeThickness = thickness };

    [Fact]
    public void TheArrowheadPointsAtTheEndAndIsSymmetricAboutTheShaft()
    {
        var head = ArrowGeometry.Head(Arrow(new Point(0, 50), new Point(200, 50), 4));

        Assert.Equal(new Point(200, 50), head[0]);
        Assert.True(head[1].X < 200 && head[2].X < 200);
        Assert.Equal(head[1].X, head[2].X, 6);
        Assert.Equal(50 - head[1].Y, head[2].Y - 50, 6);
    }

    [Fact]
    public void TheShaftStopsInsideTheHeadSoTheTipIsNotBlunted()
    {
        var arrow = Arrow(new Point(0, 50), new Point(200, 50), 4);
        var shaft = ArrowGeometry.ShaftEnd(arrow);
        var headBase = ArrowGeometry.Head(arrow)[1].X;

        Assert.Equal(50, shaft.Y, 6);
        Assert.InRange(shaft.X, headBase, 200 - 0.1);
    }

    [Fact]
    public void AShortArrowsHeadIsNoLongerThanTheArrow()
    {
        var head = ArrowGeometry.Head(Arrow(new Point(0, 0), new Point(6, 0), 20));
        Assert.True(head[1].X >= -0.001, $"head reaches back to {head[1].X}");
    }

    [Fact]
    public void AZeroLengthArrowStillHasAHeadAndNoShaft()
    {
        var arrow = Arrow(new Point(30, 30), new Point(30, 30), 4);
        Assert.Equal(new Point(30, 30), ArrowGeometry.ShaftEnd(arrow));
        Assert.All(ArrowGeometry.Head(arrow), point => Assert.False(double.IsNaN(point.X) || double.IsNaN(point.Y)));
    }

    // ---- desktop pickers ----

    [Fact]
    public void AClickOrAOnePixelDragSelectsNothing()
    {
        Assert.Null(OverlayGeometry.Selection(new Point(50, 50), new Point(50, 50)));
        Assert.Null(OverlayGeometry.Selection(new Point(50, 50), new Point(300, 51)));
    }

    [Fact]
    public void ADragInAnyDirectionSelectsTheSameRegion() =>
        Assert.Equal(new Rect(10, 20, 90, 80), OverlayGeometry.Selection(new Point(100, 100), new Point(10, 20)));

    private static readonly Size Desktop = new(1920, 1080);
    private static readonly Size Badge = new(80, 26);

    [Fact]
    public void TheSizeBadgeSitsBelowTheSelection() =>
        Assert.Equal(new Point(100, 308), OverlayGeometry.SizeBadge(new Rect(100, 100, 200, 200), Badge, Desktop, 8));

    [Fact]
    public void TheSizeBadgeMovesAboveASelectionAtTheBottomEdge() =>
        Assert.Equal(new Point(100, 866), OverlayGeometry.SizeBadge(new Rect(100, 900, 200, 180), Badge, Desktop, 8));

    [Fact]
    public void TheSizeBadgeStaysOnTheDesktopWhenTheSelectionFillsIt()
    {
        var badge = OverlayGeometry.SizeBadge(new Rect(1900, 0, 20, 1080), Badge, Desktop, 8);
        Assert.Equal(new Point(1840, 0), badge);
    }

    private static readonly Size Loupe = new(121, 151);

    [Fact]
    public void TheLoupeSitsBelowRightOfThePointer() =>
        Assert.Equal(new Point(524, 324), OverlayGeometry.Loupe(new Point(500, 300), Loupe, 24, Desktop));

    [Fact]
    public void TheLoupeFlipsOnlyOnTheAxisThatWouldRunOff()
    {
        Assert.Equal(new Point(1900 - 24 - 121, 324), OverlayGeometry.Loupe(new Point(1900, 300), Loupe, 24, Desktop));
        Assert.Equal(new Point(524, 1000 - 24 - 151), OverlayGeometry.Loupe(new Point(500, 1000), Loupe, 24, Desktop));
    }

    [Fact]
    public void TheEyedropperReadsStraightColourAndNothingOffTheImage()
    {
        using var image = DecodedImage.Allocate(2, 2);
        image.Span.Fill(0);
        (image.Span[12], image.Span[13], image.Span[14], image.Span[15]) = (30, 20, 10, 255);   // pixel (1,1), BGRA

        Assert.Equal(new Rgba(10, 20, 30), image.OpaquePixelAt(1, 1));
        Assert.Null(image.OpaquePixelAt(-1, 0));
        Assert.Null(image.OpaquePixelAt(2, 0));
        Assert.Null(image.OpaquePixelAt(0, 2));
    }
}

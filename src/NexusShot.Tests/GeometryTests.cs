using NexusShot.Core;

namespace NexusShot.Tests;

/// <summary>The shared rectangle maths every box interaction routes through.</summary>
public class GeometryTests
{
    private static readonly Rect Limits = new(0, 0, 1000, 800);

    [Fact]
    public void ResizeNormalisesWhenAHandleCrossesItsAnchor()
    {
        var origin = new Rect(100, 100, 200, 200);

        // Dragging the bottom-right corner up past the top-left flips the box rather than
        // producing a negative size.
        var resized = BoxGeometry.Resize(origin, ResizeHandle.BottomRight, new Point(50, 40), Limits);

        Assert.True(resized.Width >= 0);
        Assert.True(resized.Height >= 0);
        Assert.Equal(50, resized.Left, 3);
        Assert.Equal(40, resized.Top, 3);
    }

    [Fact]
    public void ResizeIsClampedToTheLimits()
    {
        var origin = new Rect(100, 100, 200, 200);
        var resized = BoxGeometry.Resize(origin, ResizeHandle.BottomRight, new Point(5000, 5000), Limits);

        Assert.Equal(Limits.Right, resized.Right, 3);
        Assert.Equal(Limits.Bottom, resized.Bottom, 3);
    }

    [Fact]
    public void ResizeHonoursAMinimumSize()
    {
        var origin = new Rect(100, 100, 200, 200);
        var resized = BoxGeometry.Resize(
            origin, ResizeHandle.BottomRight, new Point(100, 100), Limits, new Size(20, 20));

        Assert.True(resized.Width >= 20);
        Assert.True(resized.Height >= 20);
    }

    [Fact]
    public void AnEdgeHandleMovesOnlyItsOwnEdge()
    {
        var origin = new Rect(100, 100, 200, 200);
        var resized = BoxGeometry.Resize(origin, ResizeHandle.Right, new Point(400, 999), Limits);

        Assert.Equal(100, resized.Top, 3);
        Assert.Equal(300, resized.Bottom, 3);
        Assert.Equal(400, resized.Right, 3);
    }

    [Fact]
    public void TranslateStopsAtTheLimits()
    {
        var bounds = new Rect(700, 500, 200, 200);
        var moved = BoxGeometry.Translate(bounds, 500, 500, Limits);

        Assert.Equal(Limits.Right, moved.Right, 3);
        Assert.Equal(Limits.Bottom, moved.Bottom, 3);
    }

    [Fact]
    public void ABoxAlreadyOutsideCannotBePushedFurtherOut()
    {
        var bounds = new Rect(900, 700, 200, 200);
        var moved = BoxGeometry.Translate(bounds, 500, 500, Limits);

        Assert.Equal(bounds.X, moved.X, 3);
        Assert.Equal(bounds.Y, moved.Y, 3);
    }

    [Fact]
    public void ABoxOverhangingOneSideCanMoveBackInside()
    {
        // Hanging off the left edge only: it may travel right, back into the image.
        var bounds = new Rect(-100, 100, 200, 200);

        var inward = BoxGeometry.Translate(bounds, 60, 0, Limits);
        Assert.Equal(-40, inward.X, 3);

        // But not further out.
        var outward = BoxGeometry.Translate(bounds, -60, 0, Limits);
        Assert.Equal(-100, outward.X, 3);
    }

    [Fact]
    public void ABoxLargerThanTheLimitsIsHeldStill()
    {
        // Overhanging on both sides at once, there is no inward direction to offer.
        var bounds = new Rect(-100, -100, 1400, 1200);

        Assert.Equal(-100, BoxGeometry.Translate(bounds, 50, 50, Limits).X, 3);
        Assert.Equal(-100, BoxGeometry.Translate(bounds, -50, -50, Limits).X, 3);
    }

    [Theory]
    [InlineData(ResizeHandle.TopLeft, 100, 100)]
    [InlineData(ResizeHandle.TopRight, 300, 100)]
    [InlineData(ResizeHandle.BottomLeft, 100, 300)]
    [InlineData(ResizeHandle.BottomRight, 300, 300)]
    [InlineData(ResizeHandle.Top, 200, 100)]
    [InlineData(ResizeHandle.Bottom, 200, 300)]
    [InlineData(ResizeHandle.Left, 100, 200)]
    [InlineData(ResizeHandle.Right, 300, 200)]
    public void HandlesSitOnTheirCorners(ResizeHandle handle, double x, double y)
    {
        var position = BoxGeometry.HandlePosition(new Rect(100, 100, 200, 200), handle);

        Assert.Equal(x, position.X, 3);
        Assert.Equal(y, position.Y, 3);
    }

    [Fact]
    public void HandleHitTestingFindsTheNearestGrip()
    {
        var bounds = new Rect(100, 100, 200, 200);

        Assert.Equal(ResizeHandle.TopLeft, BoxGeometry.HitTestHandle(bounds, new Point(103, 103), 8));
        Assert.Null(BoxGeometry.HitTestHandle(bounds, new Point(200, 200), 8));
    }

    [Fact]
    public void DimBandsCoverEverythingOutsideTheRegion()
    {
        var bands = AdornerGeometry.DimAround(new Rect(100, 100, 200, 200), 1000, 800).ToList();

        var area = bands.Sum(band => band.Width * band.Height);
        Assert.Equal(1000 * 800 - 200 * 200, area, 3);
    }

    [Fact]
    public void DimBandsAreEmptyWhenTheRegionFillsTheCanvas()
    {
        var bands = AdornerGeometry.DimAround(new Rect(0, 0, 1000, 800), 1000, 800).ToList();
        Assert.Empty(bands);
    }

    [Fact]
    public void GripArmsShrinkOnATinyBoxSoTheyCannotCross()
    {
        var metrics = AdornerGeometry.Grips(new Rect(0, 0, 12, 12), 1);
        Assert.True(metrics.Arm <= 4);
    }


    [Fact]
    public void AShapeWithItsOwnOutlineGetsNoExtraFrame()
    {
        var rectangle = new Annotation
        {
            Tool = EditorTool.Rectangle,
            Start = new Point(100, 100),
            End = new Point(300, 300),
        };

        var adorner = AdornerGeometry.Selection(rectangle, 1);
        Assert.Null(adorner.DashedFrame);
    }

    [Fact]
    public void DistanceToSegmentIsMeasuredFromTheNearestPointOnIt()
    {
        var a = new Point(0, 0);
        var b = new Point(100, 0);

        Assert.Equal(10, Annotation.DistanceToSegment(new Point(50, 10), a, b), 3);

        // Past the end, the distance is to the endpoint, not to the infinite line.
        Assert.Equal(50, Annotation.DistanceToSegment(new Point(150, 0), a, b), 3);
    }
}

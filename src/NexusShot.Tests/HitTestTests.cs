using NexusShot.Core;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

/// <summary>What is grabbable where. Freehand strokes are tested along their painted path rather
/// than at their samples, and the large area tools by their edges.</summary>
public class HitTestTests
{
    [Fact]
    public void AStrokeIsHitBetweenTwoDistantSamples()
    {
        var stroke = new Annotation { Tool = EditorTool.Pen, StrokeThickness = 4 };
        stroke.Points.Add(new Point(100, 100));
        stroke.Points.Add(new Point(400, 100));

        // Midway along the segment: no sample sits here, but paint does.
        Assert.True(stroke.HitTest(new Point(250, 100)));
    }

    [Fact]
    public void AStrokeIsNotHitOffThePaintedPath()
    {
        var stroke = new Annotation { Tool = EditorTool.Pen, StrokeThickness = 4 };
        stroke.Points.Add(new Point(100, 100));
        stroke.Points.Add(new Point(400, 100));

        Assert.False(stroke.HitTest(new Point(250, 200)));
    }

    [Fact]
    public void AStrokeIsNotHitAtTheCornerOfItsSampleBox()
    {
        var stroke = new Annotation { Tool = EditorTool.Pen, StrokeThickness = 2 };
        stroke.Points.Add(new Point(100, 100));

        // Diagonally off a single dab: inside a square around the sample, outside the round dab.
        Assert.False(stroke.HitTest(new Point(106, 106), slack: 3));
    }

    [Fact]
    public void ASingleDabIsHitAtItsCentre()
    {
        var stroke = new Annotation { Tool = EditorTool.Brush, StrokeThickness = 20 };
        stroke.Points.Add(new Point(100, 100));

        Assert.True(stroke.HitTest(new Point(100, 100)));
    }

    [Fact]
    public void AnEmptyStrokeIsNeverHit()
    {
        var stroke = new Annotation { Tool = EditorTool.Pen };
        Assert.False(stroke.HitTest(new Point(0, 0)));
    }

    [Fact]
    public void ARectangleIsHitInsideItsBounds()
    {
        var rectangle = new Annotation
        {
            Tool = EditorTool.Rectangle,
            Start = new Point(100, 100),
            End = new Point(300, 300),
        };

        Assert.True(rectangle.HitTest(new Point(200, 200)));
        Assert.False(rectangle.HitTest(new Point(400, 200)));
    }

    [Fact]
    public void ALineIsHitAlongItsLengthOnly()
    {
        var line = new Annotation
        {
            Tool = EditorTool.Line,
            Start = new Point(100, 100),
            End = new Point(300, 300),
            StrokeThickness = 4,
        };

        Assert.True(line.HitTest(new Point(200, 200)));
        Assert.False(line.HitTest(new Point(200, 280)));
    }

    [Fact]
    public void ACounterIsHitAcrossItsBadgeNotJustItsCentre()
    {
        var counter = new Annotation
        {
            Tool = EditorTool.Counter,
            Start = new Point(200, 200),
            End = new Point(200, 200),
            StrokeThickness = 4,
        };

        Assert.True(counter.HitTest(new Point(200, 200)));
        Assert.True(counter.HitTest(new Point(205, 205)));
        Assert.False(counter.HitTest(new Point(400, 400)));
    }
}

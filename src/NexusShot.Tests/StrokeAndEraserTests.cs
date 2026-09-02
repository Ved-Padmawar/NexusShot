using NexusShot.Core;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

/// <summary>Freehand painting, the eraser's masks, and the geometry version that cached masks are
/// keyed on.</summary>
public class StrokeAndEraserTests
{
    private static List<Point> Line(double y, int count = 20)
    {
        var points = new List<Point>();
        for (var i = 0; i < count; i++)
            points.Add(new Point(100 + i * 20, y));
        return points;
    }

    [Fact]
    public void APenStrokeKeepsItsPath()
    {
        var document = NewDocument();
        var stroke = Stroke(document, EditorTool.Pen, Line(300));

        Assert.Equal(EditorTool.Pen, stroke.Tool);
        Assert.Equal(Line(300), stroke.Points);
    }

    [Fact]
    public void SamplesInsideOneSubpixelAreDropped()
    {
        var document = NewDocument();
        document.ActiveTool = EditorTool.Pen;
        document.BeginGesture(new Point(100, 100));
        for (var i = 0; i < 20; i++)
            document.ContinueGesture(new Point(100.01, 100.01));
        document.EndGesture(new Point(100.01, 100.01));

        Assert.Single(document.Annotations[0].Points);
    }

    [Fact]
    public void ASingleTapLeavesAValidDab()
    {
        var document = NewDocument();
        document.ActiveTool = EditorTool.Brush;
        document.SetStrokeThickness(40);
        document.BeginGesture(new Point(300, 300));
        document.EndGesture(new Point(300, 300));

        Assert.Single(document.Annotations);
        Assert.Single(document.Annotations[0].Points);
    }

    [Fact]
    public void TheEraserIsNotItselfAnAnnotation()
    {
        var document = NewDocument();
        Stroke(document, EditorTool.Pen, Line(300));
        Stroke(document, EditorTool.Eraser, Line(300), thickness: 30);

        Assert.Single(document.Annotations);
        Assert.Equal(EditorTool.Pen, document.Annotations[0].Tool);
    }

    [Fact]
    public void ErasingAcrossAStrokeMasksIt()
    {
        var document = NewDocument();
        var pen = Stroke(document, EditorTool.Pen, Line(300));

        Stroke(document, EditorTool.Eraser, [new Point(200, 300), new Point(300, 300)], thickness: 30);

        Assert.NotEmpty(pen.Erasures);
    }

    [Fact]
    public void ErasingNothingLeavesNoUndoEntry()
    {
        var document = NewDocument();
        Stroke(document, EditorTool.Pen, Line(300));

        var before = document.CanUndo;
        Stroke(document, EditorTool.Eraser, [new Point(50, 700), new Point(80, 700)], thickness: 10);

        // Nothing was under the eraser, so history is unchanged.
        Assert.Equal(before, document.CanUndo);
        document.Undo();
        Assert.Empty(document.Annotations);
    }

    [Fact]
    public void AnErasureIsUndoable()
    {
        var document = NewDocument();
        var pen = Stroke(document, EditorTool.Pen, Line(300));
        Stroke(document, EditorTool.Eraser, [new Point(200, 300), new Point(300, 300)], thickness: 30);

        Assert.NotEmpty(pen.Erasures);

        document.Undo();
        Assert.Empty(document.Annotations[0].Erasures);
    }

    [Fact]
    public void TheEraserDoesNotTouchShapes()
    {
        var document = NewDocument();
        Draw(document, EditorTool.Rectangle, new Point(100, 100), new Point(400, 400));

        Stroke(document, EditorTool.Eraser, [new Point(200, 200), new Point(300, 300)], thickness: 40);

        Assert.Empty(document.Annotations[0].Erasures);
    }

    [Fact]
    public void GeometryVersionChangesWhenAStrokeGrows()
    {
        var document = NewDocument();
        document.ActiveTool = EditorTool.Pen;
        document.BeginGesture(new Point(100, 100));
        var stroke = Assert.Single(document.Annotations);
        var before = stroke.GeometryVersion;

        document.ContinueGesture(new Point(200, 150));

        Assert.NotEqual(before, stroke.GeometryVersion);
        Assert.Equal(new Rect(100, 100, 100, 50), stroke.Bounds);
    }

    [Fact]
    public void GeometryVersionChangesWhenAnAnnotationMoves()
    {
        var stroke = new Annotation { Tool = EditorTool.Pen };
        stroke.Points.Add(new Point(10, 10));
        var before = stroke.GeometryVersion;

        stroke.Translate(5, 5);

        Assert.NotEqual(before, stroke.GeometryVersion);
    }

    [Fact]
    public void GeometryVersionChangesWithTheStrokeWidth()
    {
        var stroke = new Annotation { Tool = EditorTool.Blur, StrokeThickness = 10 };
        var before = stroke.GeometryVersion;

        stroke.StrokeThickness = 20;

        Assert.NotEqual(before, stroke.GeometryVersion);
    }

    [Fact]
    public void ARestoredCloneDoesNotReuseAnotherAnnotationsVersion()
    {
        var stroke = new Annotation { Tool = EditorTool.Pen };
        stroke.Points.Add(new Point(10, 10));

        var snapshot = stroke.Clone();
        stroke.Translate(5, 5);

        // The snapshot must not claim the version the live object now carries, or a cached mask
        // built from one would be served for the other.
        Assert.NotEqual(stroke.GeometryVersion, snapshot.GeometryVersion);
    }

    [Fact]
    public void ASnapshotKeepsItsIdentitySoUndoCanRestoreTheSelection()
    {
        var stroke = new Annotation { Tool = EditorTool.Pen };
        var snapshot = stroke.Clone();

        Assert.Equal(stroke.Id, snapshot.Id);
    }

    [Fact]
    public void CloningCopiesPointsRatherThanSharingThem()
    {
        var stroke = new Annotation { Tool = EditorTool.Pen };
        stroke.Points.Add(new Point(10, 10));

        var snapshot = stroke.Clone();
        stroke.Points.Add(new Point(50, 50));

        Assert.Single(snapshot.Points);
    }
}

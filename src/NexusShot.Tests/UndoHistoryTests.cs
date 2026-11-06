using NexusShot.Core;

namespace NexusShot.Tests;

/// <summary>
/// Undo/redo ordering and retention. The trim path is the regression guard for the defect where
/// re-pushing a trimmed stack inverted it, so the next Undo restored the oldest state.
/// </summary>
public class UndoHistoryTests
{
    private const int MaxUndo = 100;

    private static EditorDocument NewDocument()
    {
        var document = new EditorDocument();
        document.SetImageSize(1000, 1000);
        return document;
    }

    /// <summary>Draws one rectangle, which is one undoable edit. The selection is cleared first:
    /// a press within the handle tolerance of the previously placed shape would resize it instead
    /// of starting a new one.</summary>
    private static void DrawRectangle(EditorDocument document, double x, double y)
    {
        document.SelectAnnotation(null);
        document.ActiveTool = EditorTool.Rectangle;
        document.BeginGesture(new Point(x, y));
        document.ContinueGesture(new Point(x + 50, y + 50));
        document.EndGesture(new Point(x + 50, y + 50));
    }

    [Fact]
    public void UndoRevertsOnlyTheLastEditWhenHistoryIsTrimmed()
    {
        var document = NewDocument();
        for (var i = 0; i < MaxUndo + 1; i++)
            DrawRectangle(document, i, i);

        Assert.Equal(MaxUndo + 1, document.Annotations.Count);

        document.Undo();

        // One edit reverted, not a jump back to the start of history.
        Assert.Equal(MaxUndo, document.Annotations.Count);
    }

    [Fact]
    public void UndoUnwindsOneEditAtATimeAcrossTheTrimBoundary()
    {
        var document = NewDocument();
        for (var i = 0; i < MaxUndo + 5; i++)
            DrawRectangle(document, i, i);

        var count = document.Annotations.Count;
        for (var i = 0; i < 10; i++)
        {
            document.Undo();
            Assert.Equal(--count, document.Annotations.Count);
        }
    }

    [Fact]
    public void HistoryIsBoundedToMaxUndo()
    {
        var document = NewDocument();
        for (var i = 0; i < MaxUndo + 25; i++)
            DrawRectangle(document, i % 500, i % 500);

        var undone = 0;
        while (document.CanUndo)
        {
            document.Undo();
            undone++;
            Assert.True(undone <= MaxUndo, $"Undo stack exceeded its bound: {undone} entries.");
        }

        Assert.Equal(MaxUndo, undone);
    }

    [Fact]
    public void RedoRestoresAnUndoneEdit()
    {
        var document = NewDocument();
        DrawRectangle(document, 10, 10);
        DrawRectangle(document, 100, 100);

        document.Undo();
        Assert.Single(document.Annotations);

        document.Redo();
        Assert.Equal(2, document.Annotations.Count);
    }

    [Fact]
    public void NewEditClearsTheRedoStack()
    {
        var document = NewDocument();
        DrawRectangle(document, 10, 10);
        document.Undo();
        Assert.True(document.CanRedo);

        DrawRectangle(document, 200, 200);
        Assert.False(document.CanRedo);
    }

    [Fact]
    public void UndoRestoresValuesRatherThanSharedReferences()
    {
        var document = NewDocument();
        DrawRectangle(document, 10, 10);

        var before = document.Annotations[0].Bounds;

        document.SelectAnnotation(document.Annotations[0]);
        document.ActiveTool = EditorTool.Select;
        document.BeginGesture(new Point(20, 20));
        document.ContinueGesture(new Point(120, 120));
        document.EndGesture(new Point(120, 120));

        Assert.NotEqual(before, document.Annotations[0].Bounds);

        document.Undo();
        Assert.Equal(before, document.Annotations[0].Bounds);
    }

    [Fact]
    public void ClearCropIsUndoable()
    {
        var document = NewDocument();
        document.BeginCropSession();
        document.BeginGesture(new Point(0, 0));
        document.ContinueGesture(new Point(400, 400));
        document.EndGesture(new Point(400, 400));
        document.CommitCrop();

        Assert.NotNull(document.CropBounds);

        document.ClearCrop();
        Assert.Null(document.CropBounds);

        document.Undo();
        Assert.NotNull(document.CropBounds);
    }

    [Fact]
    public void ClearCropWithoutACropDoesNotPushHistory()
    {
        var document = NewDocument();
        document.ClearCrop();
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void EditingAnAnnotationAfterUndoLeavesTheStepsThatShareItIntact()
    {
        var document = NewDocument();
        DrawRectangle(document, 100, 100);
        DrawRectangle(document, 300, 300);
        document.Undo();

        document.SelectAnnotation(document.Annotations[0]);
        document.NudgeSelected(10, 0);
        document.EndAdjustment();

        document.Undo();
        Assert.Equal(new Point(100, 100), Assert.Single(document.Annotations).Start);
        document.Undo();
        Assert.Empty(document.Annotations);
        document.Redo();
        document.Redo();
        Assert.Equal(new Point(110, 100), Assert.Single(document.Annotations).Start);
    }

    [Fact]
    public void SameContentNoticesAChangeToEveryCopiedField()
    {
        // Every field a caller can set. A new one fails here until SameContent compares it.
        Assert.Equal(
            ["ColorHex", "CounterRun", "CounterValue", "Dashed", "End", "Erasures", "Fill", "Format", "Id",
             "Points", "Runs", "Start", "StrokeThickness", "Text", "Tool"],
            typeof(Annotation).GetProperties().Where(p => p.SetMethod?.IsPublic == true).Select(p => p.Name).Order());

        Action<Annotation>[] changes =
        [
            a => a.Start = new Point(1, 2),
            a => a.End = new Point(3, 4),
            a => a.Points.Add(new Point(5, 6)),
            a => a.Points[0] = new Point(9, 9),
            a => a.Erasures.Add(new EraserMask { Radius = 2 }),
            a => a.Erasures[0].Points.Add(new Point(1, 1)),
            a => a.Text = "changed",
            a => a.CounterValue = 7,
            a => a.CounterRun = 3,
            a => a.Fill = ShapeFill.Solid,
            a => a.Dashed = true,
            a => a.ColorHex = "#00FF00",
            a => a.StrokeThickness = 9,
            a => a.Format = new TextFormat(TextStyle.Bold, 30),
            a => a.Runs = [new TextRun(1, new TextFormat(TextStyle.Italic, 12))],
        ];
        foreach (var change in changes)
        {
            var original = new Annotation
            {
                Tool = EditorTool.Pen, Points = [new Point(0, 0)], Erasures = [new EraserMask { Radius = 1 }],
            };
            var copy = original.Clone();
            Assert.True(copy.SameContent(original));
            change(original);
            Assert.False(copy.SameContent(original));
        }
        var annotation = new Annotation { Tool = EditorTool.Rectangle };
        Assert.False(annotation.Duplicate().SameContent(annotation));
    }
}

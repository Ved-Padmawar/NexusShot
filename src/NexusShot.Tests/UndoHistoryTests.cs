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
}

using NexusShot.Core;
using static NexusShot.Tests.Editing;

namespace NexusShot.Tests;

/// <summary>The crop session: it opens on the current crop, is dragged by its handles, and only
/// becomes the document's crop when committed.</summary>
public class CropTests
{
    private static EditorDocument WithCropSession()
    {
        var document = NewDocument();
        document.ActiveTool = EditorTool.Crop;
        document.BeginCropSession();
        return document;
    }

    [Fact]
    public void ASessionOpensOnTheWholeImage()
    {
        var document = WithCropSession();

        Assert.True(document.IsCropSessionActive);
        Assert.Equal(new Rect(0, 0, ImageWidth, ImageHeight), document.PendingCrop);
    }

    [Fact]
    public void DraggingAHandleResizesTheFrame()
    {
        var document = WithCropSession();

        Drag(document, new Point(ImageWidth, ImageHeight), new Point(400, 300));

        Assert.Equal(new Rect(0, 0, 400, 300), document.PendingCrop);
    }

    [Fact]
    public void CommitAppliesTheFrame()
    {
        var document = WithCropSession();
        Drag(document, new Point(ImageWidth, ImageHeight), new Point(400, 300));

        document.CommitCrop();

        Assert.False(document.IsCropSessionActive);
        Assert.Equal(new Rect(0, 0, 400, 300), document.CropBounds);
    }

    [Fact]
    public void AFrameCoveringEverythingMeansNoCrop()
    {
        var document = WithCropSession();
        document.CommitCrop();

        Assert.Null(document.CropBounds);
    }

    [Fact]
    public void CancelLeavesTheCommittedCropUntouched()
    {
        var document = WithCropSession();
        Drag(document, new Point(ImageWidth, ImageHeight), new Point(400, 300));
        document.CommitCrop();

        document.BeginCropSession();
        Drag(document, new Point(400, 300), new Point(200, 150));
        document.CancelCropSession();

        Assert.Equal(new Rect(0, 0, 400, 300), document.CropBounds);
    }

    [Fact]
    public void TheFrameIsClampedToTheImage()
    {
        var document = WithCropSession();

        Drag(document, new Point(0, 0), new Point(-500, -500));

        var pending = document.PendingCrop!.Value;
        Assert.Equal(new Rect(0, 0, ImageWidth, ImageHeight), pending);
    }

    [Fact]
    public void TheFrameKeepsAMinimumSize()
    {
        var document = WithCropSession();

        Drag(document, new Point(ImageWidth, ImageHeight), new Point(0, 0));

        var pending = document.PendingCrop!.Value;
        Assert.True(pending.Width >= 8);
        Assert.True(pending.Height >= 8);
    }

    [Fact]
    public void MovingTheFrameKeepsItsSize()
    {
        var document = WithCropSession();
        Drag(document, new Point(ImageWidth, ImageHeight), new Point(400, 300));

        Drag(document, new Point(200, 150), new Point(260, 190));

        var pending = document.PendingCrop!.Value;
        Assert.Equal(400, pending.Width, 3);
        Assert.Equal(300, pending.Height, 3);
        Assert.Equal(60, pending.X, 3);
        Assert.Equal(40, pending.Y, 3);
    }

    [Fact]
    public void ASessionReopensOnTheCommittedCrop()
    {
        var document = WithCropSession();
        Drag(document, new Point(ImageWidth, ImageHeight), new Point(400, 300));
        document.CommitCrop();

        document.BeginCropSession();

        Assert.Equal(new Rect(0, 0, 400, 300), document.PendingCrop);
    }

    [Fact]
    public void ASessionOwnsThePointerSoNothingIsDrawnUnderIt()
    {
        var document = WithCropSession();
        Drag(document, new Point(ImageWidth, ImageHeight), new Point(400, 300));

        document.ActiveTool = EditorTool.Rectangle;
        Drag(document, new Point(600, 500), new Point(700, 600));

        Assert.Empty(document.Annotations);
    }
}

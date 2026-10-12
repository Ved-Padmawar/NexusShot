using NexusShot.Core;
using NexusShot.Render;
using NexusShot.Views;

namespace NexusShot.Tests;

/// <summary>
/// The destination an editor writes to: the path ownership that used to be a mutable field on the
/// window class, and where a share's copy lands. Save As itself needs a file dialog, so it stays with
/// the native audit.
/// </summary>
public class EditorFilesTests
{
    [Fact]
    public void TheDestinationIsTheFileTheEditorWasOpenedOn()
    {
        var files = new EditorFiles(new EditorDocument(), NoPixels);
        files.OpenedAt(@"C:\shots\capture.png");

        Assert.Equal(@"C:\shots\capture.png", files.Path);
        Assert.Equal("capture.png", files.FileName);
    }

    [Fact]
    public void TheDisplayedNameFollowsTheFileWhenItMoves()
    {
        // Save As moves the editor onto the copy, and the chrome reads the filename from here. A
        // second copy of this string on the window is what let the caption and the destination
        // disagree after a Save As.
        var files = new EditorFiles(new EditorDocument(), NoPixels);
        files.OpenedAt(@"C:\shots\capture.png");

        files.OpenedAt(@"D:\archive\capture_edited.png");

        Assert.Equal(@"D:\archive\capture_edited.png", files.Path);
        Assert.Equal("capture_edited.png", files.FileName);
    }

    [Fact]
    public void CancellingSaveAsLeavesTheCropAndTheDestinationUntouched()
    {
        // Regression: a cancelled Save As must cost the document nothing. Committing the crop before
        // the picker returned would flatten an edit the user had just declined to make.
        var document = new EditorDocument();
        document.SetImageSize(800, 600);
        document.BeginCropSession();

        var files = new EditorFiles(document, NoPixels);
        files.OpenedAt(@"C:\shots\capture.png");

        var result = files.PrepareSaveAs((_, _, _) => null);

        Assert.Null(result);
        Assert.True(document.IsCropSessionActive);
        Assert.Equal(@"C:\shots\capture.png", files.Path);
    }

    [Fact]
    public void SaveAsOffersTheOriginalsNameAndFolderAsTheStartingPoint()
    {
        var files = new EditorFiles(new EditorDocument(), NoPixels);
        files.OpenedAt(@"C:\shots\capture.png");

        string? offeredName = null;
        string? offeredFolder = null;
        files.PrepareSaveAs((name, folder, _) =>
        {
            (offeredName, offeredFolder) = (name, folder);
            return null;
        });

        Assert.Equal("capture_edited.png", offeredName);
        Assert.Equal(@"C:\shots", offeredFolder);
    }

    [Fact]
    public void SaveAsOffersTheSourcesOwnFormatFirstThenTheOthers()
    {
        var files = new EditorFiles(new EditorDocument(), NoPixels);
        files.OpenedAt(@"C:\shots\photo.jpg");

        string? offeredName = null;
        IReadOnlyList<ImageFormat>? offered = null;
        files.PrepareSaveAs((name, _, formats) =>
        {
            (offeredName, offered) = (name, formats);
            return null;
        });

        Assert.Equal("photo_edited.jpg", offeredName);
        Assert.Equal([ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.Bmp], offered);
    }

    [Fact]
    public void ASecondShareLeavesTheFirstSharesFileInPlace()
    {
        var document = new EditorDocument();
        document.SetImageSize(1, 1);
        using var request = new ExportRequest(document, NoPixels(), @"C:\shots\capture.png", @"C:\shots\capture.png", 0);

        var first = request.SaveShareCopy();
        var second = request.SaveShareCopy();
        try
        {
            Assert.NotEqual(first, second);
            Assert.True(File.Exists(first));
            Assert.Equal("capture.png", Path.GetFileName(second));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(first)!, recursive: true);
            Directory.Delete(Path.GetDirectoryName(second)!, recursive: true);
        }
    }

    private static DecodedImage NoPixels() => DecodedImage.Allocate(1, 1);
}

using NexusShot.Core;
using NexusShot.Views;

namespace NexusShot.Tests;

/// <summary>
/// The destination an editor writes to. Save, Save As and Copy themselves need a GPU and a file
/// dialog, so those stay with the native audit; what is tested here is the path ownership that used
/// to be a mutable field on the window class.
/// </summary>
public class EditorFilesTests
{
    [Fact]
    public void TheDestinationIsTheFileTheEditorWasOpenedOn()
    {
        var files = new EditorFiles(new EditorDocument());
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
        var files = new EditorFiles(new EditorDocument());
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

        var files = new EditorFiles(document);
        files.OpenedAt(@"C:\shots\capture.png");

        var result = files.SaveAs((_, _) => null);

        Assert.Null(result);
        Assert.True(document.IsCropSessionActive);
        Assert.Equal(@"C:\shots\capture.png", files.Path);
    }

    [Fact]
    public void SaveAsOffersTheOriginalsNameAndFolderAsTheStartingPoint()
    {
        var files = new EditorFiles(new EditorDocument());
        files.OpenedAt(@"C:\shots\capture.png");

        string? offeredName = null;
        string? offeredFolder = null;
        files.SaveAs((name, folder) =>
        {
            (offeredName, offeredFolder) = (name, folder);
            return null;
        });

        Assert.Equal("capture_edited.png", offeredName);
        Assert.Equal(@"C:\shots", offeredFolder);
    }
}

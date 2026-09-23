using NexusShot.Core;

namespace NexusShot.Tests;

public class ImageFilesTests
{
    [Theory]
    [InlineData(@"C:\a\shot.png", true)]
    [InlineData(@"C:\a\photo.JPG", true)]
    [InlineData(@"C:\a\photo.jpeg", true)]
    [InlineData(@"C:\a\scan.bmp", true)]
    [InlineData(@"C:\a\notes.txt", false)]
    [InlineData(@"C:\a\folder", false)]
    public void OpensOnlyImagesTheEditorReads(string path, bool expected) =>
        Assert.Equal(expected, ImageFiles.CanOpen(path));

    [Theory]
    [InlineData(@"C:\a\shot.PNG", ImageFormat.Png)]
    [InlineData(@"C:\a\photo.jpg", ImageFormat.Jpeg)]
    [InlineData(@"C:\a\photo.JPEG", ImageFormat.Jpeg)]
    [InlineData(@"C:\a\scan.bmp", ImageFormat.Bmp)]
    [InlineData(@"C:\a\temp.tmp", ImageFormat.Png)]
    public void SavesBackInTheFormatThePathNames(string path, ImageFormat expected) =>
        Assert.Equal(expected, ImageFiles.FormatOf(path));
}

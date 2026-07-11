using Microsoft.UI.Xaml.Media.Imaging;

namespace NexusShot.App.Helpers;

/// <summary>
/// Loads images from arbitrary filesystem paths.
/// A <see cref="BitmapImage"/> constructed from a <c>file://</c> URI does not decode in an
/// unpackaged WinUI 3 app, so every load streams the bytes in explicitly instead.
/// </summary>
public static class ImageLoader
{
    /// <param name="decodePixelWidth">Decode at thumbnail size to avoid paging in full-resolution pixels.</param>
    public static async Task<BitmapImage> LoadAsync(string path, int decodePixelWidth = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bitmap = new BitmapImage();
        if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;

        // Copy to memory first: SetSourceAsync keeps the stream alive, and holding a
        // FileStream open would lock the screenshot against later edits or deletion.
        using var memory = new MemoryStream();
        await using (var file = File.OpenRead(path))
            await file.CopyToAsync(memory, cancellationToken);
        memory.Position = 0;

        await bitmap.SetSourceAsync(memory.AsRandomAccessStream());
        return bitmap;
    }
}

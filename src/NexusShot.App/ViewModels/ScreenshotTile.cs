using Microsoft.UI.Xaml.Media.Imaging;
using NexusShot.App.Helpers;
using NexusShot.App.Models;

namespace NexusShot.App.ViewModels;

/// <summary>
/// A history entry projected for the sidebar and the detail pane. Both bitmaps decode on demand and
/// at their display resolution, so opening the window never pages in full-size images.
/// </summary>
public sealed class ScreenshotTile(ScreenshotHistoryItem item) : ObservableObject
{
    private const int ThumbnailWidth = 220;
    private const int PreviewWidth = 1400;

    private BitmapImage? _thumbnail;
    private BitmapImage? _preview;
    private bool _isLoadingThumbnail;
    private bool _isLoadingPreview;

    public ScreenshotHistoryItem Item { get; } = item;

    public string FileName => Path.GetFileName(Item.FilePath);

    public string Dimensions => $"{Item.Width} × {Item.Height}";

    public string CapturedAt => Item.CreatedAt.LocalDateTime.ToString("MMM d, yyyy 'at' HH:mm");

    /// <summary>Short form for the sidebar row, where horizontal space is scarce.</summary>
    public string Subtitle => $"{Item.Width}×{Item.Height}  ·  {Item.CreatedAt.LocalDateTime:MMM d, HH:mm}";

    /// <summary>File size on disk, or an em dash if the file has been moved or deleted.</summary>
    public string FileSize
    {
        get
        {
            try
            {
                var bytes = new FileInfo(Item.FilePath).Length;
                return bytes >= 1024 * 1024
                    ? $"{bytes / (1024.0 * 1024.0):0.#} MB"
                    : $"{Math.Max(1, bytes / 1024)} KB";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return "—";
            }
        }
    }

    /// <summary>Small bitmap for the sidebar row.</summary>
    public BitmapImage? Thumbnail
    {
        get
        {
            if (_thumbnail is null && !_isLoadingThumbnail) _ = LoadThumbnailAsync();
            return _thumbnail;
        }
        private set
        {
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Larger bitmap for the detail pane. Decoded separately from <see cref="Thumbnail"/> rather
    /// than reused: a 220px-wide decode would be visibly soft blown up to the pane, and decoding
    /// every sidebar row at pane resolution would page in the whole history at full size.
    /// </summary>
    public BitmapImage? Preview
    {
        get
        {
            if (_preview is null && !_isLoadingPreview) _ = LoadPreviewAsync();
            return _preview;
        }
        private set
        {
            _preview = value;
            OnPropertyChanged();
        }
    }

    private async Task LoadThumbnailAsync()
    {
        _isLoadingThumbnail = true;
        try
        {
            if (File.Exists(Item.FilePath))
                Thumbnail = await ImageLoader.LoadAsync(Item.FilePath, ThumbnailWidth);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            // A missing or locked file simply shows no thumbnail.
        }
    }

    private async Task LoadPreviewAsync()
    {
        _isLoadingPreview = true;
        try
        {
            if (File.Exists(Item.FilePath))
                Preview = await ImageLoader.LoadAsync(Item.FilePath, PreviewWidth);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            // A missing or locked file simply shows no preview.
        }
    }
}

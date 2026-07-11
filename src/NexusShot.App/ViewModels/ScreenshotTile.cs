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

    private BitmapImage? _thumbnail;
    private BitmapImage? _preview;
    private bool _isLoadingThumbnail;
    private bool _isLoadingPreview;
    private int _previewGeneration;

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
        var generation = ++_previewGeneration;
        try
        {
            if (File.Exists(Item.FilePath))
            {
                // Only one detail preview is retained by MainWindow, so decode the source at its
                // real resolution. A fixed 1400px cap was enlarged on wide/high-DPI windows and
                // was the direct cause of the visibly soft main-window image.
                var preview = await ImageLoader.LoadAsync(Item.FilePath);
                if (generation == _previewGeneration) Preview = preview;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            // A missing or locked file simply shows no preview.
        }
        finally
        {
            // Must clear on every exit — including a missing file — or the tile never retries.
            _isLoadingPreview = false;
        }
    }

    /// <summary>Releases the full-resolution detail bitmap when this tile is no longer selected.
    /// A generation token prevents an in-flight decode from repopulating the cache afterward.</summary>
    public void ReleasePreview()
    {
        _previewGeneration++;
        _isLoadingPreview = false;
        Preview = null;
    }
}

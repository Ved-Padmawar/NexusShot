using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// Tile thumbnails, and the history operations that act on a capture's file.
///
/// Everything that turns a path into pixels, and everything that owns those pixels afterwards. The
/// decode cache gates what a worker is allowed to hand back; the render pass only reads the result.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// The bitmap for a tile: its thumbnail, else its preview while the thumbnail decodes, else null
    /// for a capture never decoded at all.
    ///
    /// The decode runs off the UI thread: inflating a PNG costs tens of milliseconds even when the
    /// result is tile-sized. The upload has to happen here, on the thread that owns the device, and
    /// the tile fills in on the next frame. A tile that has grown past its decode keeps drawing the
    /// smaller one until the sharper one arrives, rather than going blank.
    /// </summary>
    private ImageSurface? GetThumbnail(IComObject<ID2D1RenderTarget> target, ScreenshotHistoryItem item, int width)
    {
        var path = item.FilePath;
        if (_decoded.TryRemove(path, out var ready))
        {
            using var uploadContext = target.AsDeviceContext();
            using (ready.Pixels)
            using (ready.Preview)
            {
                if (uploadContext is not null)
                {
                    // The evicted surface owns a GPU bitmap: dropping the reference would leak it.
                    if (_thumbnails.Add(path, (ImageSurface.Upload(ready.Pixels, uploadContext), ready.Width), out var evicted))
                        evicted.Surface.Dispose();
                    if (_previews.Remove(path, out var old)) old.Dispose();
                    _previews[path] = ImageSurface.Upload(ready.Preview, uploadContext);
                }
            }
        }

        if (_thumbnails.TryGetValue(path, out var cached))
        {
            if (cached.Width < width) StartDecode(path, width);
            return cached.Surface;
        }

        StartDecode(path, width);
        return _previews.GetValueOrDefault(path);
    }

    /// <summary>
    /// A 48-pixel copy of every decoded thumbnail, never evicted (about 6 KB each), so a tile the cache
    /// dropped shows blurred rather than empty while it decodes again.
    /// </summary>
    private readonly Dictionary<string, ImageSurface> _previews = new(StringComparer.OrdinalIgnoreCase);

    private const int PreviewWidth = 48;

    /// <summary>Decoded thumbnail pixels waiting to be uploaded, keyed by file, with the width they
    /// were decoded for and their preview.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DecodedImage Pixels, int Width, DecodedImage Preview)> _decoded = new();

    /// <summary>Drops pixels for files no longer in the history. An entry is only consumed when its
    /// tile is drawn, so one deleted first would be held forever.</summary>
    public void SweepDecoded()
    {
        if (_decoded.IsEmpty) return;

        var live = _history.Select(item => item.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _decoded.Keys)
            if (!live.Contains(path) && _decoded.TryRemove(path, out var stale))
            {
                stale.Pixels.Dispose();
                stale.Preview.Dispose();
            }
    }

    /// <summary>Decodes to fill a tile <paramref name="width"/> wide at 16:10, the shape the grid crops
    /// to, so no pixel is decoded that a tile will not show.</summary>
    private void StartDecode(string path, int width)
    {
        if (!_decodes.TryStart(path)) return;
        var generation = _decodes.Generation;

        Task.Run(() =>
        {
            DecodedImage? pixels = null, preview = null;
            try
            {
                pixels = ImageSurface.DecodeScaled(path, width, width * 10 / 16, cover: true);
                var shrunk = Downsample.Box(pixels.Span, pixels.Width, pixels.Height,
                    Downsample.FactorFor(pixels.Width, PreviewWidth), out var previewWidth, out var previewHeight);
                preview = DecodedImage.CopyFrom(shrunk, previewWidth, previewHeight);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException
                or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
            {
                // A capture that will not decode simply has no thumbnail.
                Log.Error("thumbnail.decode", exception, path);
            }

            Post(() =>
            {
                // Rejected pixels are freed here: this callback is their only owner.
                if (_decodes.Finish(path, generation, preview is not null) != DecodeOutcome.Accept)
                {
                    pixels?.Dispose();
                    preview?.Dispose();
                }
                else
                {
                    if (_decoded.TryRemove(path, out var superseded))
                    {
                        superseded.Pixels.Dispose();
                        superseded.Preview.Dispose();
                    }
                    _decoded[path] = (pixels!, width, preview!);
                }

                Invalidate();
            });
        });
    }

    /// <summary>Copies the capture, and says so only if the copy actually completed.</summary>
    private bool _copying;

    private void CopyToClipboard(ScreenshotHistoryItem item) => _ = CopyToClipboardAsync(item);

    private async Task CopyToClipboardAsync(ScreenshotHistoryItem item)
    {
        if (_copying) return;
        _copying = true;
        Exception? failure = null;
        try { await MediaWorker.Run(() => { ClipboardImage.Copy(item.FilePath); return true; }); }
        catch (Exception exception) { failure = exception; }
        Post(() =>
        {
            _copying = false;
            if (failure is null)
            {
                ShowToast("Copied to clipboard");
                _copiedPath = item.FilePath;
            }
            else
            {
                Log.Error("main.copy", failure, item.FilePath);
                UserFeedback.Error(Handle, "Could not copy this image. Check that the file exists and retry.");
            }
            Invalidate();
        });
    }

    public void ForgetMissingCapture(string path)
    {
        _selection.Forget(path);
        DropCache(path);
    }

    /// <summary>Deletes captures the prompt confirmed, saves the history once, and reports failures
    /// together rather than one dialog per file.</summary>
    private void DeleteCaptures(IReadOnlyList<ScreenshotHistoryItem> items)
    {
        var failed = items.Count(item => !DeleteFile(item));
        _selection.End();
        _storage.SaveHistory(_history);

        var deleted = items.Count - failed;
        if (deleted > 0) ShowToast(deleted == 1 ? "Deleted 1 capture" : $"Deleted {deleted} captures");
        if (failed > 0)
            UserFeedback.Error(Handle, failed == 1
                ? "Could not delete 1 image. It may be in use or outside the current screenshot folder."
                : $"Could not delete {failed} images. They may be in use or outside the current screenshot folder.");
        Invalidate();
    }

    /// <summary>Removes the file and its row. False, with the failure logged, when the file could not
    /// go; the history is left for the caller to save once.</summary>
    private bool DeleteFile(ScreenshotHistoryItem item)
    {
        try
        {
            var full = Path.GetFullPath(item.FilePath);
            if (!IsUnder(full, _settings.ScreenshotFolder) && !IsUnder(full, Path.GetTempPath()))
                throw new InvalidOperationException("The image is outside the managed screenshot folder.");
            File.Delete(full);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException)
        {
            Log.Error("history.delete", exception, item.FilePath);
            return false;
        }
        _history.Remove(item);
        _selection.Forget(item.FilePath);
        DropCache(item.FilePath);
        return true;
    }

    /// <summary>Whether the path sits inside root. The trailing separator matters: without it
    /// "C:\Shots-elsewhere" prefix-matches "C:\Shots".</summary>
    private static bool IsUnder(string fullPath, string root)
    {
        try
        {
            var normalized = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return false;
        }
    }

    private void Share(ScreenshotHistoryItem item)
    {
        try { ShareSheet.Share(Handle, item.FilePath); }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException
            or InvalidOperationException)
        {
            Log.Error("history.share", exception, item.FilePath);
            UserFeedback.Error(Handle, "Could not open the share sheet. Please retry.");
        }
    }

    /// <summary>Explorer, with the file selected.</summary>
    private static void Reveal(string path) => Explore($"/select,\"{path}\"", path);

    /// <summary>Explorer, in the save folder - created first, so a fresh install opens somewhere.</summary>
    private static void OpenFolder(string folder)
    {
        try { Directory.CreateDirectory(folder); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Error("history.folder", exception, folder);
        }
        Explore($"\"{folder}\"", folder);
    }

    private static void Explore(string arguments, string subject)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is IOException
            or System.ComponentModel.Win32Exception)
        {
            Log.Error("history.reveal", exception, subject);
        }
    }
}

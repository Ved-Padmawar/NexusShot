using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// Thumbnails, the detail preview, and the history operations that act on a capture's file.
///
/// Everything that turns a path into pixels, and everything that owns those pixels afterwards. The
/// decode caches gate what a worker is allowed to hand back; the render passes only read the result.
/// </summary>
public sealed partial class MainWindow
{
    private void DrawThumbnail(
        Ui ui, IComObject<ID2D1RenderTarget> target, ScreenshotHistoryItem item, Rect slot)
    {
        var bitmap = GetThumbnail(target, item);
        if (bitmap is null) return;

        // Aspect-fill, clipped to the chip. A letterboxed thumbnail in a 52x34 cell is mostly empty
        // background; filling it makes the row scannable, which is the whole job of a thumbnail.
        var fit = slot.Cover(new Size(bitmap.Width, bitmap.Height));

        target.Object.PushAxisAlignedClip(
            AnnotationRenderer.ToRect(slot), D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
        target.DrawBitmap(
            bitmap.Bitmap, 1f,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
            AnnotationRenderer.ToRect(fit));
        target.Object.PopAxisAlignedClip();
    }

    /// <summary>
    /// The bitmap for a row's thumbnail, or null while it is still being decoded.
    ///
    /// The decode runs off the UI thread: inflating a PNG costs tens of milliseconds even when the
    /// result is a 52x34 chip. The upload has to happen here, on the thread that owns the device, and
    /// the row fills in on the next frame.
    /// </summary>
    private ImageSurface? GetThumbnail(IComObject<ID2D1RenderTarget> target, ScreenshotHistoryItem item)
    {
        if (_thumbnails.TryGetValue(item.FilePath, out var cached)) return cached;

        // Decoded and waiting: upload it now that we are on the thread that owns the device.
        if (_decoded.TryRemove(item.FilePath, out var pixels))
        {
            using var uploadContext = target.AsDeviceContext();
            if (uploadContext is null) { pixels.Dispose(); return null; }

            // The pixels exist only to reach the GPU; the surface is what the cache keeps.
            using (pixels)
            {
                var surface = ImageSurface.Upload(pixels, uploadContext);

                // The evicted surface owns a GPU bitmap: dropping the reference would leak it.
                if (_thumbnails.Add(item.FilePath, surface, out var evicted)) evicted?.Dispose();
                return surface;
            }
        }

        StartDecode(item.FilePath);
        return null;
    }

    /// <summary>Decoded thumbnail pixels waiting to be uploaded, keyed by file.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DecodedImage> _decoded = new();

    /// <summary>Drops pixels for files no longer in the history. An entry is only consumed when its
    /// row is drawn, so one deleted first would be held forever.</summary>
    public void SweepDecoded()
    {
        if (_decoded.IsEmpty) return;

        var live = _history.Select(item => item.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _decoded.Keys)
            if (!live.Contains(path) && _decoded.TryRemove(path, out var pixels)) pixels.Dispose();
    }

    private void StartDecode(string path)
    {
        if (!_decodes.TryStart(path)) return;
        var generation = _decodes.Generation;

        Task.Run(() =>
        {
            DecodedImage? pixels = null;
            try
            {
                // 2x the chip, so it stays crisp on a scaled display.
                pixels = ImageSurface.DecodeScaled(path, maxWidth: 160, maxHeight: 160);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException
                or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
            {
                // A capture that will not decode simply has no thumbnail.
                Log.Error("thumbnail.decode", exception, path);
            }

            Post(() =>
            {
                // Rejected pixels are freed here: this callback is their only owner, so returning
                // without disposing would strand a decode's worth of memory per stale repaint.
                if (_decodes.Finish(path, generation, pixels is not null) != DecodeOutcome.Accept)
                {
                    pixels?.Dispose();
                }
                else if (_decoded.TryGetValue(path, out var superseded) && !ReferenceEquals(superseded, pixels))
                {
                    _decoded[path] = pixels!;
                    superseded.Dispose();
                }
                else _decoded[path] = pixels!;

                Invalidate();
            });
        });
    }

    /// <summary>
    /// One detail decode at a time. Completed pixels are admitted on the UI thread only if their
    /// cache generation and selection still match.
    /// </summary>
    private bool _previewLoading;

    /// <summary>Pixels decoded off-thread, waiting for a render to upload them.</summary>
    private (string Path, DecodedImage Image, (int Width, int Height) Size)? _pendingPreview;

    /// <summary>
    /// The display-sized bitmap for the detail preview, decoded on a worker.
    ///
    /// The upload needs a live device, so it happens here - inside a render call - rather than from
    /// the worker's completion: a render target handed to one frame can be resized or disposed
    /// before an asynchronous callback would run.
    /// </summary>
    private ImageSurface? GetPreviewBitmap(IComObject<ID2D1RenderTarget> target, ScreenshotHistoryItem item, Rect well)
    {
        var path = item.FilePath;
        // Round up to avoid restarting a decode for every pixel of a window resize. These are
        // physical pixels, so this remains sharp on a high-DPI monitor without storing a 4K image
        // behind an 800-pixel preview. Editors and exports still use the original file.
        var size = (Width: Math.Max(256, (int)Math.Ceiling(well.Width / 256) * 256),
            Height: Math.Max(256, (int)Math.Ceiling(well.Height / 256) * 256));

        if (_pendingPreview is { } pending && pending.Path == path)
        {
            _pendingPreview = null;
            using (pending.Image)
            {
                using var context = target.AsDeviceContext();
                if (context is not null)
                {
                    _preview?.Dispose();
                    _preview = ImageSurface.Upload(pending.Image, context);
                    _previewPath = path;
                    _previewSize = pending.Size;
                }
            }
        }

        if (_previewPath == path && _preview is not null
            && _previewSize.Width >= size.Width && _previewSize.Height >= size.Height) return _preview;
        if (_previewLoading || !File.Exists(path) || !_previewDecodes.TryStart(path)) return null;

        _previewLoading = true;
        var generation = _previewDecodes.Generation;
        _ = Task.Run(() =>
        {
            DecodedImage? decoded = null;
            try
            {
                decoded = ImageSurface.DecodeScaled(path, size.Width, size.Height);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException
                or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
            {
                // A capture that will not decode shows the empty preview rather than failing the frame.
                Log.Error("preview.decode", exception, path);
            }

            Post(() =>
            {
                _previewLoading = false;

                // The selection is part of this decode's world: pixels for a capture the user has
                // already moved off are as stale as pixels from an older generation.
                var outcome = _previewDecodes.Finish(path, generation, decoded is not null,
                    stillWanted: _selected?.FilePath == path);

                if (outcome != DecodeOutcome.Accept) decoded?.Dispose();
                else
                {
                    // A pending decode that was never drawn still owns its pixels.
                    DropPendingPreview();
                    _pendingPreview = (path, decoded!, size);
                }
                Invalidate();
            });
        });
        return null;
    }

    /// <summary>Frees a decode that arrived but was never uploaded. The pending slot owns its
    /// pixels, so every path that clears it goes through here.</summary>
    private void DropPendingPreview()
    {
        _pendingPreview?.Image.Dispose();
        _pendingPreview = null;
    }

    /// <summary>Closes the capture back to the empty state, releasing its full-resolution bitmap.</summary>
    private readonly ConfirmFeedback _copied = new();

    /// <summary>Copies the capture, and ticks the button only if the copy actually completed.</summary>
    private void CopyToClipboard(ScreenshotHistoryItem item)
    {
        try
        {
            ClipboardImage.Copy(item.FilePath);
            _copied.Start(Environment.TickCount64);
            WindowInterop.SetTimer(Handle, CopyFeedbackTimerId, 16, IntPtr.Zero);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            StopCopyFeedback();
            Log.Error("main.copy", exception, item.FilePath);
        }
        Invalidate();
    }

    private void StepCopyFeedback()
    {
        if (_copied.NextFrameDelay(Environment.TickCount64) is { } delay)
            WindowInterop.SetTimer(Handle, CopyFeedbackTimerId, delay, IntPtr.Zero);
        else
            WindowInterop.KillTimer(Handle, CopyFeedbackTimerId);

        Invalidate();
    }

    private void StopCopyFeedback()
    {
        _copied.Stop();
        WindowInterop.KillTimer(Handle, CopyFeedbackTimerId);
    }

    private void Deselect()
    {
        _selected = null;
        DropPendingPreview();

        _preview?.Dispose();
        _preview = null;
        _previewPath = null;

        Invalidate();
    }

    private void Delete(ScreenshotHistoryItem item)
    {
        _history.Remove(item);

        // Deleting what you were looking at lands on the empty state, rather than decoding whichever
        // capture happens to be next.
        if (ReferenceEquals(_selected, item)) Deselect();

        DropCache(item.FilePath);

        // history.json is editable on disk, so its paths are not trusted. Delete only under a root
        // we own: the screenshot folder, or temp for a capture that was never saved.
        try
        {
            var full = Path.GetFullPath(item.FilePath);
            if (IsUnder(full, _settings.ScreenshotFolder) || IsUnder(full, Path.GetTempPath()))
                File.Delete(full);
            else
                Log.Error("history.delete_outside_root", new InvalidOperationException(full));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The file may be open elsewhere; the history entry still goes.
        }

        _storage.SaveHistory(_history);
        Invalidate();
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

    private static void Reveal(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is IOException
            or System.ComponentModel.Win32Exception)
        {
            // Explorer not opening is not worth taking the app down for.
        }
    }

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..(limit - 1)] + "…";

    private static string Ago(DateTimeOffset when)
    {
        var elapsed = DateTimeOffset.Now - when;
        if (elapsed.TotalMinutes < 1) return "just now";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays}d ago";
        return when.LocalDateTime.ToString("d MMM");
    }
}

using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot.Platform;

/// <summary>Writes pixels directly to their final folder. A partial encode is never admitted
/// to history or observed as a PNG by the folder watcher.</summary>
internal static class CaptureStore
{
    public static ScreenshotHistoryItem Save(DecodedImage pixels, string folder, ImageFormat format, string? app = null)
    {
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".nexusshot-{Guid.NewGuid():N}.tmp");
        try
        {
            ImageWriter.Write(temporary, pixels, format);
            return Commit(temporary, folder, pixels.Width, pixels.Height, ImageFiles.ExtensionOf(format), app);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Log.Error("capture.temp_cleanup", exception); }
        }
    }

    internal static ScreenshotHistoryItem Commit(string temporary, string folder, int width, int height, string extension, string? app = null)
    {
        var captured = DateTimeOffset.Now;
        var name = CaptureName.For(captured.LocalDateTime, app);
        for (var suffix = 0; ; suffix++)
        {
            var destination = Path.Combine(folder,
                name + (suffix == 0 ? "" : $"_{suffix:D3}") + extension);
            try
            {
                File.Move(temporary, destination, overwrite: false);
                return new ScreenshotHistoryItem
                {
                    FilePath = destination, CapturedAt = captured,
                    Width = width, Height = height,
                };
            }
            catch (IOException) when (File.Exists(destination)) { }
        }
    }
}

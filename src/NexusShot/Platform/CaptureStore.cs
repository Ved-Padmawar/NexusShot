using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot.Platform;

/// <summary>Writes pixels directly to their final folder. A partial encode is never admitted
/// to history or observed as a PNG by the folder watcher.</summary>
internal static class CaptureStore
{
    public static ScreenshotHistoryItem Save(DecodedImage pixels, string folder, bool autoSave)
    {
        var directory = autoSave ? folder : Path.GetTempPath();
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".nexusshot-{Guid.NewGuid():N}.tmp");
        try
        {
            PngWriter.Write(temporary, pixels);
            var captured = DateTimeOffset.Now;
            var name = autoSave ? CaptureName.For(captured.LocalDateTime) : $"NexusShot_{Guid.NewGuid():N}";
            for (var suffix = 0; ; suffix++)
            {
                var destination = Path.Combine(directory, name + (suffix == 0 ? "" : $"_{suffix:D3}") + ".png");
                try
                {
                    File.Move(temporary, destination, overwrite: false);
                    return new ScreenshotHistoryItem
                    {
                        FilePath = destination, CapturedAt = captured,
                        Width = pixels.Width, Height = pixels.Height,
                    };
                }
                catch (IOException) when (File.Exists(destination)) { }
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Log.Error("capture.temp_cleanup", exception); }
        }
    }
}

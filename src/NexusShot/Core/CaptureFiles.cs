namespace NexusShot.Core;

/// <summary>Library media includes recordings; the image editor still accepts only still images.</summary>
public static class CaptureFiles
{
    public static readonly string[] Extensions = [.. ImageFiles.Extensions, ".gif", ".mp4"];

    public static bool CanOpen(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsRecording(string path) => Path.GetExtension(path).ToLowerInvariant() is ".gif" or ".mp4";

    public static bool IsVideo(string path) => string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase);
}

using NexusShot.App.Enums;

namespace NexusShot.App.Models;

/// <summary>Metadata-only record persisted in the local history JSON file.</summary>
public sealed class ScreenshotHistoryItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public int Width { get; set; }
    public int Height { get; set; }
    public CaptureMode CaptureMode { get; set; }
    public string? EditedFilePath { get; set; }
    public bool IsPinned { get; set; }
}

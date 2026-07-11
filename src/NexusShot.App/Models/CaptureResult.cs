using NexusShot.App.Enums;

namespace NexusShot.App.Models;

public sealed class CaptureResult
{
    public required string TemporaryFilePath { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required CaptureMode Mode { get; init; }
    /// <summary>The physical desktop coordinates represented by this image.</summary>
    public required Windows.Graphics.RectInt32 SourceBounds { get; init; }
}

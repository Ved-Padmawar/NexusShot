using NexusShot.App.Models;
using NexusShot.App.Enums;
namespace NexusShot.App.Services;
public interface IScreenshotCaptureService
{
    Task<CaptureResult> CaptureFullScreenAsync(CancellationToken cancellationToken);
    Task<CaptureResult> CaptureRegionAsync(Windows.Graphics.RectInt32 region, CancellationToken cancellationToken);
    Task<CaptureResult> CaptureActiveWindowAsync(CancellationToken cancellationToken);
}
public interface IStorageService
{
    Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken);
    Task<IReadOnlyList<ScreenshotHistoryItem>> LoadHistoryAsync(CancellationToken cancellationToken);
    Task SaveHistoryAsync(IReadOnlyList<ScreenshotHistoryItem> history, CancellationToken cancellationToken);
    /// <summary>Moves a captured temp file into <paramref name="destinationFolder"/> under a timestamped name.</summary>
    Task<string> SaveScreenshotAsync(string sourcePath, string destinationFolder, CancellationToken cancellationToken);
}

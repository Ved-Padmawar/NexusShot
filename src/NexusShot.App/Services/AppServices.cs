using NexusShot.App.Capture;
using NexusShot.App.Editor;
using NexusShot.App.Models;

namespace NexusShot.App.Services;

/// <summary>Composition root. Constructed once at launch and passed down explicitly.</summary>
public sealed class AppServices
{
    public IStorageService Storage { get; private set; } = null!;
    public IScreenshotCaptureService Capture { get; private set; } = null!;
    public IClipboardService Clipboard { get; private set; } = null!;
    public IFloatingPreviewService Previews { get; private set; } = null!;
    public IAnnotationFlattener Flattener { get; private set; } = null!;
    public IAppLogger Logger { get; private set; } = null!;
    public ThemeService Theme { get; } = new();
    public AppSettings Settings { get; private set; } = null!;

    /// <summary>Raised (on the UI thread) after the editor rewrites a capture's file, so the
    /// library can refresh thumbnails and metadata.</summary>
    public event EventHandler<ScreenshotHistoryItem>? ScreenshotUpdated;

    public void NotifyScreenshotUpdated(ScreenshotHistoryItem item) =>
        ScreenshotUpdated?.Invoke(this, item);

    public void Configure(IStorageService storage, AppSettings settings, IAppLogger logger)
    {
        Storage = storage;
        Settings = settings;
        Logger = logger;
        Capture = new GdiScreenshotCaptureService();
        Clipboard = new ClipboardService();
        Flattener = new AnnotationFlattener();
        Previews = new FloatingPreviewService(this);
    }
}

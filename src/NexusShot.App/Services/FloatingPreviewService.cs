using NexusShot.App.Models;
using NexusShot.App.Views;

namespace NexusShot.App.Services;

public interface IFloatingPreviewService
{
    Task ShowPreviewAsync(ScreenshotHistoryItem screenshot, CancellationToken cancellationToken);
    Task RefreshPreviewAsync(ScreenshotHistoryItem screenshot, CancellationToken cancellationToken);

    /// <summary>Closes every open preview, so application shutdown is not blocked by them.</summary>
    void CloseAll();
}

/// <summary>
/// Owns the Quick Access Overlay stack. The newest card sits at the bottom-left anchor and
/// older cards are pushed upward; the stack re-flows whenever a card is dismissed.
/// </summary>
public sealed class FloatingPreviewService(AppServices services) : IFloatingPreviewService
{
    private const int MaximumVisiblePreviews = 5;

    /// <summary>Ordered newest-first, which matches the bottom-up visual stacking.</summary>
    private readonly List<FloatingPreviewWindow> _previews = [];

    /// <summary>
    /// The monitor the stack lives on, captured when the first card appears. Re-resolving it per
    /// reflow would scatter the stack across monitors as the cursor moves.
    /// </summary>
    private Windows.Graphics.RectInt32? _anchor;

    public Task ShowPreviewAsync(ScreenshotHistoryItem screenshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_previews.Count == 0) _anchor = Helpers.MonitorHelper.GetWorkArea();

        // Retire the oldest unpinned card once the stack would overflow the screen.
        while (_previews.Count >= MaximumVisiblePreviews)
        {
            var oldest = _previews.LastOrDefault(p => !p.IsPinned);
            if (oldest is null) break;
            oldest.Close();
            _previews.Remove(oldest);
        }

        var preview = new FloatingPreviewWindow(screenshot, services);
        preview.Closed += (_, _) =>
        {
            _previews.Remove(preview);
            if (_previews.Count == 0) _anchor = null;
            Reflow();
        };
        preview.PinnedChanged += (_, _) => Reflow();

        _previews.Insert(0, preview);
        Reflow();
        preview.ShowWithoutActivating();
        return Task.CompletedTask;
    }

    public async Task RefreshPreviewAsync(ScreenshotHistoryItem screenshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preview = _previews.FirstOrDefault(p => p.ItemId == screenshot.Id);
        if (preview is null)
        {
            await ShowPreviewAsync(screenshot, cancellationToken);
            return;
        }

        await preview.RefreshAsync(screenshot, cancellationToken);
        Reflow();
    }

    public void CloseAll()
    {
        // Copy first: closing raises Closed, which mutates _previews.
        foreach (var preview in _previews.ToList()) preview.Close();
        _previews.Clear();
        _anchor = null;
    }

    private void Reflow()
    {
        if (_anchor is not { } workArea) return;
        // Cards have per-capture heights, so the stack accumulates each card's real extent.
        var offset = 0;
        foreach (var preview in _previews)
        {
            preview.MoveToStackPosition(workArea, offset);
            offset += preview.StackExtent;
        }
    }
}

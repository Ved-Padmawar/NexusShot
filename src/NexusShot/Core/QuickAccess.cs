namespace NexusShot.Core;

/// <summary>One capture in the quick-access stack.</summary>
public sealed class QuickAccessCard
{
    internal QuickAccessCard(ScreenshotHistoryItem item) => Item = item;

    public ScreenshotHistoryItem Item { get; internal set; }
    public bool IsPinned { get; internal set; }

    /// <summary>Seconds left before the card closes itself.</summary>
    internal int Remaining { get; set; }
}

/// <summary>
/// The quick-access cards: which are up (newest first), pinned, counting down, and recently closed.
/// A card closed for any reason - timer, close button, drop, overflow - lands in the same restore list.
/// </summary>
public sealed class QuickAccess(AppSettings settings)
{
    /// <summary>More than any strip shows; older ones are in the main window's history.</summary>
    public const int ClosedCapacity = 24;

    private readonly List<QuickAccessCard> _open = [];
    private readonly List<ScreenshotHistoryItem> _closed = [];

    /// <summary>Newest first.</summary>
    public IReadOnlyList<QuickAccessCard> Open => _open;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ScreenshotHistoryItem> RecentlyClosed => _closed;

    public QuickAccessCard? Find(string path) =>
        _open.FirstOrDefault(card => SamePath(card.Item.FilePath, path));

    /// <summary>Puts a capture on the stack, or updates it in place if already up. Also how a
    /// closed capture is restored.</summary>
    public QuickAccessCard Show(ScreenshotHistoryItem item)
    {
        _closed.RemoveAll(closed => SamePath(closed.FilePath, item.FilePath));

        if (Find(item.FilePath) is { } existing)
        {
            existing.Item = item;
            ResetCountdown(existing);
            return existing;
        }

        var card = new QuickAccessCard(item);
        ResetCountdown(card);
        _open.Insert(0, card);
        return card;
    }

    public void Close(QuickAccessCard card)
    {
        if (!_open.Remove(card)) return;

        _closed.RemoveAll(closed => SamePath(closed.FilePath, card.Item.FilePath));
        _closed.Insert(0, card.Item);
        if (_closed.Count > ClosedCapacity) _closed.RemoveAt(_closed.Count - 1);
    }

    /// <summary>Drops a capture whose file is gone from the restore list.</summary>
    public void Forget(string path) => _closed.RemoveAll(closed => SamePath(closed.FilePath, path));

    public void TogglePin(QuickAccessCard card)
    {
        card.IsPinned = !card.IsPinned;
        ResetCountdown(card);
    }

    public void ResetCountdown(QuickAccessCard card) => card.Remaining = settings.PreviewDismissSeconds;

    /// <summary>
    /// Counts down one second; true when the card is due to close. <paramref name="held"/> (hover,
    /// drag, an open dialog) restarts the count, so a card never closes under the user's hand.
    /// </summary>
    public bool Tick(QuickAccessCard card, bool held)
    {
        if (settings.PreviewDismissSeconds <= 0) return false;

        if (held || card.IsPinned)
        {
            ResetCountdown(card);
            return false;
        }

        return --card.Remaining <= 0;
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

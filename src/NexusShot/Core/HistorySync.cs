namespace NexusShot.Core;

/// <summary>A file's identity for change detection: what a rescan compares to skip unchanged files.</summary>
public readonly record struct FileVersion(long Length, DateTime LastWriteUtc)
{
    public static FileVersion Read(string path)
    {
        var file = new FileInfo(path);
        return new(file.Length, file.LastWriteTimeUtc);
    }
}

/// <summary>What a worker found in the save folder. <paramref name="Unreadable"/> are images that
/// failed to decode, with the version that failed, so they are skipped until they change rather
/// than retried on every rescan.</summary>
public sealed record HistoryScan(
    IReadOnlyList<(ScreenshotHistoryItem Item, FileVersion Version)> Changed,
    IReadOnlyList<string> Missing,
    IReadOnlyList<(string Path, FileVersion Version)> Unreadable);

/// <summary>What applying a scan changed, for the view to refresh. <paramref name="ScanAgain"/> means
/// a request arrived, or the world moved, while the scan ran.</summary>
public sealed record HistorySyncResult(
    IReadOnlyList<string> Removed,
    IReadOnlyList<ScreenshotHistoryItem> Refreshed,
    bool Changed,
    bool ScanAgain);

/// <summary>
/// Keeps the history in step with the save folder: one scan at a time, and a scan's findings applied
/// to the list as it is now, not as it was when the scan started. Deletes and renames made in
/// Explorer drop out; images that appeared there are adopted.
///
/// UI-thread only. The file checks are passed in, so the rules run without a disk.
/// </summary>
public sealed class HistorySync
{
    private readonly Dictionary<string, FileVersion> _versions = new(StringComparer.OrdinalIgnoreCase);
    private bool _running;
    private bool _queued;

    /// <summary>Claims the one scan slot. False while a scan runs; the request is then remembered and
    /// answered by <see cref="HistorySyncResult.ScanAgain"/> when that scan completes.</summary>
    public bool TryBegin()
    {
        if (_running)
        {
            _queued = true;
            return false;
        }
        _running = true;
        return true;
    }

    /// <summary>The versions a scan starting now compares against; a copy, since it runs on a worker.</summary>
    public Dictionary<string, FileVersion> Versions() => new(_versions, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ends the running scan and applies <paramref name="scan"/> to <paramref name="history"/>, which it
    /// keeps sorted newest first. <paramref name="stale"/> - the folder changed since the scan began -
    /// discards the result for a fresh scan. The file checks are repeated here because a capture or
    /// save may have recreated or rewritten a path after the worker looked.
    /// </summary>
    public HistorySyncResult Complete(List<ScreenshotHistoryItem> history, HistoryScan? scan, bool stale,
        Func<string, bool> exists, Func<string, FileVersion> versionOf)
    {
        _running = false;
        List<string> removed = [];
        List<ScreenshotHistoryItem> refreshed = [];
        var changed = false;

        if (stale) _queued = true;
        else if (scan is not null)
        {
            foreach (var path in scan.Missing)
            {
                if (exists(path)) continue;
                if (history.RemoveAll(item => string.Equals(item.FilePath, path, StringComparison.OrdinalIgnoreCase)) != 0)
                {
                    changed = true;
                    removed.Add(path);
                }
                _versions.Remove(path);
            }
            foreach (var (path, version) in scan.Unreadable) _versions[path] = version;

            var live = history.ToDictionary(item => item.FilePath, StringComparer.OrdinalIgnoreCase);
            foreach (var (candidate, version) in scan.Changed)
            {
                // Replaced since the scan: the watcher reports the replacement, so rescan for it.
                try
                {
                    if (versionOf(candidate.FilePath) != version) { _queued = true; continue; }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { continue; }

                _versions[candidate.FilePath] = version;
                if (live.TryGetValue(candidate.FilePath, out var existing))
                {
                    existing.Width = candidate.Width;
                    existing.Height = candidate.Height;
                    refreshed.Add(existing);
                }
                else history.Add(candidate);
                changed = true;
            }
            if (changed) history.Sort((a, b) => b.CapturedAt.CompareTo(a.CapturedAt));
        }

        var again = _queued;
        _queued = false;
        return new(removed, refreshed, changed, again);
    }
}

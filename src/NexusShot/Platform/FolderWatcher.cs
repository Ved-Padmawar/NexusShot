namespace NexusShot.Platform;

/// <summary>
/// Watches the save folder, so Explorer and the app agree on what exists.
///
/// Events are coalesced onto a short timer rather than acted on individually: a single save can
/// raise Created plus two Changed, and a rename arrives as Deleted-then-Created. Reacting to each
/// one would rebuild the history several times for one user action.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private readonly Action _changed;

    public FolderWatcher(string folder, Action changed)
    {
        _changed = changed;

        Directory.CreateDirectory(folder);

        _debounce = new Timer(_ => _changed(), null, Timeout.Infinite, Timeout.Infinite);

        _watcher = new FileSystemWatcher(folder, "*.png")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };

        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;
        _watcher.Changed += OnChanged;
    }

    private void OnChanged(object? sender, FileSystemEventArgs e) =>
        _debounce.Change(300, Timeout.Infinite);

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _debounce.Dispose();
    }
}

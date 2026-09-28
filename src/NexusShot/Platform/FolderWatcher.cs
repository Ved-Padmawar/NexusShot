using NexusShot.Core;

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
    private readonly Timer _retry;
    private readonly Action _changed;
    private volatile bool _disposed;

    public FolderWatcher(string folder, Action changed)
    {
        _changed = changed;

        Directory.CreateDirectory(folder);

        _debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        _retry = new Timer(_ => Rearm(), null, Timeout.Infinite, Timeout.Infinite);

        // Every format the app writes and opens, or a JPEG or BMP deleted in Explorer stays listed.
        _watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        foreach (var extension in ImageFiles.Extensions) _watcher.Filters.Add("*" + extension);
        _watcher.EnableRaisingEvents = true;

        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Changed += OnChanged;
        _watcher.Error += OnError;
    }

    private void OnChanged(object? sender, FileSystemEventArgs e) => Schedule();

    /// <summary>Renamed carries its own delegate type. Subscribed as itself rather than relying on
    /// a conversion, so Dispose can actually detach it - a converted handler is a different
    /// delegate instance, and -= would silently not match.</summary>
    private void OnRenamed(object? sender, RenamedEventArgs e) => Schedule();

    /// <summary>
    /// The watcher's internal buffer overflowed, or the handle was lost: the events it dropped are
    /// gone, so the folder is rescanned rather than trusted to keep arriving.
    /// </summary>
    private void OnError(object? sender, ErrorEventArgs e) => Rearm();

    /// <summary>Restarts the watcher and rescans. A network or removable folder can be gone for a
    /// while, so a failed restart tries again every few seconds until the folder is back.</summary>
    private void Rearm()
    {
        if (_disposed) return;

        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.EnableRaisingEvents = true;
        }
        catch (ObjectDisposedException) { return; }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            try { _retry.Change(5000, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
            return;
        }

        Schedule();
    }

    /// <summary>Restarts the debounce. Events can arrive on a watcher thread while the owner is
    /// disposing, so a torn-down timer is not an error here.</summary>
    private void Schedule()
    {
        if (_disposed) return;
        try
        { _debounce.Change(300, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void Fire()
    {
        if (_disposed) return;
        _changed();
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher.EnableRaisingEvents = false;

        _watcher.Created -= OnChanged;
        _watcher.Deleted -= OnChanged;
        _watcher.Renamed -= OnRenamed;
        _watcher.Changed -= OnChanged;
        _watcher.Error -= OnError;

        _watcher.Dispose();
        _debounce.Dispose();
        _retry.Dispose();
    }
}

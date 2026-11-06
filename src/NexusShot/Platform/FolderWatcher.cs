using NexusShot.Core;

namespace NexusShot.Platform;

/// <summary>
/// Watches the save folder, so Explorer and the app agree on what exists.
///
/// Events are coalesced onto a short timer rather than acted on individually: a single save can
/// raise Created plus two Changed, and a rename arrives as Deleted-then-Created. Reacting to each
/// one would rebuild the history several times for one user action.
///
/// The folder's own name is watched in its parent as well. A watcher's handle follows the directory,
/// not the path: renamed away, the folder is still watched under its new name, and a folder created
/// at the path again would never be seen.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;

    /// <summary>Null for a drive root, which has no parent to be renamed in.</summary>
    private readonly FileSystemWatcher? _parent;
    private readonly string? _parentFolder;

    private readonly Timer _debounce;
    private readonly Timer _retry;
    private readonly string _folder;
    private readonly Action _changed;
    private readonly Lock _gate = new();
    private volatile bool _disposed;

    public FolderWatcher(string folder, Action changed)
    {
        _folder = folder;
        _changed = changed;

        try { Directory.CreateDirectory(folder); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Log.Error("watcher.folder", exception, folder); }

        _debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        _retry = new Timer(_ => Rearm(), null, Timeout.Infinite, Timeout.Infinite);

        // Every format the app writes and opens, or a JPEG or BMP deleted in Explorer stays listed.
        _watcher = new FileSystemWatcher { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        foreach (var extension in ImageFiles.Extensions) _watcher.Filters.Add("*" + extension);

        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Changed += OnChanged;
        _watcher.Error += OnError;

        var trimmed = Path.TrimEndingDirectorySeparator(folder);
        _parentFolder = Path.GetDirectoryName(trimmed);
        if (!string.IsNullOrEmpty(_parentFolder))
        {
            _parent = new FileSystemWatcher { NotifyFilter = NotifyFilters.DirectoryName, Filter = Path.GetFileName(trimmed) };
            _parent.Created += OnFolderMoved;
            _parent.Deleted += OnFolderMoved;
            _parent.Renamed += OnFolderRenamed;
        }

        TryStart();
    }

    private void OnChanged(object? sender, FileSystemEventArgs e) => Schedule();

    /// <summary>Renamed carries its own delegate type. Subscribed as itself rather than relying on
    /// a conversion, so Dispose can actually detach it - a converted handler is a different
    /// delegate instance, and -= would silently not match.</summary>
    private void OnRenamed(object? sender, RenamedEventArgs e) => Schedule();

    private void OnFolderMoved(object? sender, FileSystemEventArgs e) => Rearm();

    private void OnFolderRenamed(object? sender, RenamedEventArgs e) => Rearm();

    /// <summary>
    /// The watcher's internal buffer overflowed, or the handle was lost: the events it dropped are
    /// gone, so the folder is rescanned rather than trusted to keep arriving.
    /// </summary>
    private void OnError(object? sender, ErrorEventArgs e) => Rearm();

    /// <summary>Restarts the watcher and rescans, as events may have been missed.</summary>
    private void Rearm()
    {
        if (TryStart()) Schedule();
    }

    /// <summary>Starts watching, or restarts, reopening the folder at its path. A network or removable
    /// folder can be gone for a while, so a failed start retries every few seconds. Locked, as the
    /// retry timer and both watchers call it from their own threads.</summary>
    private bool TryStart()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            WatchParent();
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Path = _folder;
                _watcher.EnableRaisingEvents = true;
                return true;
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
            {
                _retry.Change(5000, Timeout.Infinite);
                return false;
            }
        }
    }

    /// <summary>Best effort: an unwatchable parent leaves the retry timer to notice a move.</summary>
    private void WatchParent()
    {
        if (_parent is null || _parent.EnableRaisingEvents) return;
        try
        {
            _parent.Path = _parentFolder!;
            _parent.EnableRaisingEvents = true;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException) { }
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
        lock (_gate)
        {
            _disposed = true;
            _watcher.EnableRaisingEvents = false;

            _watcher.Created -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Changed -= OnChanged;
            _watcher.Error -= OnError;
            _watcher.Dispose();

            if (_parent is not null)
            {
                _parent.EnableRaisingEvents = false;
                _parent.Created -= OnFolderMoved;
                _parent.Deleted -= OnFolderMoved;
                _parent.Renamed -= OnFolderRenamed;
                _parent.Dispose();
            }

            _debounce.Dispose();
            _retry.Dispose();
        }
    }
}

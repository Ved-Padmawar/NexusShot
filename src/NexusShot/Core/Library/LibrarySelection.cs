namespace NexusShot.Core;

/// <summary>
/// The library's multi-select. Keyed by path: a capture the editor re-saves becomes a new history
/// item for the same file, and must stay picked.
/// </summary>
public sealed class LibrarySelection
{
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Outlives an emptied selection, so unpicking the last tile does not end the mode.</summary>
    public bool Active { get; private set; }

    public int Count => _paths.Count;

    public IReadOnlyCollection<string> Paths => _paths;

    public bool Contains(string path) => _paths.Contains(path);

    public void Begin() => Active = true;

    public void Toggle(string path)
    {
        Active = true;
        if (!_paths.Remove(path)) _paths.Add(path);
    }

    /// <summary>Keeps picks already made - ones a search is hiding, too.</summary>
    public void SelectAll(IEnumerable<string> paths)
    {
        Active = true;
        _paths.UnionWith(paths);
    }

    public void Forget(string path) => _paths.Remove(path);

    public void End()
    {
        Active = false;
        _paths.Clear();
    }
}

namespace NexusShot.Core;

/// <summary>
/// The text read out of each capture, so the Library search finds a screenshot by what it says. An
/// entry is tied to the file's version and the recognition language, so a capture edited since it
/// was read, or read in another language, is read again.
/// </summary>
public sealed class TextIndex
{
    /// <summary>An empty <paramref name="Language"/> is the Windows default. Entries saved before the
    /// language was recorded deserialize with none, so they never match and are read once more.</summary>
    public sealed record Entry(FileVersion Version, string Language, string Text);

    private readonly Dictionary<string, Entry> _entries;

    public TextIndex(IDictionary<string, Entry>? entries = null) =>
        _entries = new(entries ?? new Dictionary<string, Entry>(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Changes whenever an entry does, so a view filtering by text knows to redraw.</summary>
    public long Generation { get; private set; }

    public IReadOnlyDictionary<string, Entry> Entries => _entries;

    public string? TextOf(string path) => _entries.TryGetValue(path, out var entry) ? entry.Text : null;

    public bool IsCurrent(string path, FileVersion version, string? language) =>
        _entries.TryGetValue(path, out var entry) && entry.Version == version
        && string.Equals(entry.Language, language ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>Empty text records a capture that has none, or could not be read, so it is not tried
    /// again until it changes.</summary>
    public void Set(string path, FileVersion version, string? language, string text)
    {
        _entries[path] = new Entry(version, language ?? "", text);
        Generation++;
    }

    /// <summary>Drops entries for captures no longer in <paramref name="paths"/>.</summary>
    public void Retain(IEnumerable<string> paths)
    {
        var live = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _entries.Keys.Where(path => !live.Contains(path)).ToList()) _entries.Remove(path);
    }
}

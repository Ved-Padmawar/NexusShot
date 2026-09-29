namespace NexusShot.Core;

/// <summary>
/// Which editor owns which file: one editor per path, so a second Edit of a capture raises the window
/// already open, and two editors can never write the same file. A path is owned by the editor showing
/// it and, while a save is writing, by the editor saving to it.
///
/// Keyed by path, never by editor: DirectN windows compare by handle, which is zero once destroyed.
/// </summary>
public sealed class EditorRegistry<TEditor>(Func<TEditor, string?> pendingSavePath) where TEditor : class
{
    private readonly Dictionary<string, TEditor> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<TEditor> All => _byPath.Values;

    /// <summary>The editor showing <paramref name="path"/>, or about to write it.</summary>
    public TEditor? Owner(string path) => _byPath.TryGetValue(path, out var editor)
        ? editor
        : _byPath.Values.FirstOrDefault(other => string.Equals(pendingSavePath(other), path, StringComparison.OrdinalIgnoreCase));

    public void Add(string path, TEditor editor) => _byPath.Add(path, editor);

    /// <summary>False when another editor owns <paramref name="path"/>.</summary>
    public bool CanSaveTo(TEditor editor, string path) => Owner(path) is not { } owner || ReferenceEquals(owner, editor);

    /// <summary>Follows a Save As: the editor now shows <paramref name="to"/>.</summary>
    public void Move(TEditor editor, string from, string to)
    {
        Remove(editor, from);
        _byPath[to] = editor;
    }

    public void Remove(TEditor editor, string path)
    {
        if (_byPath.TryGetValue(path, out var owner) && ReferenceEquals(owner, editor)) _byPath.Remove(path);
    }

    public void Clear() => _byPath.Clear();
}

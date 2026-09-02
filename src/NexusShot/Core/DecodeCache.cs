namespace NexusShot.Core;

/// <summary>
/// Admission control for decodes that finish on a worker thread.
///
/// A decode started for one selection can finish after the window has been hidden, the file
/// re-saved, or another capture selected, so a result is only kept if the world it was started
/// against still exists. A generation counter is the whole mechanism.
///
/// Single-threaded by contract: every method runs on the UI thread; only the pixels cross threads.
/// </summary>
public sealed class DecodeCache
{
    private readonly HashSet<string> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private long _generation;

    /// <summary>The stamp a decode starting now must carry back to be admitted.</summary>
    public long Generation => _generation;

    public int FailureCount => _failures.Count;

    public bool IsRunning(string path) => _running.Contains(path);

    public bool HasFailed(string path) => _failures.Contains(path);

    /// <summary>Claims the right to decode <paramref name="path"/>. False when one is already
    /// running or the file has already failed, so a repaint cannot start a second decode of the
    /// same file or retry a corrupt one forever.</summary>
    public bool TryStart(string path) => !_failures.Contains(path) && _running.Add(path);

    /// <summary>
    /// Reports a decode finished, and says whether its result may be used.
    ///
    /// Always call this, including for a failed decode: it clears the in-flight mark, and skipping
    /// it would block every later decode of that file.
    /// </summary>
    /// <param name="stillWanted">Part of the same question, for the detail preview, which also
    /// needs its capture to still be selected. False is stale, not a failure.</param>
    public DecodeOutcome Finish(string path, long generation, bool succeeded, bool stillWanted = true)
    {
        _running.Remove(path);

        // Stale: the world this decode was started against is gone. The result is dropped without
        // being recorded as a failure - the file may be perfectly readable.
        if (generation != _generation || !stillWanted) return DecodeOutcome.Stale;

        if (succeeded) return DecodeOutcome.Accept;

        _failures.Add(path);
        return DecodeOutcome.Failed;
    }

    /// <summary>Invalidates everything decoded so far: in-flight results are no longer admitted and
    /// failures are forgotten, so a file replaced on disk is tried again.</summary>
    public void InvalidateAll()
    {
        _generation++;
        _failures.Clear();
    }

    /// <summary>
    /// Invalidates one file, for a save that rewrote it.
    ///
    /// The generation still moves: a decode of a *different* file may already be carrying pixels
    /// read before this one changed, and there is no per-file stamp to distinguish them.
    /// </summary>
    public void Invalidate(string path)
    {
        _generation++;
        _failures.Remove(path);
    }
}

/// <summary>What a caller should do with a decode that has just finished.</summary>
public enum DecodeOutcome
{
    Accept,

    /// <summary>Discard: decoded against a state that no longer exists. Not a failure.</summary>
    Stale,

    Failed,
}

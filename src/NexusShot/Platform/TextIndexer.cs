using System.Collections.Concurrent;
using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot.Platform;

/// <summary>
/// Reads the text in captures for the Library search: one at a time, on its own thread below normal
/// priority, so neither the UI nor the media worker a copy or save waits on is held up by a backlog
/// of hundreds. Results go to <c>read</c> on this thread, with the language they were read in; the
/// caller moves them to its own.
/// </summary>
internal sealed class TextIndexer : IDisposable
{
    private readonly BlockingCollection<string> _queue = [];
    private readonly HashSet<string> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string?> _language;
    private readonly Action<string, FileVersion, string?, string> _read;

    public TextIndexer(Func<string?> language, Action<string, FileVersion, string?, string> read)
    {
        _language = language;
        _read = read;
        new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Text index" }.Start();
    }

    /// <summary>Queues a capture unless it is already waiting.</summary>
    public void Enqueue(string path)
    {
        lock (_waiting)
            if (!_waiting.Add(path)) return;
        _queue.Add(path);
    }

    private void Run()
    {
        foreach (var path in _queue.GetConsumingEnumerable())
        {
            lock (_waiting) _waiting.Remove(path);

            // Without a language nothing is recorded, so captures are read once one is installed.
            var language = _language();
            if (!TextRecognition.CanRead(language)) continue;

            FileVersion version;
            try { version = FileVersion.Read(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }

            string text;
            try
            {
                using var pixels = ImageSurface.Decode(path);
                text = string.Join('\n', TextRecognition.Recognize(pixels, language));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            {
                // Recorded as empty against this version, so a broken file is not read every rescan.
                Log.Error("text_index.read", exception, path);
                text = "";
            }
            _read(path, version, language, text);
        }
    }

    /// <summary>Stops taking work. A read in progress finishes on its background thread and is
    /// dropped with the process; it is not worth holding exit for.</summary>
    public void Dispose() => _queue.CompleteAdding();
}

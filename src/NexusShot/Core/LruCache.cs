namespace NexusShot.Core;

/// <summary>
/// A bounded cache that evicts the least recently used entry once it is over capacity. The linked
/// list keeps recency, so an eviction never scans for its oldest entry.
///
/// Evicted values are handed back to the caller rather than dropped: they own GPU resources, and
/// losing the last reference to one would leak it.
/// </summary>
public sealed class LruCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _entries = [];
    private readonly LinkedList<(TKey Key, TValue Value)> _recency = [];

    public int Count => _entries.Count;
    public IEnumerable<TValue> Values => _recency.Select(entry => entry.Value);

    /// <summary>Looks up a key, marking it as most recently used on a hit.</summary>
    public bool TryGetValue(TKey key, out TValue value)
    {
        if (!_entries.TryGetValue(key, out var node))
        {
            value = default!;
            return false;
        }

        _recency.Remove(node);
        _recency.AddFirst(node);
        value = node.Value.Value;
        return true;
    }

    /// <summary>Adds or replaces an entry. True when this insert pushed a value out - the caller
    /// disposes what comes back.</summary>
    public bool Add(TKey key, TValue value, out TValue evicted)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            _recency.Remove(existing);
            _entries.Remove(key);
            evicted = existing.Value.Value;
            Insert(key, value);
            return true;
        }

        Insert(key, value);

        if (_entries.Count <= capacity)
        {
            evicted = default!;
            return false;
        }

        var oldest = _recency.Last!;
        _recency.RemoveLast();
        _entries.Remove(oldest.Value.Key);
        evicted = oldest.Value.Value;
        return true;
    }

    /// <summary>Removes a key, handing back its value so the caller can release it.</summary>
    public bool Remove(TKey key, out TValue value)
    {
        if (!_entries.Remove(key, out var node))
        {
            value = default!;
            return false;
        }

        _recency.Remove(node);
        value = node.Value.Value;
        return true;
    }

    public void Clear()
    {
        _entries.Clear();
        _recency.Clear();
    }

    private void Insert(TKey key, TValue value)
    {
        var node = _recency.AddFirst((key, value));
        _entries[key] = node;
    }
}

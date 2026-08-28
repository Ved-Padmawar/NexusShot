using NexusShot.Core;

namespace NexusShot.Tests;

/// <summary>The bounded cache the render resources and thumbnails rely on. Evicted values hold GPU
/// resources, so they must always come back to the caller to be disposed.</summary>
public class LruCacheTests
{
    [Fact]
    public void AnEntryComesBackUntilItIsEvicted()
    {
        var cache = new LruCache<string, int>(2);
        cache.Add("a", 1, out _);

        Assert.True(cache.TryGetValue("a", out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void TheOldestEntryIsEvictedOverCapacity()
    {
        var cache = new LruCache<string, int>(2);
        cache.Add("a", 1, out _);
        cache.Add("b", 2, out _);

        Assert.True(cache.Add("c", 3, out var evicted));
        Assert.Equal(1, evicted);
        Assert.False(cache.TryGetValue("a", out _));
    }

    [Fact]
    public void ReadingAnEntryKeepsItFromBeingEvicted()
    {
        var cache = new LruCache<string, int>(2);
        cache.Add("a", 1, out _);
        cache.Add("b", 2, out _);

        // "a" is now the most recently used, so "b" is the one that goes.
        cache.TryGetValue("a", out _);
        cache.Add("c", 3, out var evicted);

        Assert.Equal(2, evicted);
        Assert.True(cache.TryGetValue("a", out _));
    }

    [Fact]
    public void ReplacingAKeyHandsBackTheOldValue()
    {
        var cache = new LruCache<string, int>(2);
        cache.Add("a", 1, out _);

        Assert.True(cache.Add("a", 9, out var replaced));
        Assert.Equal(1, replaced);
        Assert.Equal(1, cache.Count);

        cache.TryGetValue("a", out var value);
        Assert.Equal(9, value);
    }

    [Fact]
    public void RemoveHandsBackTheValueSoItCanBeReleased()
    {
        var cache = new LruCache<string, int>(2);
        cache.Add("a", 1, out _);

        Assert.True(cache.Remove("a", out var removed));
        Assert.Equal(1, removed);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void CountNeverExceedsCapacity()
    {
        var cache = new LruCache<int, int>(8);
        for (var i = 0; i < 500; i++)
            cache.Add(i, i, out _);

        Assert.Equal(8, cache.Count);
    }

    [Fact]
    public void EveryEvictedValueIsHandedBackExactlyOnce()
    {
        var cache = new LruCache<int, int>(4);
        var returned = new List<int>();

        for (var i = 0; i < 100; i++)
        {
            if (cache.Add(i, i, out var evicted))
                returned.Add(evicted);
        }

        // Nothing may be silently dropped: what went in either sits in the cache or came back.
        Assert.Equal(96, returned.Count);
        Assert.Equal(returned.Count, returned.Distinct().Count());
    }
}

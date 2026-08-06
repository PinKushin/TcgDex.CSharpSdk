namespace TcgDex.Caching;

using System.Collections.Concurrent;

/// <summary>
/// The default in-process response cache: bounded, thread-safe, and evicting the
/// least recently used entry when full.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not built on <c>IMemoryCache</c>. That would add a package
/// reference for a dictionary with an eviction policy, and it prices entries by
/// an arbitrary "size" unit; here the natural bound is a count of responses,
/// which is directly meaningful.
/// </para>
/// <para>
/// Eviction tracks last access rather than insertion, so the entries an
/// application actually reads survive — which for this API means the enumeration
/// endpoints it hits on every screen.
/// </para>
/// </remarks>
public sealed class MemoryTcgDexResponseCache : ITcgDexResponseCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _maxEntries;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a cache holding at most <paramref name="maxEntries"/> responses.</summary>
    /// <param name="maxEntries">The upper bound on stored entries.</param>
    /// <param name="timeProvider">Clock used for expiry; defaults to the system clock.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEntries"/> is less than one.</exception>
    public MemoryTcgDexResponseCache(int maxEntries = 512, TimeProvider? timeProvider = null)
    {
        Guard.NotLessThan(maxEntries, 1);

        _maxEntries = maxEntries;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The number of entries currently held.</summary>
    public int Count => _entries.Count;

    /// <inheritdoc />
    public ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(key, out var entry))
        {
            return new ValueTask<CachedResponse?>((CachedResponse?)null);
        }

        var now = _timeProvider.GetUtcNow();

        // Past its absolute lifetime the entry is dropped rather than returned:
        // an entry kept indefinitely for revalidation would pin memory forever.
        if (now >= entry.ExpiresAt)
        {
            _entries.TryRemove(key, out _);
            return new ValueTask<CachedResponse?>((CachedResponse?)null);
        }

        entry.Touch(now);

        return new ValueTask<CachedResponse?>(entry.Response);
    }

    /// <inheritdoc />
    public ValueTask SetAsync(
        string key,
        CachedResponse response,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(key);
        Guard.NotNull(response);
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();

        // Retained well past its freshness window so the ETag stays available:
        // a stale-but-present entry turns a re-download into a 304.
        // FromTicks rather than `timeToLive * multiplier`: the TimeSpan
        // multiplication operator is .NET Core 3.0+ and this also builds for
        // netstandard2.0. Same arithmetic.
        var absoluteLifetime = timeToLive > TimeSpan.Zero
            ? TimeSpan.FromTicks(timeToLive.Ticks * RevalidationLifetimeMultiplier)
            : TimeSpan.FromMinutes(1);

        _entries[key] = new Entry(response, now + absoluteLifetime, now);

        if (_entries.Count > _maxEntries)
        {
            EvictLeastRecentlyUsed();
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        _entries.TryRemove(key, out _);

        return default;
    }

    /// <summary>Empties the cache.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>
    /// How much longer than its freshness window an entry is retained so that its
    /// ETag remains usable for revalidation.
    /// </summary>
    private const int RevalidationLifetimeMultiplier = 12;

    private void EvictLeastRecentlyUsed()
    {
        // Sampling rather than a full ordered scan: with a bound in the hundreds
        // this runs only on overflow, and an exact LRU would need a lock across
        // every read.
        var oldestKey = (string?)null;
        var oldestAccess = DateTimeOffset.MaxValue;

        foreach (var pair in _entries)
        {
            var accessed = pair.Value.LastAccessedAt;

            if (accessed < oldestAccess)
            {
                oldestAccess = accessed;
                oldestKey = pair.Key;
            }
        }

        if (oldestKey is not null)
        {
            _entries.TryRemove(oldestKey, out _);
        }
    }

    /// <summary>
    /// A stored response with its bookkeeping. <see cref="LastAccessedAt"/> is
    /// written without synchronisation because a lost update only costs slightly
    /// worse eviction ordering, never correctness.
    /// </summary>
    private sealed class Entry(CachedResponse response, DateTimeOffset expiresAt, DateTimeOffset accessedAt)
    {
        internal CachedResponse Response { get; } = response;

        internal DateTimeOffset ExpiresAt { get; } = expiresAt;

        internal DateTimeOffset LastAccessedAt { get; private set; } = accessedAt;

        internal void Touch(DateTimeOffset now) => LastAccessedAt = now;
    }
}

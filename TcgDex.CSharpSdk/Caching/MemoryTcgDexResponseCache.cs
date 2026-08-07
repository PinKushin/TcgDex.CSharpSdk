namespace TcgDex.Caching;

/// <summary>
/// The default in-process response cache: bounded, thread-safe, and evicting the
/// least recently used entries when full.
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
/// endpoints it hits on every screen. The bounding and eviction policy itself
/// lives in <see cref="BoundedLru{TKey, TValue}"/>, shared with the
/// deserialized-response cache; what this type adds is the absolute lifetime.
/// </para>
/// </remarks>
public sealed class MemoryTcgDexResponseCache : ITcgDexResponseCache
{
    /// <summary>
    /// The stored value is a struct so it lives inside the store's own entry
    /// rather than in a second heap object — one allocation per write instead of
    /// two, which is what the shared store cost before this was noticed.
    /// </summary>
    private readonly BoundedLru<string, (CachedResponse Response, DateTimeOffset ExpiresAt)> _entries;

    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a cache holding at most <paramref name="maxEntries"/> responses.</summary>
    /// <param name="maxEntries">The upper bound on stored entries.</param>
    /// <param name="timeProvider">Clock used for expiry; defaults to the system clock.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEntries"/> is less than one.</exception>
    public MemoryTcgDexResponseCache(int maxEntries = 512, TimeProvider? timeProvider = null)
    {
        Guard.NotLessThan(maxEntries, 1);

        _timeProvider = timeProvider ?? TimeProvider.System;
        _entries = new BoundedLru<string, (CachedResponse, DateTimeOffset)>(maxEntries);
    }

    /// <summary>The number of entries currently held.</summary>
    public int Count => _entries.Count;

    /// <inheritdoc />
    public ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        // A miss returns here. It would also return null by falling through to
        // the expiry check below, because the stored value is a struct and a
        // default DateTimeOffset is always in the past — so mutation testing
        // reports removing this block as equivalent, and it is. It stays
        // because arriving at "expired" for something that was never stored is
        // the wrong reason to be right.
        if (!_entries.TryGet(key, out var entry))
        {
            return new ValueTask<CachedResponse?>((CachedResponse?)null);
        }

        // Past its absolute lifetime the entry is dropped rather than returned:
        // an entry kept indefinitely for revalidation would pin memory forever.
        if (_timeProvider.GetUtcNow() >= entry.ExpiresAt)
        {
            _entries.Remove(key);
            return new ValueTask<CachedResponse?>((CachedResponse?)null);
        }

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

        // Retained well past its freshness window so the ETag stays available:
        // a stale-but-present entry turns a re-download into a 304.
        // FromTicks rather than `timeToLive * multiplier`: the TimeSpan
        // multiplication operator is .NET Core 3.0+ and this also builds for
        // netstandard2.0. Same arithmetic.
        var absoluteLifetime = timeToLive > TimeSpan.Zero
            ? TimeSpan.FromTicks(timeToLive.Ticks * RevalidationLifetimeMultiplier)
            : TimeSpan.FromMinutes(1);

        _entries.Set(key, (response, _timeProvider.GetUtcNow() + absoluteLifetime));

        return default;
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        _entries.Remove(key);

        return default;
    }

    /// <summary>Empties the cache.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>
    /// How much longer than its freshness window an entry is retained so that its
    /// ETag remains usable for revalidation.
    /// </summary>
    private const int RevalidationLifetimeMultiplier = 12;

}

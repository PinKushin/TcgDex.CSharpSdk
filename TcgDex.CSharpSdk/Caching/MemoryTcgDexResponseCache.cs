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

    /// <summary>
    /// Entry count maintained incrementally, used only for the bound check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ConcurrentDictionary{TKey, TValue}.Count"/> looks like a field
    /// read and is not: it acquires every one of the dictionary's locks, and the
    /// lock array grows with the table. Measured on a full cache it cost
    /// <b>4.8 µs at 512 entries and 18.8 µs at 4096</b> — more than the eviction
    /// scan it was guarding, on <i>every</i> store, which made the cheapest
    /// operation in the class its most expensive.
    /// </para>
    /// <para>
    /// It is approximate by design. A store and a concurrent removal can
    /// interleave so that this drifts by one or two from the truth, which costs
    /// an eviction slightly early or slightly late and nothing else — the same
    /// tolerance the eviction order itself already has. The drift cannot
    /// accumulate: <see cref="EvictLeastRecentlyUsed"/> re-derives this from the
    /// dictionary each time it runs.
    /// </para>
    /// <para>
    /// <see cref="Count"/> stays exact, because that one is public and callers
    /// are entitled to a real answer.
    /// </para>
    /// </remarks>
    private int _approximateCount;

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
            Drop(key);
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

        var entry = new Entry(response, now + absoluteLifetime, now);

        // TryAdd rather than the indexer, because the two cases have to be told
        // apart: replacing an existing response is not growth, and counting it
        // as growth would evict live entries from a cache sitting under its
        // bound. The indexer cannot report which happened.
        if (_entries.TryAdd(key, entry))
        {
            if (Interlocked.Increment(ref _approximateCount) > _maxEntries)
            {
                EvictLeastRecentlyUsed();
            }
        }
        else
        {
            _entries[key] = entry;
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        Drop(key);

        return default;
    }

    /// <summary>Empties the cache.</summary>
    public void Clear()
    {
        _entries.Clear();
        Interlocked.Exchange(ref _approximateCount, 0);
    }

    /// <summary>Removes one entry and keeps the tracked count with it.</summary>
    /// <param name="key">The entry to remove; absent keys are ignored.</param>
    private void Drop(string key)
    {
        if (_entries.TryRemove(key, out _))
        {
            Interlocked.Decrement(ref _approximateCount);
        }
    }

    /// <summary>
    /// How much longer than its freshness window an entry is retained so that its
    /// ETag remains usable for revalidation.
    /// </summary>
    private const int RevalidationLifetimeMultiplier = 12;

    /// <summary>
    /// The fraction of the bound removed per eviction, as a divisor: an eighth.
    /// </summary>
    /// <remarks>
    /// The trade this number sets. Larger batches amortise the scan over more
    /// stores but leave the cache further below its bound; smaller ones keep it
    /// full and scan more often. An eighth costs at most 12.5% of the configured
    /// capacity and turns one scan per store into one per <c>MaxEntries/8</c>
    /// stores.
    /// </remarks>
    private const int EvictionBatchDivisor = 8;

    /// <summary>
    /// Removes the oldest batch of entries by last access.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a batch.</b> This used to remove exactly one entry, which sounds
    /// cheaper and is not: once the cache is at its bound <i>every</i> store
    /// overflows it, so a scan of every entry ran on every write. Measured, that
    /// is 14 µs per store at the default bound of 512 and 49 µs at 4096 — a cost
    /// that grows with a number the caller chooses. Evicting <c>MaxEntries/8</c>
    /// at a time amortises the scan across that many stores.
    /// </para>
    /// <para>
    /// <b>Why not an exact LRU.</b> A linked list would make eviction O(1), but
    /// only by moving a node to the head on every <i>read</i>, which needs a lock
    /// on the hot path. That trades the common case for the rare one. Reads here
    /// stay lock-free and eviction order stays approximate — which it already
    /// was, since <see cref="Entry.LastAccessedTicks"/> is written without
    /// synchronisation.
    /// </para>
    /// <para>
    /// Entries are sorted by last access rather than reduced to a cut-off time.
    /// A cut-off would be cheaper, but the system clock is coarse enough that
    /// many entries can share one tick, and every entry at the cut-off would
    /// then go at once — emptying most of the cache on a burst of stores.
    /// Sorting removes exactly the batch size, whatever the clock does.
    /// </para>
    /// <para>
    /// <b>Why a snapshot and not a pooled buffer.</b> The first version scanned
    /// the live dictionary into rented arrays, which allocated less. It also
    /// needed three branches that no test could reach: an empty-cache check, a
    /// guard against the dictionary growing mid-scan, and the pooled buffers'
    /// return path, whose removal changes nothing observable. Mutation testing
    /// found all three. <c>ToArray</c> takes the dictionary's locks and returns
    /// a consistent snapshot, so none of them are needed — and eviction now
    /// runs once per batch rather than once per store, which is what makes the
    /// snapshot affordable.
    /// </para>
    /// </remarks>
    private void EvictLeastRecentlyUsed()
    {
        var batch = Math.Max(1, _maxEntries / EvictionBatchDivisor);
        var snapshot = _entries.ToArray();

        Array.Sort(snapshot, CompareByLastAccess);

        var evicting = Math.Min(batch, snapshot.Length);

        for (var i = 0; i < evicting; i++)
        {
            _entries.TryRemove(snapshot[i].Key, out _);
        }

        // Re-derived rather than decremented, so any drift the incremental
        // counter picked up from concurrent stores and removals is corrected
        // here instead of accumulating. This is the expensive Count call, and it
        // now runs once per batch rather than once per store.
        Interlocked.Exchange(ref _approximateCount, _entries.Count);
    }

    /// <summary>Orders entries oldest-accessed first.</summary>
    private static readonly Comparison<KeyValuePair<string, Entry>> CompareByLastAccess =
        (x, y) => x.Value.LastAccessedTicks.CompareTo(y.Value.LastAccessedTicks);

    /// <summary>
    /// A stored response with its bookkeeping.
    /// </summary>
    /// <remarks>
    /// <see cref="LastAccessedTicks"/> is written without synchronisation
    /// because a lost update only costs slightly worse eviction ordering, never
    /// correctness. It is a <see cref="long"/> of UTC ticks rather than a
    /// <see cref="DateTimeOffset"/> for the same reason it is unsynchronised: a
    /// 64-bit field is written atomically on the platforms this actually runs
    /// on, whereas a 16-byte struct can tear into a value that was never
    /// written. Cheaper to compare when sorting, too.
    /// </remarks>
    private sealed class Entry(CachedResponse response, DateTimeOffset expiresAt, DateTimeOffset accessedAt)
    {
        internal CachedResponse Response { get; } = response;

        internal DateTimeOffset ExpiresAt { get; } = expiresAt;

        internal long LastAccessedTicks { get; private set; } = accessedAt.UtcTicks;

        internal void Touch(DateTimeOffset now) => LastAccessedTicks = now.UtcTicks;
    }
}

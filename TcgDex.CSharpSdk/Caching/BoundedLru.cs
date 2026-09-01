namespace TcgDex.Caching;

using System.Collections.Concurrent;

/// <summary>
/// A thread-safe store with an entry bound, evicting the least recently used
/// entries in batches when it overflows.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The stored value.</typeparam>
/// <remarks>
/// <para>
/// Extracted when a second cache needed the same policy. The two callers store
/// very different things — response bytes with an expiry, and deserialized
/// models keyed by <c>ETag</c> — but the bounding, the access tracking and the
/// eviction are identical, and every subtlety below was expensive enough to
/// learn once.
/// </para>
/// <para>
/// <b>The bound is checked against a counter this class maintains, not against
/// <see cref="ConcurrentDictionary{TKey, TValue}.Count"/>.</b> That property
/// reads like a field access and is not: it acquires every lock in the
/// dictionary, and the lock array grows with the table. Measured on a full
/// cache it cost 4.8 µs at 512 entries and 18.8 µs at 4096 — up to seventeen
/// times the entire store operation it was guarding, on every write.
/// </para>
/// <para>
/// The counter is approximate. A store and a concurrent removal can interleave
/// so it drifts by one or two, which costs an eviction slightly early or
/// slightly late and nothing else. The drift cannot accumulate:
/// <see cref="Evict"/> re-derives it from the dictionary each time it runs.
/// </para>
/// <para>
/// <b>Eviction takes a batch, not a single entry.</b> Once the store is at its
/// bound every write overflows it, so evicting one at a time ran a full scan on
/// every write. Removing <c>Max/8</c> at once amortises that scan across that
/// many writes, at the cost of settling just below the bound rather than
/// exactly at it.
/// </para>
/// <para>
/// <b>Recency is a counter, not a clock, and that is a correctness matter
/// rather than a preference.</b> The first version stamped each access with
/// <c>TimeProvider.GetUtcNow()</c>, and the <c>net472</c> test run caught what
/// that costs: the system clock is coarse — roughly 15 ms on Windows by default
/// — so a burst of accesses all share one timestamp, the sort sees a wall of
/// ties, and eviction picks arbitrarily among them. It can then remove **the
/// entry just inserted**, which turns a cache into wasted work. A monotonic
/// counter has no resolution to run out of and no ties to break.
/// </para>
/// <para>
/// <b>Eviction order is approximate, and reads stay lock-free because of it.</b>
/// An exact LRU would move a node to the head of a list on every <i>read</i>,
/// which needs a lock on the hot path — trading the common case for the rare
/// one. Here a read publishes an ordinal unsynchronised; a lost update costs
/// slightly worse ordering, never correctness.
/// </para>
/// </remarks>
internal sealed class BoundedLru<TKey, TValue>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Entry> _entries;
    private readonly int _maxEntries;
    private int _approximateCount;

    /// <summary>
    /// Source of access ordinals, incremented on every read and write.
    /// </summary>
    /// <remarks>
    /// A <see cref="long"/> so it cannot realistically wrap: at a billion
    /// accesses a second it lasts about 292 years, where an <see cref="int"/>
    /// would overflow in seconds and invert the ordering when it did.
    /// </remarks>
    private long _clock;

    /// <summary>The fraction of the bound removed per eviction, as a divisor.</summary>
    private const int EvictionBatchDivisor = 8;

    /// <summary>Creates a store holding at most <paramref name="maxEntries"/> values.</summary>
    /// <param name="maxEntries">The upper bound on stored entries.</param>
    /// <remarks>
    /// There is deliberately no comparer parameter. The first version took one
    /// so the response cache could pass <c>StringComparer.Ordinal</c>, and
    /// mutation testing showed all three of the resulting branches to be
    /// equivalent — because <c>EqualityComparer&lt;string&gt;.Default</c> is
    /// already ordinal, so the argument changed nothing. It was worse than
    /// nothing: naming a comparer explicitly opts a string-keyed
    /// <see cref="ConcurrentDictionary{TKey, TValue}"/> out of the runtime's
    /// fast path for string keys.
    /// </remarks>
    internal BoundedLru(int maxEntries)
    {
        Guard.NotLessThan(maxEntries, 1);

        _entries = new ConcurrentDictionary<TKey, Entry>();
        _maxEntries = maxEntries;
    }

    /// <summary>The number of entries currently held, counted exactly.</summary>
    internal int Count => _entries.Count;

    /// <summary>
    /// Reads an entry and records the access.
    /// </summary>
    /// <param name="key">The entry to read.</param>
    /// <param name="value">The stored value, when present.</param>
    /// <returns><see langword="true"/> when the key was present.</returns>
    internal bool TryGet(TKey key, out TValue value)
    {
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            value = default!;
            return false;
        }

        entry.Touch(Interlocked.Increment(ref _clock));
        value = entry.Value;

        return true;
    }

    /// <summary>
    /// Stores a value, evicting a batch if that takes the store over its bound.
    /// </summary>
    /// <param name="key">The entry key.</param>
    /// <param name="value">The value to store.</param>
    internal void Set(TKey key, TValue value)
    {
        Entry entry = new(value, Interlocked.Increment(ref _clock));

        // TryAdd rather than the indexer, because the two cases have to be told
        // apart: replacing an existing value is not growth, and counting it as
        // growth would evict live entries from a store sitting under its bound.
        // The indexer cannot report which happened.
        //
        // The loop is what makes that true under concurrency. Falling back to a
        // plain indexer assignment in an else branch — the obvious shape — LOSES
        // COUNT PERMANENTLY when a removal lands between the failed TryAdd and
        // that assignment:
        //
        //   1. this thread     Set(k)   -> TryAdd false, k is present
        //   2. another thread  Remove(k) -> succeeds, count N-1
        //   3. this thread     _entries[k] = entry
        //
        // The indexer ADDS when the key is absent, so step 3 puts k back
        // uncounted: the dictionary holds N while the counter says N-1. Both
        // racers are on the hot path — the cache removes on every non-success
        // response and on absolute-lifetime expiry, while another in-flight
        // request stores the same key.
        //
        // It cannot self-correct either. Evict only runs when the counter
        // EXCEEDS the bound, and this drift is precisely what stops it getting
        // there, so every occurrence raises the effective bound by one and it
        // never comes back down. MaxEntries then stops bounding memory in the
        // long-running process it exists for.
        //
        // TryUpdate replaces only if the key is still there; if it is not, the
        // next turn of the loop goes back through the counted TryAdd branch.
        while (true)
        {
            if (_entries.TryAdd(key, entry))
            {
                if (Interlocked.Increment(ref _approximateCount) > _maxEntries)
                {
                    Evict();
                }

                return;
            }

            if (_entries.TryGetValue(key, out Entry? existing)
                && _entries.TryUpdate(key, entry, existing))
            {
                return;
            }
        }
    }

    /// <summary>Removes one entry, keeping the tracked count with it.</summary>
    /// <param name="key">The entry to remove; absent keys are ignored.</param>
    internal void Remove(TKey key)
    {
        if (_entries.TryRemove(key, out _))
        {
            Interlocked.Decrement(ref _approximateCount);
        }
    }

    /// <summary>Empties the store.</summary>
    internal void Clear()
    {
        _entries.Clear();
        Interlocked.Exchange(ref _approximateCount, 0);
    }

    /// <summary>
    /// Removes the oldest batch of entries by last access.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Entries are sorted by access ordinal rather than reduced to a cut-off
    /// value. Sorting removes exactly the batch size; a cut-off is only cheaper
    /// while nothing ties, and the ordering this once used — a wall-clock
    /// timestamp — tied constantly.
    /// </para>
    /// <para>
    /// <b>Why a snapshot and not a pooled buffer.</b> Scanning the live
    /// dictionary into rented arrays allocates less. It also needs three
    /// branches no test can reach: an empty-store check, a guard against the
    /// dictionary growing mid-scan, and the buffers' return path, whose removal
    /// changes nothing observable. Mutation testing found all three.
    /// <c>ToArray</c> takes the locks once and returns a consistent snapshot, so
    /// none are needed — and eviction runs once per batch rather than once per
    /// write, which is what makes the snapshot affordable.
    /// </para>
    /// </remarks>
    private void Evict()
    {
        int batch = Math.Max(1, _maxEntries / EvictionBatchDivisor);
        KeyValuePair<TKey, Entry>[] snapshot = _entries.ToArray();

        Array.Sort(snapshot, CompareByLastAccess);

        int evicting = Math.Min(batch, snapshot.Length);

        for (int i = 0; i < evicting; i++)
        {
            _entries.TryRemove(snapshot[i].Key, out _);
        }

        // Re-derived rather than decremented, so any drift the incremental
        // counter picked up from concurrent writes and removals is corrected
        // here instead of accumulating. This is the expensive Count call, and it
        // runs once per batch rather than once per write.
        Interlocked.Exchange(ref _approximateCount, _entries.Count);
    }

    /// <summary>Orders entries oldest-accessed first.</summary>
    private static readonly Comparison<KeyValuePair<TKey, Entry>> CompareByLastAccess =
        (x, y) => x.Value.LastAccessed.CompareTo(y.Value.LastAccessed);

    /// <summary>
    /// A stored value with the ordinal of its most recent access.
    /// </summary>
    /// <remarks>
    /// <see cref="LastAccessed"/> is written without synchronisation, so a
    /// <see cref="long"/> rather than anything wider: a 64-bit field is written
    /// atomically on the platforms this runs on, whereas a 16-byte struct can
    /// tear into a value that was never written. Cheap to compare when sorting,
    /// and strictly increasing, so no two entries can tie.
    /// </remarks>
    private sealed class Entry(TValue value, long accessedAt)
    {
        internal TValue Value { get; } = value;

        internal long LastAccessed { get; private set; } = accessedAt;

        internal void Touch(long ordinal) => LastAccessed = ordinal;
    }
}

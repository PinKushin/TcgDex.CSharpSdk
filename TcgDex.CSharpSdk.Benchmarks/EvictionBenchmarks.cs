namespace TcgDex.Benchmarks;

using System;
using System.Globalization;
using System.Threading;
using TcgDex.Caching;

/// <summary>
/// Storing into a cache that is already full, where eviction runs.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MemoryTcgDexResponseCache"/> evicts by scanning every entry for
/// the oldest last-access time. That is O(n) per eviction, and once the cache is
/// at its bound <i>every</i> store overflows it, so the scan runs on every
/// write — not occasionally. Constant-factor costs elsewhere in this project
/// were worth a few microseconds; this one grows with the bound, which makes it
/// a different kind of problem and the reason it is measured separately.
/// </para>
/// <para>
/// <c>MaxEntries</c> is swept rather than fixed because the shape of the curve
/// is the finding. A cost that grows linearly with the parameter is the scan; a
/// flat line is not.
/// </para>
/// <para>
/// Each invocation stores one <em>new</em> key, which is what a real miss does.
/// Reusing keys would overwrite in place, leave the count unchanged and never
/// trigger eviction at all — measuring the one path this benchmark exists to
/// avoid measuring. Building that key allocates a string, so
/// <see cref="KeyGenerationOnly"/> measures the key alone and can be subtracted;
/// it is identical across every parameter value, so it cannot affect the slope.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class EvictionBenchmarks
{
    /// <summary>512 is the cache's default; the others bracket it by a factor of eight.</summary>
    [Params(64, 512, 4096)]
    public int MaxEntries { get; set; }

    private MemoryTcgDexResponseCache _cache = null!;
    private CachedResponse _response = null!;
    private int _next;

    private MemoryTcgDexResponseCache _mineFixed = null!;
    private TCGDex.MemoryTCGDexCache _theirsFixed = null!;
    private string[] _pool = [];
    private int _cursor;

    // A fourth row stood here: stores into a cache with a bound it could never
    // reach, meant to isolate insertion from eviction by subtraction. It was
    // unsound and had to go. A cache that never evicts grows for as long as the
    // benchmark runs, so the row measured a dictionary of a few hundred entries
    // in one iteration and a few million in the next — and once the store got
    // fast enough to run more iterations, it took the process to 7.4 GB before
    // it was killed. Isolation by subtraction needs the thing being subtracted
    // to hold still.

    [GlobalSetup]
    public void Setup()
    {
        _response = new CachedResponse
        {
            Body = new byte[64],
            ETag = "W/\"benchmark\"",
            ContentType = "application/json",
            StoredAt = DateTimeOffset.UtcNow,
        };

        _cache = new MemoryTcgDexResponseCache(MaxEntries);

        // Filled to the bound so the very first measured store already
        // overflows. Without this the early invocations would measure an insert
        // with no eviction and flatter the result.
        for (var i = 0; i < MaxEntries; i++)
        {
            Store(_cache, Key(i));
        }

        _next = MaxEntries;

        // ----- the like-for-like pair -----
        //
        // The other SDK's cache has no bound: MemoryTCGDexCache is a Dictionary
        // behind a lock, with no MaxEntries and no eviction. There is therefore
        // no "full" state on their side to compare against the row above, and
        // driving it with unique keys would grow it until the process died —
        // which is exactly how the removed fourth row failed.
        //
        // So the comparable question is narrower and both sides can answer it:
        // what does a store cost when nothing has to be evicted? A fixed pool of
        // keys, cycled, makes every store after the first a replacement. Neither
        // cache grows, memory is bounded at MaxEntries on both, and the row
        // measures insertion rather than policy.
        _pool = new string[MaxEntries];

        for (var i = 0; i < MaxEntries; i++)
        {
            _pool[i] = Key(i);
        }

        _mineFixed = new MemoryTcgDexResponseCache(MaxEntries);
        _theirsFixed = new TCGDex.MemoryTCGDexCache();

        foreach (var key in _pool)
        {
            Store(_mineFixed, key);
            _theirsFixed.Set(key, _response, 300);
        }
    }

    /// <summary>One store into a full cache: insert, overflow, evict.</summary>
    [Benchmark(Baseline = true)]
    public void StoreWhenFull() => Store(_cache, Key(_next++));

    /// <summary>Replacing an existing entry — no eviction, no growth.</summary>
    [Benchmark]
    public void StoreExisting_Mine() => Store(_mineFixed, NextPooledKey());

    /// <summary>The same on the other SDK's cache, which has no bound at all.</summary>
    /// <remarks>
    /// Read this as measuring what a bound costs, not who wrote a faster
    /// dictionary. Theirs takes a lock and inserts; ours additionally maintains
    /// the count that decides when to evict. The difference is the price of
    /// never exceeding <c>MaxEntries</c> — which their cache does not offer, so
    /// a long-lived process holds every response it ever fetched.
    /// </remarks>
    [Benchmark]
    public void StoreExisting_Theirs() => _theirsFixed.Set(NextPooledKey(), _response, 300);

    private string NextPooledKey()
    {
        var key = _pool[_cursor];
        _cursor = _cursor + 1 == _pool.Length ? 0 : _cursor + 1;

        return key;
    }

    /// <summary>
    /// The bound check alone — <c>_entries.Count</c>, which every store performs.
    /// </summary>
    /// <remarks>
    /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}.Count"/>
    /// is not a field read: it takes every one of the dictionary's locks and
    /// sums the per-lock counters. Whether that or the eviction scan dominates a
    /// store is the question this row exists to settle, and it is not one worth
    /// answering by reasoning about the source.
    /// </remarks>
    [Benchmark]
    public int CountOnly() => _cache.Count;

    /// <summary>The key construction alone, so it can be subtracted from the rows above.</summary>
    [Benchmark]
    public string KeyGenerationOnly() => Key(_next++);

    /// <summary>
    /// Stores one entry, without awaiting.
    /// </summary>
    /// <remarks>
    /// An in-memory store completes synchronously, so a benchmark that awaited
    /// would be measuring the state machine as much as the cache. Reading a
    /// <see cref="System.Threading.Tasks.ValueTask"/>'s result is only defined
    /// once it has completed, hence the check rather than a bare
    /// <c>GetResult()</c> — CA2012 is right to insist on it, and if the store
    /// ever became genuinely asynchronous this would keep working instead of
    /// silently returning early.
    /// </remarks>
    private void Store(MemoryTcgDexResponseCache cache, string key)
    {
        var pending = cache.SetAsync(key, _response, TimeSpan.FromMinutes(5), CancellationToken.None);

        if (pending.IsCompleted)
        {
            pending.GetAwaiter().GetResult();
        }
        else
        {
            pending.AsTask().GetAwaiter().GetResult();
        }
    }

    private static string Key(int index)
        => "https://api.tcgdex.net/v2/en/cards/swsh3-"
           + index.ToString(CultureInfo.InvariantCulture);
}

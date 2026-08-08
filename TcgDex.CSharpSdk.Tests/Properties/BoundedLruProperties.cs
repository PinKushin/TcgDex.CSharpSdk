namespace TcgDex.Tests.Properties;

using CsCheck;
using TcgDex.Caching;

/// <summary>
/// Invariants of <see cref="BoundedLru{TKey, TValue}"/> under generated
/// operation sequences.
/// </summary>
/// <remarks>
/// <para>
/// The example-based tests state what happens for a handful of chosen
/// sequences. These state what must hold for <em>all</em> of them, and CsCheck
/// shrinks any counter-example to the smallest sequence that still fails —
/// which is the difference between "eviction misbehaves somewhere in 200
/// writes" and "eviction misbehaves at maxEntries: 2, keys: [0, 1, 0]".
/// </para>
/// <para>
/// Single-threaded on purpose. The class is concurrency-safe by construction
/// and the count it tracks is explicitly approximate under concurrent writes,
/// so a property asserting an exact count across threads would be asserting
/// something the design does not promise. What it does promise, and what is
/// checked here, is the behaviour a single caller sees.
/// </para>
/// </remarks>
[TestFixture]
public sealed class BoundedLruProperties
{
    /// <summary>Bounds small enough that eviction actually fires.</summary>
    private static readonly Gen<int> Bounds = Gen.Int[1, 32];

    /// <summary>
    /// Keys drawn from a small pool so collisions — the replace path — occur
    /// often. Drawing from a wide range would make almost every write an insert
    /// and leave <c>TryAdd</c>'s replace branch generated but never taken.
    /// </summary>
    private static readonly Gen<int[]> KeySequences = Gen.Int[0, 40].Array[0, 300];

    [Test]
    public void TheBoundIsNeverExceeded()
    {
        // The memory bound is the entire reason this type exists. A cache that
        // grows past it is a leak that happens to have an eviction method.
        Gen.Select(Bounds, KeySequences).Sample((maxEntries, keys) =>
        {
            BoundedLru<int, int> lru = new(maxEntries);

            foreach (int key in keys)
            {
                lru.Set(key, key);

                if (lru.Count > maxEntries)
                {
                    return false;
                }
            }

            return true;
        });
    }

    [Test]
    public void TheEntryJustWrittenIsStillThere()
    {
        // The bug this would have caught: eviction once ordered entries by a
        // wall clock, whose resolution is coarse enough that a whole batch of
        // writes shared one timestamp. With ties, the sort could place the entry
        // just inserted inside the evicted prefix — so a Set immediately
        // followed by a TryGet missed, and only on the fast net472 run where
        // the writes landed inside a single tick.
        //
        // A monotonic counter gives the newest entry the highest ordinal, so it
        // sorts last and cannot be in the evicted prefix. That is what this
        // asserts, for every sequence rather than for the one that happened to
        // expose it.
        Gen.Select(Bounds, KeySequences).Sample((maxEntries, keys) =>
        {
            BoundedLru<int, int> lru = new(maxEntries);

            foreach (int key in keys)
            {
                lru.Set(key, key);

                if (!lru.TryGet(key, out int value) || value != key)
                {
                    return false;
                }
            }

            return true;
        });
    }

    [Test]
    public void ReplacingAnExistingKeyDoesNotEvict()
    {
        // Set uses TryAdd rather than the indexer specifically so a replace is
        // not counted as growth. If it were, a store sitting under its bound
        // would accumulate phantom growth and evict live entries on the next
        // genuine insert.
        //
        // The condition matters more than the assertion here. A first version
        // filled the store, rewrote existing keys, and checked the count had
        // not moved — and it passed even with replaces counted as growth,
        // because `Count` reads the dictionary rather than the tracked counter,
        // so the drift is invisible until something acts on it. Eviction only
        // runs on insert, so the sequence has to end with one.
        //
        // Hence: stay *below* the bound, rewrite heavily, then add one new key.
        // Nothing should have been evicted, because the store never grew past
        // what it can hold.
        Gen.Select(Bounds, Gen.Int[0, 200]).Sample((maxEntries, rewrites) =>
        {
            BoundedLru<int, int> lru = new(maxEntries);

            // Half the bound, so the one insert at the end cannot legitimately
            // trigger eviction. At maxEntries 1 there is no room to be under
            // the bound and still hold a key, so that case is skipped.
            int initial = maxEntries / 2;

            if (initial == 0)
            {
                return true;
            }

            for (int key = 0; key < initial; key++)
            {
                lru.Set(key, key);
            }

            for (int i = 0; i < rewrites; i++)
            {
                lru.Set(i % initial, i);
            }

            lru.Set(int.MaxValue, 0);

            return lru.Count == initial + 1;
        });
    }

    [Test]
    public void RemoveThenSetLeavesTheStoreConsistent()
    {
        // Remove decrements the tracked count while Evict re-derives it. Those
        // two paths disagreeing is how a counter drifts, and drift shows up as
        // either premature eviction or a bound quietly exceeded — both only
        // after an interleaving no example test would think to write.
        Gen.Select(Bounds, KeySequences, Gen.Int[0, 40].Array[0, 100])
            .Sample((maxEntries, writes, removals) =>
            {
                BoundedLru<int, int> lru = new(maxEntries);

                foreach (int key in writes)
                {
                    lru.Set(key, key);
                }

                foreach (int key in removals)
                {
                    lru.Remove(key);
                }

                foreach (int key in writes)
                {
                    lru.Set(key, key);

                    if (lru.Count > maxEntries)
                    {
                        return false;
                    }
                }

                return true;
            });
    }
}

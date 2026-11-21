namespace TcgDex.Tests.Caching;

using System.Threading;
using TcgDex.Caching;

/// <summary>
/// The entry counter that decides when to evict, after writes, removals and clears race each other.
/// </summary>
/// <remarks>
/// <para>
/// The counter is private, so it is read through what it controls. After the storm the store is cleared
/// with no one else running, then filled to exactly its bound: an over-counted store evicts too early and
/// holds fewer than the bound, an under-counted one never evicts and holds more after one more write.
/// </para>
/// <para>
/// Eviction used to overwrite the counter with the dictionary's count, losing any increment landing between
/// the read and the write (89 entries against a bound of 50 under parallel writes). These tests are a race,
/// so they are not deterministic; the storm repeats to make missing it unlikely.
/// </para>
/// </remarks>
[TestFixture]
public sealed class BoundedLruCountTests
{
    private const int MaxEntries = 64;
    private const int Threads = 8;
    private const int OpsPerThread = 2_000;

    private static void Storm(BoundedLru<int, int> lru, bool withClear)
    {
        using Barrier start = new(Threads);
        Thread[] workers = Enumerable.Range(0, Threads)
            .Select(t => new Thread(() =>
            {
                start.SignalAndWait();

                for (int i = 0; i < OpsPerThread; i++)
                {
                    int key = ((t * 7_919) + i) % (MaxEntries * 3);

                    if (withClear && i % 500 == 499)
                    {
                        lru.Clear();
                    }
                    else if (i % 3 == 0)
                    {
                        lru.Remove(key);
                    }
                    else
                    {
                        lru.Set(key, i);
                    }
                }
            }))
            .ToArray();

        Array.ForEach(workers, w => w.Start());
        Array.ForEach(workers, w => w.Join());
    }

    private static void CounterMatchesTheStore(BoundedLru<int, int> lru)
    {
        lru.Clear();
        lru.Count.ShouldBe(0);

        for (int i = 0; i < MaxEntries; i++)
        {
            lru.Set(1_000_000 + i, i);
        }

        lru.Count.ShouldBe(MaxEntries, "an over-counted store evicts before reaching its bound");

        lru.Set(2_000_000, 0);

        lru.Count.ShouldBeLessThanOrEqualTo(MaxEntries, "an under-counted store never evicts");
    }

    [Test]
    public void AfterSetsAndRemovesRace_TheStoreIsWithinItsBound_AndTheCounterMatchesIt(
        [Values(false, true)] bool withClear)
    {
        for (int round = 0; round < 10; round++)
        {
            BoundedLru<int, int> lru = new(MaxEntries);

            Storm(lru, withClear);

            lru.Count.ShouldBeLessThanOrEqualTo(MaxEntries, $"round {round}");
            CounterMatchesTheStore(lru);
        }
    }
}

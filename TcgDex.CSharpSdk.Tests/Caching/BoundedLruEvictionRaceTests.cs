namespace TcgDex.Tests.Caching;

using System.Threading;
using System.Threading.Tasks;
using TcgDex.Caching;

/// <summary>
/// Eviction while other threads are reading the same entries.
/// </summary>
/// <remarks>
/// <para>
/// Eviction used to sort the live entries by their last-access ordinal, while
/// every concurrent read wrote a new ordinal into those same entries. A comparer
/// that answers differently for the same pair partway through a sort is
/// inconsistent, and .NET's sort detects that and throws
/// <see cref="ArgumentException"/> — out of <c>Set</c>, so out of a cache write
/// on the request path. The fix copies each ordinal once into a value pair and
/// sorts the copies.
/// </para>
/// <para>
/// A race test cannot be deterministic, and no seam was added to force one: the
/// eviction path is the hot path's rare branch and a test hook there would ship.
/// This one is not marginal, though. Against the old code it failed 9 runs out
/// of 9 on each of net472, net8.0 and net10.0, in under 400 ms, with the
/// runtime's own "IComparer.Compare() method returns inconsistent results".
/// </para>
/// </remarks>
[TestFixture]
public sealed class BoundedLruEvictionRaceTests
{
    [Test]
    public void EvictingWhileReadersTouchEntries_NeverThrowsAndStaysBounded()
    {
        const int MaxEntries = 256;
        const int Readers = 4;
        const int Writes = 40_000;

        BoundedLru<int, int> lru = new(MaxEntries);
        using CancellationTokenSource stop = new();

        Task[] readers = Enumerable.Range(0, Readers)
            .Select(seed => Task.Run(() =>
            {
                int key = seed;

                while (!stop.IsCancellationRequested)
                {
                    key = (key * 1_103_515_245 + 12_345) & 0x3FF;
                    _ = lru.TryGet(key, out _);
                }
            }))
            .ToArray();

        try
        {
            for (int i = 0; i < Writes; i++)
            {
                lru.Set(i & 0x3FF, i);
            }
        }
        finally
        {
            stop.Cancel();
            Task.WaitAll(readers);
        }

        lru.Count.ShouldBeLessThanOrEqualTo(MaxEntries);
    }
}

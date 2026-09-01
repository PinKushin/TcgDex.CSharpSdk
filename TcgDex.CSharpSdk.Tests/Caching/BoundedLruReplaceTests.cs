namespace TcgDex.Tests.Caching;

using TcgDex.Caching;

/// <summary>
/// Storing a key that was removed between the failed add and the write.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is deliberately no concurrency test here, and that is a finding
/// rather than an omission.</b> The race this guards — a removal landing between
/// a failed <c>TryAdd</c> and the write that follows — was reported as losing
/// count "permanently and unboundedly", making <c>MaxEntries</c> stop bounding
/// memory. Checked against the code, it does not: <c>Evict</c> re-derives the
/// counter from the dictionary rather than decrementing it, so every eviction
/// wipes whatever drift has built up. The store can sit over its bound by the
/// drift accumulated since the last eviction, and then corrects itself.
/// </para>
/// <para>
/// A concurrency test was written for it and deleted after the manipulation
/// harness reported it INSENSITIVE: reintroducing the defect left it green. The
/// effect is below the resolution of anything observable from outside — each
/// occurrence costs one entry, the window is a few instructions wide, and
/// eviction erases the evidence. An insensitive test is worse than no test,
/// because it reads as evidence.
/// </para>
/// <para>
/// The fix is kept regardless. It costs one retry loop, removes a real
/// inconsistency between the counter and the store, and has no downside — but it
/// is a tidiness fix, not the memory leak it was reported as.
/// </para>
/// </remarks>
[TestFixture]
public sealed class BoundedLruReplaceTests
{
    [Test]
    public void StoringAKeyAfterRemovingIt_KeepsOneEntryWithTheLatestValue()
    {
        // Replace-after-remove semantics, single-threaded: the ordinary path
        // through the retry loop. It cannot detect the race above — both the
        // fixed and the broken implementation land here — so it is named for
        // what it actually measures.
        BoundedLru<string, int> lru = new(4);

        for (int i = 0; i < 50; i++)
        {
            lru.Set("k", i);
            lru.Remove("k");
            lru.Set("k", i);
        }

        lru.Count.ShouldBe(1);
        lru.TryGet("k", out int value).ShouldBeTrue();
        value.ShouldBe(49);
    }
}

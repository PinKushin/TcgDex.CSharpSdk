namespace TcgDex.Tests.Caching;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TcgDex;
using TcgDex.Caching;

/// <summary>
/// The default in-memory store, and the DI registration that wires it up.
/// </summary>
[TestFixture]
public sealed class MemoryCacheTests
{
    private static CachedResponse Response(string body = "{}", string? etag = "W/\"x\"")
        => new()
        {
            Body = System.Text.Encoding.UTF8.GetBytes(body),
            ETag = etag,
            ContentType = "application/json",
            StoredAt = UnixEpoch,
        };

    [Test]
    public async Task StoredEntry_IsRetrievable()
    {
        MemoryTcgDexResponseCache cache = new();

        await cache.SetAsync("a", Response("""{"v":1}"""), TimeSpan.FromMinutes(5));
        CachedResponse? entry = await cache.GetAsync("a");

        entry.ShouldNotBeNull();
        System.Text.Encoding.UTF8.GetString(entry.Body).ShouldBe("""{"v":1}""");
        cache.Count.ShouldBe(1);
    }

    [Test]
    public async Task MissingKey_ReturnsNull()
        => (await new MemoryTcgDexResponseCache().GetAsync("absent")).ShouldBeNull();

    [Test]
    public async Task RemovedEntry_IsGone()
    {
        MemoryTcgDexResponseCache cache = new();

        await cache.SetAsync("a", Response(), TimeSpan.FromMinutes(5));
        await cache.RemoveAsync("a");

        (await cache.GetAsync("a")).ShouldBeNull();
    }

    [Test]
    public async Task RemovingAnAbsentKey_IsHarmless()
        => await Should.NotThrowAsync(async () =>
            await new MemoryTcgDexResponseCache().RemoveAsync("never-stored"));

    [Test]
    public async Task Clear_EmptiesTheCache()
    {
        MemoryTcgDexResponseCache cache = new();

        await cache.SetAsync("a", Response(), TimeSpan.FromMinutes(5));
        await cache.SetAsync("b", Response(), TimeSpan.FromMinutes(5));
        cache.Clear();

        cache.Count.ShouldBe(0);
        (await cache.GetAsync("a")).ShouldBeNull();
    }

    [Test]
    public async Task PastItsAbsoluteLifetime_AnEntryIsDropped()
    {
        // Entries outlive their freshness window so the ETag stays usable, but
        // not forever — an entry kept indefinitely would pin memory.
        FakeTimeProvider time = new();
        MemoryTcgDexResponseCache cache = new(timeProvider: time);

        await cache.SetAsync("a", Response(), TimeSpan.FromMinutes(1));

        time.Advance(TimeSpan.FromMinutes(30));

        (await cache.GetAsync("a")).ShouldBeNull();
        cache.Count.ShouldBe(0, "the expired entry should have been removed, not just hidden");
    }

    [Test]
    public async Task WithinItsLifetime_AStaleEntryIsStillReturnedForRevalidation()
    {
        FakeTimeProvider time = new();
        MemoryTcgDexResponseCache cache = new(timeProvider: time);

        await cache.SetAsync("a", Response(), TimeSpan.FromMinutes(5));

        // Past freshness, well inside the retention window.
        time.Advance(TimeSpan.FromMinutes(10));

        (await cache.GetAsync("a")).ShouldNotBeNull("the ETag must survive to allow a 304");
    }

    [Test]
    public async Task AZeroTimeToLive_StillRetainsBriefly()
    {
        // A zero window means "revalidate every time", not "do not store" — the
        // entry must survive long enough to carry its ETag.
        FakeTimeProvider time = new();
        MemoryTcgDexResponseCache cache = new(timeProvider: time);

        await cache.SetAsync("a", Response(), TimeSpan.Zero);

        (await cache.GetAsync("a")).ShouldNotBeNull();
    }

    [Test]
    public async Task WhenFull_TheLeastRecentlyUsedEntryIsEvicted()
    {
        FakeTimeProvider time = new();
        MemoryTcgDexResponseCache cache = new(maxEntries: 3, timeProvider: time);

        await cache.SetAsync("a", Response(), TimeSpan.FromHours(1));
        time.Advance(TimeSpan.FromSeconds(1));
        await cache.SetAsync("b", Response(), TimeSpan.FromHours(1));
        time.Advance(TimeSpan.FromSeconds(1));
        await cache.SetAsync("c", Response(), TimeSpan.FromHours(1));

        // Touch "a" so it is no longer the oldest by access.
        time.Advance(TimeSpan.FromSeconds(1));
        await cache.GetAsync("a");

        time.Advance(TimeSpan.FromSeconds(1));
        await cache.SetAsync("d", Response(), TimeSpan.FromHours(1));

        cache.Count.ShouldBe(3);
        (await cache.GetAsync("a")).ShouldNotBeNull("recently read, so it should survive");
        (await cache.GetAsync("b")).ShouldBeNull("least recently used, so it should be evicted");
    }

    [Test]
    public void ANonPositiveBound_IsRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new MemoryTcgDexResponseCache(0));
        Should.Throw<ArgumentOutOfRangeException>(() => new MemoryTcgDexResponseCache(-1));
    }

    [Test]
    public async Task WhenFull_OneOverflowEvictsAWholeBatch()
    {
        // Eviction scans every entry to find the oldest, so doing it once per
        // store makes every write O(MaxEntries) — measured at 14 µs on the
        // default bound of 512 and 49 µs at 4096. Evicting a batch amortises
        // that scan over many stores, at the cost of the cache settling just
        // below its bound rather than exactly at it.
        FakeTimeProvider time = new();
        MemoryTcgDexResponseCache cache = new(maxEntries: 16, timeProvider: time);

        for (int i = 0; i < 16; i++)
        {
            await cache.SetAsync($"key-{i}", Response(), TimeSpan.FromHours(1));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        await cache.SetAsync("overflow", Response(), TimeSpan.FromHours(1));

        // 16 / 8 = 2 evicted from 17, so 15 remain.
        cache.Count.ShouldBe(15);
        (await cache.GetAsync("key-0")).ShouldBeNull("oldest, so first out");
        (await cache.GetAsync("key-1")).ShouldBeNull("second oldest, so also in the batch");
        (await cache.GetAsync("key-2")).ShouldNotBeNull("third oldest, so outside the batch");
        (await cache.GetAsync("overflow")).ShouldNotBeNull("just stored");
    }

    [Test]
    public async Task ASmallBoundStillEvictsOneAtATime()
    {
        // A batch of MaxEntries/8 rounds to zero below eight, and evicting
        // nothing would let the cache grow without limit — the floor of one is
        // what stops that, and nothing else tests a bound that small.
        FakeTimeProvider time = new();
        MemoryTcgDexResponseCache cache = new(maxEntries: 2, timeProvider: time);

        await cache.SetAsync("a", Response(), TimeSpan.FromHours(1));
        time.Advance(TimeSpan.FromSeconds(1));
        await cache.SetAsync("b", Response(), TimeSpan.FromHours(1));
        time.Advance(TimeSpan.FromSeconds(1));
        await cache.SetAsync("c", Response(), TimeSpan.FromHours(1));

        cache.Count.ShouldBe(2);
        (await cache.GetAsync("a")).ShouldBeNull("oldest");
    }

    [Test]
    public async Task AfterClear_TheNextStoreSurvives()
    {
        // Clear has to reset the tracked entry count as well as the entries.
        // If it does not, the cache believes it is still full, the next store
        // trips the bound, and eviction removes the only entry there is —
        // the one just written. Mutation testing found this: deleting the reset
        // left every existing test passing.
        MemoryTcgDexResponseCache cache = new(maxEntries: 3);

        await cache.SetAsync("a", Response(), TimeSpan.FromHours(1));
        await cache.SetAsync("b", Response(), TimeSpan.FromHours(1));
        await cache.SetAsync("c", Response(), TimeSpan.FromHours(1));

        cache.Clear();

        await cache.SetAsync("d", Response(), TimeSpan.FromHours(1));

        cache.Count.ShouldBe(1);
        (await cache.GetAsync("d")).ShouldNotBeNull("stored after the clear, so nothing should have evicted it");
    }

    [Test]
    public async Task RepeatedlyStoringTheSameKey_EvictsNothing()
    {
        // Replacing an entry is not growth. Nothing asserted that before, and
        // it is exactly what breaks if the bound is checked against a counter
        // that is incremented per store rather than per insert — a cache would
        // then evict its own live entries while sitting well under its limit.
        MemoryTcgDexResponseCache cache = new(maxEntries: 3);

        await cache.SetAsync("a", Response(), TimeSpan.FromHours(1));
        await cache.SetAsync("b", Response(), TimeSpan.FromHours(1));
        await cache.SetAsync("c", Response(), TimeSpan.FromHours(1));

        for (int i = 0; i < 20; i++)
        {
            await cache.SetAsync("a", Response(), TimeSpan.FromHours(1));
        }

        cache.Count.ShouldBe(3);
        (await cache.GetAsync("a")).ShouldNotBeNull();
        (await cache.GetAsync("b")).ShouldNotBeNull();
        (await cache.GetAsync("c")).ShouldNotBeNull();
    }

    [Test]
    public async Task RemovedEntries_FreeRoomWithoutEviction()
    {
        // The other half of the same contract: a removal has to be accounted
        // for, or the cache believes it is full when it is not and evicts on a
        // store that had room.
        MemoryTcgDexResponseCache cache = new(maxEntries: 3);

        await cache.SetAsync("a", Response(), TimeSpan.FromHours(1));
        await cache.SetAsync("b", Response(), TimeSpan.FromHours(1));
        await cache.SetAsync("c", Response(), TimeSpan.FromHours(1));
        await cache.RemoveAsync("a");
        await cache.SetAsync("d", Response(), TimeSpan.FromHours(1));

        cache.Count.ShouldBe(3);
        (await cache.GetAsync("b")).ShouldNotBeNull("nothing should have been evicted");
        (await cache.GetAsync("c")).ShouldNotBeNull("nothing should have been evicted");
        (await cache.GetAsync("d")).ShouldNotBeNull();
    }

    [Test]
    public async Task RepeatedOverflow_NeverExceedsTheBound()
    {
        // The bound is the whole point of the class: a batch that miscounted,
        // or a scan that removed nothing because every entry shared a
        // timestamp, would leak memory rather than fail visibly.
        MemoryTcgDexResponseCache cache = new(maxEntries: 32);

        for (int i = 0; i < 500; i++)
        {
            await cache.SetAsync($"key-{i}", Response(), TimeSpan.FromHours(1));
            cache.Count.ShouldBeLessThanOrEqualTo(32);
        }

        cache.Count.ShouldBeGreaterThan(0);
    }

    // Two tests stood here — ABlankKey_IsRejected and ANullResponse_IsRejected —
    // and both could not fail. Each called Should.ThrowAsync from a void test
    // method and discarded the returned Task, so when the guard did *not* throw
    // the resulting faulted task was never observed and the test still passed.
    // GetAsync_WithABlankKey_Throws and its siblings below cover the same
    // contract, over all three methods and a null key as well, by blocking on
    // the call. These were removed rather than repaired because keeping both
    // would have duplicated working tests with weaker ones.

    [Test]
    public async Task ConcurrentWrites_DoNotCorruptTheCache()
    {
        MemoryTcgDexResponseCache cache = new(maxEntries: 50);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i =>
            cache.SetAsync($"key-{i}", Response(), TimeSpan.FromHours(1)).AsTask()));

        cache.Count.ShouldBeLessThanOrEqualTo(50);
        cache.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public void IsFresh_ComparesAgainstTheStoreTime()
    {
        DateTimeOffset stored = UnixEpoch;
        CachedResponse response = Response() with { StoredAt = stored };

        response.IsFresh(stored.AddSeconds(30), TimeSpan.FromMinutes(1)).ShouldBeTrue();
        response.IsFresh(stored.AddMinutes(2), TimeSpan.FromMinutes(1)).ShouldBeFalse();
    }

    // ----- DI registration -----

    [Test]
    public void AddTcgDexWithCaching_RegistersAWorkingClient()
    {
        ServiceCollection services = new();
        services.AddTcgDexWithCaching();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<ITcgDexClient>().ShouldNotBeNull();
        provider.GetRequiredService<ITcgDexResponseCache>().ShouldBeOfType<MemoryTcgDexResponseCache>();
        provider.GetRequiredService<TcgDexCacheOptions>().ShouldNotBeNull();
    }

    [Test]
    public void AddTcgDexWithCaching_AppliesBothOptionSets()
    {
        ServiceCollection services = new();
        services.AddTcgDexWithCaching(
            configure: o => o.Language = TcgDexLanguages.German,
            configureCache: c => c.DefaultTimeToLive = TimeSpan.FromMinutes(42));

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<TcgDexOptions>().Language.ShouldBe("de");
        provider.GetRequiredService<TcgDexCacheOptions>()
            .DefaultTimeToLive.ShouldBe(TimeSpan.FromMinutes(42));
    }

    [Test]
    public void AddTcgDexWithCaching_LetsCallersSupplyTheirOwnStore()
    {
        ServiceCollection services = new();
        services.AddSingleton<ITcgDexResponseCache, CountingCache>();
        services.AddTcgDexWithCaching();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<ITcgDexResponseCache>().ShouldBeOfType<CountingCache>();
    }

    [Test]
    public void AddTcgDexWithCaching_ValidatesLanguageAtRegistration()
    {
        ServiceCollection services = new();

        Should.Throw<ArgumentException>(() => services.AddTcgDexWithCaching(o => o.Language = "zz"));
    }

    [Test]
    public void CachingHandler_RequiresACache()
        => Should.Throw<ArgumentNullException>(() => new TcgDexCachingHandler(null!));

    private sealed class CountingCache : ITcgDexResponseCache
    {
        public ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default)
            => new((CachedResponse?)null);

        public ValueTask SetAsync(
            string key,
            CachedResponse response,
            TimeSpan timeToLive,
            CancellationToken cancellationToken = default)
            => default;

        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
            => default;
    }

    // ----- the public contract of a cache anyone can implement against -----

    private static CachedResponse SampleResponse(DateTimeOffset storedAt) => new()
    {
        Body = System.Text.Encoding.UTF8.GetBytes("{}"),
        StoredAt = storedAt,
    };

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void GetAsync_WithABlankKey_Throws(string? key)
    {
        // MemoryTcgDexResponseCache is public and ITcgDexResponseCache is an
        // extension point, so these guards are contract rather than internal
        // defensiveness — a caller really can reach them. Nothing tested any of
        // the three methods with a bad key.
        MemoryTcgDexResponseCache cache = new();

        Should.Throw<ArgumentException>(() => cache.GetAsync(key!).AsTask().Wait());
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void SetAsync_WithABlankKey_Throws(string? key)
    {
        MemoryTcgDexResponseCache cache = new();

        Should.Throw<ArgumentException>(() =>
            cache.SetAsync(key!, SampleResponse(UnixEpoch), TimeSpan.FromMinutes(1))
                .AsTask().Wait());
    }

    [Test]
    public void SetAsync_WithANullResponse_Throws()
    {
        MemoryTcgDexResponseCache cache = new();

        Should.Throw<ArgumentNullException>(() =>
            cache.SetAsync("k", null!, TimeSpan.FromMinutes(1)).AsTask().Wait());
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void RemoveAsync_WithABlankKey_Throws(string? key)
    {
        MemoryTcgDexResponseCache cache = new();

        Should.Throw<ArgumentException>(() => cache.RemoveAsync(key!).AsTask().Wait());
    }

    [Test]
    public void EveryMethod_ObservesCancellation()
    {
        // Each entry point checks the token before doing any work. An
        // implementation that ignored it would look identical in every other
        // test, because none of them cancel.
        MemoryTcgDexResponseCache cache = new();
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        Should.Throw<OperationCanceledException>(
            () => cache.GetAsync("k", cancelled.Token).AsTask().Wait());

        Should.Throw<OperationCanceledException>(
            () => cache.SetAsync("k", SampleResponse(UnixEpoch), TimeSpan.FromMinutes(1), cancelled.Token)
                .AsTask().Wait());

        Should.Throw<OperationCanceledException>(
            () => cache.RemoveAsync("k", cancelled.Token).AsTask().Wait());
    }

    [Test]
    public async Task AnEntryExactlyAtItsExpiry_IsTreatedAsExpired()
    {
        // The boundary, which `>=` versus `>` decides. An entry whose lifetime
        // has exactly elapsed is gone, not still valid — and with distinct
        // timestamps every other test passes either way, so only landing
        // precisely on the expiry distinguishes them.
        FakeTimeProvider time = new();
        MemoryTcgDexResponseCache cache = new(timeProvider: time);

        TimeSpan ttl = TimeSpan.FromMinutes(5);
        await cache.SetAsync("k", SampleResponse(time.GetUtcNow()), ttl);

        // The entry is retained past freshness for revalidation, so advancing
        // by the retention multiple is what lands on the absolute expiry.
        time.Advance(TimeSpan.FromMinutes(5 * 12));

        (await cache.GetAsync("k")).ShouldBeNull();
    }}

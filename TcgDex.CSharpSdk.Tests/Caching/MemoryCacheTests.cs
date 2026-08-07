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
        var cache = new MemoryTcgDexResponseCache();

        await cache.SetAsync("a", Response("""{"v":1}"""), TimeSpan.FromMinutes(5));
        var entry = await cache.GetAsync("a");

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
        var cache = new MemoryTcgDexResponseCache();

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
        var cache = new MemoryTcgDexResponseCache();

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
        var time = new FakeTimeProvider();
        var cache = new MemoryTcgDexResponseCache(timeProvider: time);

        await cache.SetAsync("a", Response(), TimeSpan.FromMinutes(1));

        time.Advance(TimeSpan.FromMinutes(30));

        (await cache.GetAsync("a")).ShouldBeNull();
        cache.Count.ShouldBe(0, "the expired entry should have been removed, not just hidden");
    }

    [Test]
    public async Task WithinItsLifetime_AStaleEntryIsStillReturnedForRevalidation()
    {
        var time = new FakeTimeProvider();
        var cache = new MemoryTcgDexResponseCache(timeProvider: time);

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
        var time = new FakeTimeProvider();
        var cache = new MemoryTcgDexResponseCache(timeProvider: time);

        await cache.SetAsync("a", Response(), TimeSpan.Zero);

        (await cache.GetAsync("a")).ShouldNotBeNull();
    }

    [Test]
    public async Task WhenFull_TheLeastRecentlyUsedEntryIsEvicted()
    {
        var time = new FakeTimeProvider();
        var cache = new MemoryTcgDexResponseCache(maxEntries: 3, timeProvider: time);

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

    [TestCase("")]
    [TestCase("   ")]
    public void ABlankKey_IsRejected(string key)
    {
        var cache = new MemoryTcgDexResponseCache();

        Should.ThrowAsync<ArgumentException>(async () => await cache.GetAsync(key));
        Should.ThrowAsync<ArgumentException>(async () => await cache.RemoveAsync(key));
        Should.ThrowAsync<ArgumentException>(async () => await cache.SetAsync(key, Response(), TimeSpan.FromMinutes(1)));
    }

    [Test]
    public void ANullResponse_IsRejected()
        => Should.ThrowAsync<ArgumentNullException>(async () =>
            await new MemoryTcgDexResponseCache().SetAsync("a", null!, TimeSpan.FromMinutes(1)));

    [Test]
    public async Task ConcurrentWrites_DoNotCorruptTheCache()
    {
        var cache = new MemoryTcgDexResponseCache(maxEntries: 50);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i =>
            cache.SetAsync($"key-{i}", Response(), TimeSpan.FromHours(1)).AsTask()));

        cache.Count.ShouldBeLessThanOrEqualTo(50);
        cache.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public void IsFresh_ComparesAgainstTheStoreTime()
    {
        var stored = UnixEpoch;
        var response = Response() with { StoredAt = stored };

        response.IsFresh(stored.AddSeconds(30), TimeSpan.FromMinutes(1)).ShouldBeTrue();
        response.IsFresh(stored.AddMinutes(2), TimeSpan.FromMinutes(1)).ShouldBeFalse();
    }

    // ----- DI registration -----

    [Test]
    public void AddTcgDexWithCaching_RegistersAWorkingClient()
    {
        var services = new ServiceCollection();
        services.AddTcgDexWithCaching();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ITcgDexClient>().ShouldNotBeNull();
        provider.GetRequiredService<ITcgDexResponseCache>().ShouldBeOfType<MemoryTcgDexResponseCache>();
        provider.GetRequiredService<TcgDexCacheOptions>().ShouldNotBeNull();
    }

    [Test]
    public void AddTcgDexWithCaching_AppliesBothOptionSets()
    {
        var services = new ServiceCollection();
        services.AddTcgDexWithCaching(
            configure: o => o.Language = TcgDexLanguages.German,
            configureCache: c => c.DefaultTimeToLive = TimeSpan.FromMinutes(42));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<TcgDexOptions>().Language.ShouldBe("de");
        provider.GetRequiredService<TcgDexCacheOptions>()
            .DefaultTimeToLive.ShouldBe(TimeSpan.FromMinutes(42));
    }

    [Test]
    public void AddTcgDexWithCaching_LetsCallersSupplyTheirOwnStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITcgDexResponseCache, CountingCache>();
        services.AddTcgDexWithCaching();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ITcgDexResponseCache>().ShouldBeOfType<CountingCache>();
    }

    [Test]
    public void AddTcgDexWithCaching_ValidatesLanguageAtRegistration()
    {
        var services = new ServiceCollection();

        Should.Throw<ArgumentException>(() => services.AddTcgDexWithCaching(o => o.Language = "zz"));
    }

    [Test]
    public void CachingHandler_RequiresACache()
        => Should.Throw<ArgumentNullException>(() => new TcgDexCachingHandler(null!));

    private sealed class CountingCache : ITcgDexResponseCache
    {
        public ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default)
            => new ValueTask<CachedResponse?>((CachedResponse?)null);

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
        var cache = new MemoryTcgDexResponseCache();

        Should.Throw<ArgumentException>(() => cache.GetAsync(key!).AsTask().Wait());
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void SetAsync_WithABlankKey_Throws(string? key)
    {
        var cache = new MemoryTcgDexResponseCache();

        Should.Throw<ArgumentException>(() =>
            cache.SetAsync(key!, SampleResponse(UnixEpoch), TimeSpan.FromMinutes(1))
                .AsTask().Wait());
    }

    [Test]
    public void SetAsync_WithANullResponse_Throws()
    {
        var cache = new MemoryTcgDexResponseCache();

        Should.Throw<ArgumentNullException>(() =>
            cache.SetAsync("k", null!, TimeSpan.FromMinutes(1)).AsTask().Wait());
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void RemoveAsync_WithABlankKey_Throws(string? key)
    {
        var cache = new MemoryTcgDexResponseCache();

        Should.Throw<ArgumentException>(() => cache.RemoveAsync(key!).AsTask().Wait());
    }

    [Test]
    public void EveryMethod_ObservesCancellation()
    {
        // Each entry point checks the token before doing any work. An
        // implementation that ignored it would look identical in every other
        // test, because none of them cancel.
        var cache = new MemoryTcgDexResponseCache();
        using var cancelled = new CancellationTokenSource();
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
        var time = new FakeTimeProvider();
        var cache = new MemoryTcgDexResponseCache(timeProvider: time);

        var ttl = TimeSpan.FromMinutes(5);
        await cache.SetAsync("k", SampleResponse(time.GetUtcNow()), ttl);

        // The entry is retained past freshness for revalidation, so advancing
        // by the retention multiple is what lands on the absolute expiry.
        time.Advance(TimeSpan.FromMinutes(5 * 12));

        (await cache.GetAsync("k")).ShouldBeNull();
    }}

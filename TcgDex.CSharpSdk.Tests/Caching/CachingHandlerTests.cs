namespace TcgDex.Tests.Caching;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex.Caching;

/// <summary>
/// The three request paths — fresh hit, revalidation, miss — and the guarantees
/// around them.
/// </summary>
[TestFixture]
public sealed class CachingHandlerTests
{
    private const string CardUrl = "https://api.tcgdex.net/v2/en/cards/swsh3-136";
    private const string Etag = "W/\"abc123\"";

    private static (TcgDexCachingHandler Handler, HttpClient Client, CountingHandler Inner) Build(
        FakeTimeProvider? time = null,
        TcgDexCacheOptions? options = null)
    {
        var inner = new CountingHandler();
        var handler = new TcgDexCachingHandler(
            new MemoryTcgDexResponseCache(timeProvider: time),
            options ?? new TcgDexCacheOptions(),
            time)
        {
            InnerHandler = inner,
        };

        return (handler, new HttpClient(handler), inner);
    }

    private static async Task<string> GetAsync(HttpClient client, string url = CardUrl)
    {
        using var response = await client.GetAsync(url, CancellationToken.None);
        return await response.Content.ReadAsStringAsync(CancellationToken.None);
    }

    // ----- path 1: fresh hit -----

    [Test]
    public async Task SecondRequestWithinFreshness_MakesNoNetworkCall()
    {
        var (handler, client, inner) = Build();
        inner.Respond(HttpStatusCode.OK, """{"id":"swsh3-136"}""", Etag);

        var first = await GetAsync(client);
        var second = await GetAsync(client);

        inner.Requests.Count.ShouldBe(1, "the second read should not reach the network");
        second.ShouldBe(first);
        handler.FreshHits.ShouldBe(1);
    }

    [Test]
    public async Task CachedBody_IsReadableMoreThanOnce()
    {
        // Replaying a single HttpContent would fail the second reader; each
        // cached response must get its own content.
        var (_, client, inner) = Build();
        inner.Respond(HttpStatusCode.OK, """{"id":"swsh3-136"}""", Etag);

        await GetAsync(client);

        (await GetAsync(client)).ShouldBe("""{"id":"swsh3-136"}""");
        (await GetAsync(client)).ShouldBe("""{"id":"swsh3-136"}""");
    }

    [Test]
    public async Task DifferentUrls_AreCachedSeparately()
    {
        var (_, client, inner) = Build();
        inner.Respond(HttpStatusCode.OK, """{"id":"a"}""", Etag);
        inner.Respond(HttpStatusCode.OK, """{"id":"b"}""", Etag);

        var first = await GetAsync(client, CardUrl);
        var second = await GetAsync(client, "https://api.tcgdex.net/v2/en/cards/base1-1");

        first.ShouldBe("""{"id":"a"}""");
        second.ShouldBe("""{"id":"b"}""");
        inner.Requests.Count.ShouldBe(2);
    }

    // ----- path 2: revalidation -----

    [Test]
    public async Task AfterFreshnessExpires_RevalidatesWithIfNoneMatch()
    {
        var time = new FakeTimeProvider();
        var (handler, client, inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"id":"swsh3-136"}""", Etag);
        inner.Respond(HttpStatusCode.NotModified, "", Etag);

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));
        var second = await GetAsync(client);

        inner.Requests.Count.ShouldBe(2);
        inner.Requests[1].Headers.IfNoneMatch.ToString().ShouldContain("abc123");

        // The 304 carries no body, so this can only have come from the cache.
        second.ShouldBe("""{"id":"swsh3-136"}""");
        handler.Revalidations.ShouldBe(1);
    }

    [Test]
    public async Task Revalidation_ResetsTheFreshnessWindow()
    {
        var time = new FakeTimeProvider();
        var (handler, client, inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"id":"x"}""", Etag);
        inner.Respond(HttpStatusCode.NotModified, "", Etag);

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));
        await GetAsync(client);

        // Immediately after revalidating, the entry is fresh again.
        await GetAsync(client);

        inner.Requests.Count.ShouldBe(2, "the third read should be a fresh hit");
        handler.FreshHits.ShouldBe(1);
    }

    [Test]
    public async Task WhenContentChanged_TheNewBodyReplacesTheOld()
    {
        var time = new FakeTimeProvider();
        var (_, client, inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"v":1}""", Etag);
        inner.Respond(HttpStatusCode.OK, """{"v":2}""", "W/\"def456\"");

        (await GetAsync(client)).ShouldBe("""{"v":1}""");
        time.Advance(TimeSpan.FromMinutes(10));

        (await GetAsync(client)).ShouldBe("""{"v":2}""");
    }

    [Test]
    public async Task WithoutAnEtag_AStaleEntryIsRefetchedNotRevalidated()
    {
        var time = new FakeTimeProvider();
        var (_, client, inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"v":1}""", etag: null);
        inner.Respond(HttpStatusCode.OK, """{"v":2}""", etag: null);

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));
        var second = await GetAsync(client);

        inner.Requests[1].Headers.IfNoneMatch.ShouldBeEmpty();
        second.ShouldBe("""{"v":2}""");
    }

    // ----- errors are never cached -----

    [TestCase(HttpStatusCode.NotFound)]
    [TestCase(HttpStatusCode.InternalServerError)]
    [TestCase(HttpStatusCode.ServiceUnavailable)]
    public async Task FailureResponses_AreNotCached(HttpStatusCode status)
    {
        // Caching a 404 would suppress a card that appears later, and caching a
        // 5xx would turn a blip into a persistent outage for the caller.
        var (_, client, inner) = Build();
        inner.Respond(status, """{"error":"nope"}""", etag: null);
        inner.Respond(HttpStatusCode.OK, """{"id":"swsh3-136"}""", Etag);

        using var failed = await client.GetAsync(CardUrl, CancellationToken.None);
        failed.StatusCode.ShouldBe(status);

        var second = await GetAsync(client);

        second.ShouldBe("""{"id":"swsh3-136"}""", "the failure must not have been cached");
        inner.Requests.Count.ShouldBe(2);
    }

    [Test]
    public async Task AFailureAfterASuccess_EvictsTheStaleEntry()
    {
        var time = new FakeTimeProvider();
        var (_, client, inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"v":1}""", Etag);
        inner.Respond(HttpStatusCode.NotFound, "", etag: null);
        inner.Respond(HttpStatusCode.OK, """{"v":3}""", Etag);

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));

        using var failed = await client.GetAsync(CardUrl, CancellationToken.None);
        failed.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The evicted entry means this is a full fetch, not a revalidation.
        (await GetAsync(client)).ShouldBe("""{"v":3}""");
    }

    // ----- only GET is cached -----

    [Test]
    public async Task NonGetRequests_BypassTheCacheEntirely()
    {
        var (_, client, inner) = Build();
        inner.Respond(HttpStatusCode.OK, """{"data":{}}""", Etag);
        inner.Respond(HttpStatusCode.OK, """{"data":{}}""", Etag);

        using var content1 = new StringContent("{}");
        using var content2 = new StringContent("{}");
        await client.PostAsync("https://api.tcgdex.net/v2/graphql", content1, CancellationToken.None);
        await client.PostAsync("https://api.tcgdex.net/v2/graphql", content2, CancellationToken.None);

        inner.Requests.Count.ShouldBe(2, "GraphQL POSTs must not be served from cache");
    }

    // ----- stampede protection -----

    [Test]
    public async Task ConcurrentIdenticalRequests_ShareOneNetworkCall()
    {
        var (_, client, inner) = Build();
        inner.RespondSlowly(HttpStatusCode.OK, """{"id":"swsh3-136"}""", Etag, TimeSpan.FromMilliseconds(120));

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => GetAsync(client)));

        inner.Requests.Count.ShouldBe(1, "twelve concurrent readers should share one fetch");
        results.ShouldAllBe(r => r == """{"id":"swsh3-136"}""");
    }

    [Test]
    public async Task WhenCoalescingDisabled_EachCallerFetches()
    {
        var options = new TcgDexCacheOptions { CoalesceConcurrentRequests = false };
        var (_, client, inner) = Build(options: options);
        inner.RespondSlowly(HttpStatusCode.OK, """{"id":"x"}""", Etag, TimeSpan.FromMilliseconds(60), repeat: 5);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => GetAsync(client)));

        inner.Requests.Count.ShouldBeGreaterThan(1);
    }

    [Test]
    public async Task WhenTheLeaderGetsAFailure_WaitersIssueTheirOwnRequest()
    {
        // A failure response cannot be shared: it has one content stream and is
        // never cached. Waiters must fall through to their own request rather
        // than receive nothing.
        var (handler, client, inner) = Build();

        inner.RespondSlowly(HttpStatusCode.ServiceUnavailable, """{"e":1}""", etag: null, TimeSpan.FromMilliseconds(120));
        inner.RespondSlowly(HttpStatusCode.OK, """{"id":"ok"}""", Etag, TimeSpan.FromMilliseconds(10), repeat: 5);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => client.GetAsync(CardUrl, CancellationToken.None)));

        try
        {
            // One caller saw the 503; the rest issued their own request.
            responses.Count(r => r.StatusCode == HttpStatusCode.ServiceUnavailable).ShouldBe(1);
            responses.Count(r => r.IsSuccessStatusCode).ShouldBe(3);
            inner.Requests.Count.ShouldBeGreaterThan(1);
            handler.Misses.ShouldBeGreaterThan(0);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Test]
    public async Task MissCounter_TracksFullFetches()
    {
        var (handler, client, inner) = Build();
        inner.Respond(HttpStatusCode.OK, """{"id":"a"}""", Etag);
        inner.Respond(HttpStatusCode.OK, """{"id":"b"}""", Etag);

        await GetAsync(client, CardUrl);
        await GetAsync(client, "https://api.tcgdex.net/v2/en/cards/base1-1");
        await GetAsync(client, CardUrl);

        handler.Misses.ShouldBe(2, "two distinct URLs were fetched in full");
        handler.FreshHits.ShouldBe(1);
    }

    [Test]
    public void WhenTheSharedFetchFails_EveryWaiterSeesTheFailure()
    {
        // A faulted shared task must propagate rather than leaving waiters
        // hanging on a task nobody completes.
        var (_, client, inner) = Build();
        inner.Throw(new HttpRequestException("network down"), repeat: 12);

        var task = Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Should.ThrowAsync<HttpRequestException>(() => GetAsync(client))));

        Should.NotThrow(() => task.Wait(TimeSpan.FromSeconds(10)));

        // Task.IsCompletedSuccessfully is .NET Core 2.0+ and absent from .NET
        // Framework, which this suite also targets. RanToCompletion is the
        // status that property reports on.
        task.Status.ShouldBe(TaskStatus.RanToCompletion);
    }

    // ----- responses the handler consumes are disposed -----

    [Test]
    public async Task ARevalidated304_IsDisposedRatherThanLeaked()
    {
        // The 304 exists only to say "your copy is still good". Its body is
        // empty and nothing reads it, so failing to dispose leaks the
        // connection back to the pool late — invisible to every assertion about
        // status codes and bodies, which is why this call went unprotected.
        var time = new FakeTimeProvider();
        var (_, client, inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"id":"swsh3-136"}""", Etag);

        var tracked = new TrackedResponse(HttpStatusCode.NotModified);
        inner.RespondTracked(tracked, string.Empty, Etag);

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));
        await GetAsync(client);

        tracked.WasDisposed.ShouldBeTrue();
    }

    [Test]
    public async Task ACachedResponse_IsDisposedOnceItsBodyHasBeenCopied()
    {
        // The body is copied into the cache and a fresh response is built for
        // the caller, so the original is the handler's to dispose. Same leak,
        // different path — and the more common one, since it runs on every
        // cache miss.
        var (_, client, inner) = Build();

        var tracked = new TrackedResponse(HttpStatusCode.OK);
        inner.RespondTracked(tracked, "{\"id\":\"a\"}", Etag);

        await GetAsync(client);

        tracked.WasDisposed.ShouldBeTrue();
    }

    // ----- what waiters see when the leader does not produce a cache entry ---

    [Test]
    public void WhenTheLeaderFails_EveryWaiterSeesTheFailure()
    {
        // Coalescing means one request fetches and the rest wait on its result.
        // If the leader throws and the shared task is never faulted, the
        // waiters hang forever on a task nobody completes — a deadlock, not an
        // error. Asserting each one throws is what distinguishes those.
        var (_, client, inner) = Build();

        inner.Throw(new HttpRequestException("network down"), repeat: 8);

        var attempts = Enumerable.Range(0, 8)
            .Select(_ => Should.ThrowAsync<HttpRequestException>(() => GetAsync(client)))
            .ToArray();

        var all = Task.WhenAll(attempts);

        Should.NotThrow(() => all.Wait(TimeSpan.FromSeconds(10)));
        all.Status.ShouldBe(TaskStatus.RanToCompletion);
    }

    [Test]
    public async Task WhenTheLeaderGetsAnError_WaitersAreNotServedACachedBody()
    {
        // The leader receives a 500, which is never cached, so the shared
        // result is "no cache entry" rather than a body. A mutant that treated
        // that as a cache hit would hand every waiter a response built from
        // null.
        var (_, client, inner) = Build();

        inner.Respond(HttpStatusCode.InternalServerError, "boom", null);
        inner.Respond(HttpStatusCode.InternalServerError, "boom", null);

        using var first = await client.GetAsync(CardUrl, CancellationToken.None);
        using var second = await client.GetAsync(CardUrl, CancellationToken.None);

        first.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        second.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

        // Both reached the network: an error is not cached, so the second call
        // must not be served from a stored entry.
        inner.Requests.Count.ShouldBe(2);
    }

    [Test]
    public async Task AnErrorResponse_EvictsTheEntryFromTheCache()
    {
        // Asserted against the cache rather than through the client, and the
        // first version of this test is the reason. Going through the client
        // proves nothing: the entry being evicted is already stale, so the next
        // request revalidates whether or not it was removed. That test passed
        // with the eviction deleted — it could not fail.
        //
        // The eviction still matters. ITcgDexResponseCache is a public
        // extension point, and an implementation backed by Redis or disk is
        // entitled to be told an entry is gone rather than holding it forever.
        var time = new FakeTimeProvider();
        var cache = new RecordingCache(new MemoryTcgDexResponseCache(timeProvider: time));

        var inner = new CountingHandler();
        var handler = new TcgDexCachingHandler(cache, new TcgDexCacheOptions(), time)
        {
            InnerHandler = inner,
        };

        using var client = new HttpClient(handler);

        inner.Respond(HttpStatusCode.InternalServerError, "boom", null);

        using var response = await client.GetAsync(CardUrl, CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        cache.Removed.ShouldContain(CardUrl);
    }

    // ----- policy -----

    [Test]
    public async Task CatalogEndpoints_StayFreshLongerThanCards()
    {
        var time = new FakeTimeProvider();
        var (_, client, inner) = Build(time);
        inner.Respond(HttpStatusCode.OK, """["Common"]""", Etag);

        await GetAsync(client, "https://api.tcgdex.net/v2/en/rarities");

        // Well past the card window, still inside the catalog window.
        time.Advance(TimeSpan.FromHours(6));
        await GetAsync(client, "https://api.tcgdex.net/v2/en/rarities");

        inner.Requests.Count.ShouldBe(1);
    }

    [Test]
    public async Task SingleCards_UseTheShortPricingWindow()
    {
        // A single card embeds pricing, which moves daily.
        var time = new FakeTimeProvider();
        var (handler, client, inner) = Build(time);
        inner.Respond(HttpStatusCode.OK, """{"id":"x"}""", Etag);
        inner.Respond(HttpStatusCode.NotModified, "", Etag);

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(2));
        await GetAsync(client);

        handler.Revalidations.ShouldBe(1, "a two-minute-old card should be revalidated");
    }

    [Test]
    public void TimeToLive_IsChosenByPath()
    {
        var options = new TcgDexCacheOptions();

        options.GetTimeToLive(new Uri("https://api.tcgdex.net/v2/en/rarities"))
            .ShouldBe(options.CatalogTimeToLive);
        options.GetTimeToLive(new Uri("https://api.tcgdex.net/v2/en/trainer-types"))
            .ShouldBe(options.CatalogTimeToLive);
        options.GetTimeToLive(new Uri("https://api.tcgdex.net/v2/en/cards/swsh3-136"))
            .ShouldBe(options.PricingTimeToLive);
        options.GetTimeToLive(new Uri("https://api.tcgdex.net/v2/en/cards"))
            .ShouldBe(options.DefaultTimeToLive);
        options.GetTimeToLive(new Uri("https://api.tcgdex.net/v2/en/sets/swsh3"))
            .ShouldBe(options.DefaultTimeToLive);
    }

    [Test]
    public void CustomPolicy_CanOverrideTimeToLive()
    {
        var options = new FixedTtlOptions(TimeSpan.FromSeconds(30));

        options.GetTimeToLive(new Uri("https://api.tcgdex.net/v2/en/rarities"))
            .ShouldBe(TimeSpan.FromSeconds(30));
    }

    private sealed class FixedTtlOptions(TimeSpan ttl) : TcgDexCacheOptions
    {
        public override TimeSpan GetTimeToLive(Uri requestUri) => ttl;
    }
}

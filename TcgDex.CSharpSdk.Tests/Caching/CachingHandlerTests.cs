namespace TcgDex.Tests.Caching;

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
        CountingHandler inner = new();
        TcgDexCachingHandler handler = new(
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
        using HttpResponseMessage response = await client.GetAsync(url, CancellationToken.None);
        return await response.Content.ReadAsStringAsync(CancellationToken.None);
    }

    // ----- path 1: fresh hit -----

    [Test]
    public async Task SecondRequestWithinFreshness_MakesNoNetworkCall()
    {
        (TcgDexCachingHandler? handler, HttpClient? client, CountingHandler? inner) = Build();
        inner.Respond(HttpStatusCode.OK, """{"id":"swsh3-136"}""", Etag);

        string first = await GetAsync(client);
        string second = await GetAsync(client);

        inner.Requests.Count.ShouldBe(1, "the second read should not reach the network");
        second.ShouldBe(first);
        handler.FreshHits.ShouldBe(1);
    }

    [Test]
    public async Task CachedBody_IsReadableMoreThanOnce()
    {
        // Replaying a single HttpContent would fail the second reader; each
        // cached response must get its own content.
        (TcgDexCachingHandler _, HttpClient? client, CountingHandler? inner) = Build();
        inner.Respond(HttpStatusCode.OK, """{"id":"swsh3-136"}""", Etag);

        await GetAsync(client);

        (await GetAsync(client)).ShouldBe("""{"id":"swsh3-136"}""");
        (await GetAsync(client)).ShouldBe("""{"id":"swsh3-136"}""");
    }

    [Test]
    public async Task DifferentUrls_AreCachedSeparately()
    {
        (TcgDexCachingHandler _, HttpClient? client, CountingHandler? inner) = Build();
        inner.Respond(HttpStatusCode.OK, """{"id":"a"}""", Etag);
        inner.Respond(HttpStatusCode.OK, """{"id":"b"}""", Etag);

        string first = await GetAsync(client, CardUrl);
        string second = await GetAsync(client, "https://api.tcgdex.net/v2/en/cards/base1-1");

        first.ShouldBe("""{"id":"a"}""");
        second.ShouldBe("""{"id":"b"}""");
        inner.Requests.Count.ShouldBe(2);
    }

    // ----- path 2: revalidation -----

    [Test]
    public async Task AfterFreshnessExpires_RevalidatesWithIfNoneMatch()
    {
        FakeTimeProvider time = new();
        (TcgDexCachingHandler? handler, HttpClient? client, CountingHandler? inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"id":"swsh3-136"}""", Etag);
        inner.Respond(HttpStatusCode.NotModified, "", Etag);

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));
        string second = await GetAsync(client);

        inner.Requests.Count.ShouldBe(2);
        inner.Requests[1].Headers.IfNoneMatch.ToString().ShouldContain("abc123");

        // The 304 carries no body, so this can only have come from the cache.
        second.ShouldBe("""{"id":"swsh3-136"}""");
        handler.Revalidations.ShouldBe(1);
    }

    [Test]
    public async Task ANewETagOnA304_IsTheValidatorUsedNextTime()
    {
        // RFC 9110 §15.4.5 lets a 304 carry an updated ETag. Keeping the old one
        // revalidates against a validator the server has already replaced.
        FakeTimeProvider time = new();
        (TcgDexCachingHandler _, HttpClient? client, CountingHandler? inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"id":"x"}""", "\"a\"");
        inner.Respond(HttpStatusCode.NotModified, "", "\"b\"");
        inner.Respond(HttpStatusCode.NotModified, "", "\"b\"");

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));
        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));
        await GetAsync(client);

        inner.Requests[2].Headers.IfNoneMatch.ToString().ShouldBe("\"b\"");
    }

    /// <summary>Re-sends the same request message once if the first attempt fails.</summary>
    private sealed class RetryOnceHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return await base.SendAsync(request, cancellationToken);
            }
        }
    }

    [Test]
    public async Task AResentRequest_CarriesOneIfNoneMatch()
    {
        // A handler above this one may send the same message twice. Each fetch
        // added the validator again, so the retry went out as `"a", "a"`.
        FakeTimeProvider time = new();
        (TcgDexCachingHandler handler, HttpClient _, CountingHandler inner) = Build(time);
        using HttpClient client = new(new RetryOnceHandler { InnerHandler = handler });

        inner.Respond(HttpStatusCode.OK, """{"id":"x"}""", Etag);
        inner.Throw(new HttpRequestException("reset"));
        inner.Respond(HttpStatusCode.NotModified, "", Etag);

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));
        await GetAsync(client);

        inner.Requests.Count.ShouldBe(3);
        inner.Requests[2].Headers.IfNoneMatch.Count.ShouldBe(1);
    }

    [Test]
    public async Task ACallerSetIfNoneMatch_SurvivesRevalidation()
    {
        // Only the handler's own validator is the handler's to manage; clearing
        // the collection dropped one the caller had set.
        FakeTimeProvider time = new();
        (TcgDexCachingHandler _, HttpClient? client, CountingHandler? inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"id":"x"}""", Etag);
        inner.Respond(HttpStatusCode.NotModified, "", Etag);

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));

        using HttpRequestMessage request = new(HttpMethod.Get, CardUrl);
        request.Headers.IfNoneMatch.ParseAdd("\"x\"");
        using HttpResponseMessage _ = await client.SendAsync(request, CancellationToken.None);

        string[] sent = inner.Requests[1].Headers.IfNoneMatch.Select(t => t.ToString()).ToArray();
        sent.ShouldBe(["\"x\"", Etag], ignoreOrder: true);
    }

    [Test]
    public async Task A304ValidatingTheCallersETag_IsPassedThroughNotServedFromCache()
    {
        // The caller's "x" and our stale "a" go out together. A 304 naming "x"
        // validates the caller's copy, not ours: serving "a"'s body would hand
        // back a stale representation as current.
        FakeTimeProvider time = new();
        (TcgDexCachingHandler handler, HttpClient client, CountingHandler inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"id":"old"}""", "\"a\"");
        inner.Respond(HttpStatusCode.NotModified, "", "\"x\"");
        inner.Respond(HttpStatusCode.NotModified, "", "\"a\"");

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));

        using HttpRequestMessage request = new(HttpMethod.Get, CardUrl);
        request.Headers.IfNoneMatch.ParseAdd("\"x\"");
        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await response.Content.ReadAsStringAsync(CancellationToken.None)).ShouldBeEmpty();
        handler.Revalidations.ShouldBe(0);

        // The entry is untouched: still stale, still validated by "a".
        string next = await GetAsync(client);
        inner.Requests[2].Headers.IfNoneMatch.ToString().ShouldBe("\"a\"");
        next.ShouldBe("""{"id":"old"}""");
    }

    [Test]
    public async Task A304WithoutETag_WhenTheCallerAlsoSentOne_IsPassedThrough()
    {
        // Ambiguous: it may validate the caller's copy rather than ours.
        FakeTimeProvider time = new();
        (TcgDexCachingHandler handler, HttpClient client, CountingHandler inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"id":"old"}""", "\"a\"");
        inner.Respond(HttpStatusCode.NotModified, "", null);

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));

        using HttpRequestMessage request = new(HttpMethod.Get, CardUrl);
        request.Headers.IfNoneMatch.ParseAdd("\"x\"");
        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        handler.Revalidations.ShouldBe(0);

        // Not evicted either: the next request still revalidates with "a".
        // Before the 304 branch existed, this response fell into the error
        // path, which also passed through but evicted.
        inner.Respond(HttpStatusCode.NotModified, "", "\"a\"");
        string next = await GetAsync(client);
        inner.Requests[2].Headers.IfNoneMatch.ToString().ShouldBe("\"a\"");
        next.ShouldBe("""{"id":"old"}""");
    }

    [Test]
    public async Task A304WithAWeakFormOfOurETag_WhenTheCallerAlsoSentOne_IsServedFromCache()
    {
        // If-None-Match uses weak comparison (RFC 9110 §13.1.2 / §8.8.3.2): W/"a" names "a".
        FakeTimeProvider time = new();
        (TcgDexCachingHandler handler, HttpClient client, CountingHandler inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"id":"old"}""", "\"a\"");
        inner.Respond(HttpStatusCode.NotModified, "", "W/\"a\"");

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));

        using HttpRequestMessage request = new(HttpMethod.Get, CardUrl);
        request.Headers.IfNoneMatch.ParseAdd("\"x\"");
        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(CancellationToken.None)).ShouldBe("""{"id":"old"}""");
        handler.Revalidations.ShouldBe(1);
    }

    [Test]
    public async Task ACallersWeakFormOfOurETag_IsNotSentTwice()
    {
        FakeTimeProvider time = new();
        (TcgDexCachingHandler _, HttpClient client, CountingHandler inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"id":"old"}""", "\"a\"");
        inner.Respond(HttpStatusCode.NotModified, "", "\"a\"");

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));

        using HttpRequestMessage request = new(HttpMethod.Get, CardUrl);
        request.Headers.IfNoneMatch.ParseAdd("W/\"a\"");
        using HttpResponseMessage _ = await client.SendAsync(request, CancellationToken.None);

        inner.Requests[1].Headers.IfNoneMatch.Count.ShouldBe(1);
    }

    [Test]
    public async Task A304WithoutETag_ForAnEntryStoredWithoutOne_WhenTheCallerSentOne_IsPassedThrough()
    {
        // Both ETags absent is not a match: nothing ties the 304 to our entry.
        FakeTimeProvider time = new();
        (TcgDexCachingHandler handler, HttpClient client, CountingHandler inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"id":"old"}""", null);
        inner.Respond(HttpStatusCode.NotModified, "", null);

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));

        using HttpRequestMessage request = new(HttpMethod.Get, CardUrl);
        request.Headers.IfNoneMatch.ParseAdd("\"x\"");
        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        handler.Revalidations.ShouldBe(0);
    }

    /// <summary>Sends the same message twice, running a hook in between.</summary>
    private sealed class ResendAfterHookHandler(Func<Task> between) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException)
            {
                await between();
                return await base.SendAsync(request, cancellationToken);
            }
        }
    }

    [Test]
    public async Task AResentRequest_AfterOurETagChanged_DropsTheOldTagWeAdded()
    {
        // The first send added "a". Before the resend the entry was renewed to
        // "b"; "a" is still on the message, but it is ours, not the caller's.
        FakeTimeProvider time = new();
        (TcgDexCachingHandler handler, HttpClient plain, CountingHandler inner) = Build(time);
        using HttpClient client = new(new ResendAfterHookHandler(async () =>
        {
            await GetAsync(plain);
            time.Advance(TimeSpan.FromMinutes(10));
        })
        {
            InnerHandler = handler,
        });

        inner.Respond(HttpStatusCode.OK, """{"id":"old"}""", "\"a\"");
        inner.Throw(new HttpRequestException("reset"));
        inner.Respond(HttpStatusCode.NotModified, "", "\"b\"");
        inner.Respond(HttpStatusCode.NotModified, "", "\"b\"");

        await GetAsync(plain);
        time.Advance(TimeSpan.FromMinutes(10));
        string body = await GetAsync(client);

        inner.Requests.Count.ShouldBe(4);
        inner.Requests[3].Headers.IfNoneMatch.ToString().ShouldBe("\"b\"");
        body.ShouldBe("""{"id":"old"}""");
        handler.Revalidations.ShouldBe(2);
    }

    [Test]
    public async Task AResentRequest_KeepsTheCallersTagIdenticalToOurs()
    {
        // Between sends the caller puts its own "a" ahead of the "a" we added.
        // Matching by text removed the first "a" found — the caller's. Only the
        // instance we added is ours (owner: "only remove our own value").
        FakeTimeProvider time = new();
        (TcgDexCachingHandler handler, MemoryTcgDexResponseCache cache, CountingHandler inner) = BuildSeeded(time);
        await SeedAsync(cache, time, "\"a\"");
        time.Advance(TimeSpan.FromMinutes(10));
        inner.Respond(HttpStatusCode.NotModified, "", "\"a\"");
        inner.Respond(HttpStatusCode.NotModified, "", "\"a\"");

        using HttpMessageInvoker invoker = new(handler);
        using HttpRequestMessage request = new(HttpMethod.Get, CardUrl);
        (await invoker.SendAsync(request, CancellationToken.None)).Dispose();

        EntityTagHeaderValue callers = new("\"a\"");
        List<EntityTagHeaderValue> ours = [.. request.Headers.IfNoneMatch];
        request.Headers.IfNoneMatch.Clear();
        request.Headers.IfNoneMatch.Add(callers);
        foreach (EntityTagHeaderValue tag in ours)
        {
            request.Headers.IfNoneMatch.Add(tag);
        }

        // Precondition: the collection keeps the instances it is given, on
        // every TFM — otherwise there is no "ours" to tell apart.
        request.Headers.IfNoneMatch.ShouldContain(t => ReferenceEquals(t, callers));
        request.Headers.IfNoneMatch.ShouldContain(t => ReferenceEquals(t, ours[0]));

        time.Advance(TimeSpan.FromMinutes(10));
        (await invoker.SendAsync(request, CancellationToken.None)).Dispose();

        request.Headers.IfNoneMatch.ShouldContain(t => ReferenceEquals(t, callers));
        inner.Requests[1].Headers.IfNoneMatch.ToString().ShouldBe("\"a\"");
    }

    /// <summary>A handler over a cache the test seeds directly, as a custom cache could.</summary>
    private static (TcgDexCachingHandler Handler, MemoryTcgDexResponseCache Cache, CountingHandler Inner) BuildSeeded(
        FakeTimeProvider time)
    {
        CountingHandler inner = new();
        MemoryTcgDexResponseCache cache = new(timeProvider: time);
        TcgDexCachingHandler handler = new(cache, new TcgDexCacheOptions(), time) { InnerHandler = inner };
        return (handler, cache, inner);
    }

    private static ValueTask SeedAsync(MemoryTcgDexResponseCache cache, FakeTimeProvider time, string? etag)
        => cache.SetAsync(
            CardUrl,
            new CachedResponse { Body = "{\"id\":\"old\"}"u8.ToArray(), ETag = etag, StoredAt = time.GetUtcNow() },
            TimeSpan.FromHours(1));

    [Test]
    public async Task ACallersWildcardIfNoneMatch_IsSentAlone_AndIts304PassesThrough()
    {
        // RFC 9110 §13.1.2: If-None-Match is "*" / #entity-tag, never both.
        FakeTimeProvider time = new();
        (TcgDexCachingHandler handler, HttpClient client, CountingHandler inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"id":"old"}""", "\"a\"");
        inner.Respond(HttpStatusCode.NotModified, "", "\"a\"");
        inner.Respond(HttpStatusCode.NotModified, "", "\"a\"");

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));

        using HttpRequestMessage request = new(HttpMethod.Get, CardUrl);
        request.Headers.IfNoneMatch.ParseAdd("*");
        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        inner.Requests[1].Headers.GetValues("If-None-Match").ShouldBe(["*"]);
        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        handler.Revalidations.ShouldBe(0);

        // Untouched: still stale, still validated by "a".
        (await GetAsync(client)).ShouldBe("""{"id":"old"}""");
        inner.Requests[2].Headers.IfNoneMatch.ToString().ShouldBe("\"a\"");
    }

    [Test]
    public async Task AResentRequest_WithANonCanonicalCachedETag_DropsTheOldTagWeAdded()
    {
        // " \"a\"" is stored as a custom cache might hold it; the header
        // normalises it to "\"a\"", so removing by the raw string missed it.
        FakeTimeProvider time = new();
        (TcgDexCachingHandler handler, MemoryTcgDexResponseCache cache, CountingHandler inner) = BuildSeeded(time);
        await SeedAsync(cache, time, " \"a\"");
        time.Advance(TimeSpan.FromMinutes(10));

        using HttpClient client = new(new ResendAfterHookHandler(async () =>
        {
            await SeedAsync(cache, time, "\"b\"");
            time.Advance(TimeSpan.FromMinutes(10));
        })
        {
            InnerHandler = handler,
        });

        inner.Throw(new HttpRequestException("reset"));
        inner.Respond(HttpStatusCode.NotModified, "", "\"b\"");

        await GetAsync(client);

        inner.Requests[1].Headers.IfNoneMatch.ToString().ShouldBe("\"b\"");
    }

    [Test]
    public async Task AResentRequest_ServedFresh_NoLongerCarriesTheTagWeAdded()
    {
        // The first send added "a"; the resend is a fresh hit and never fetches,
        // so only clearing on every path takes "a" back off the message.
        FakeTimeProvider time = new();
        (TcgDexCachingHandler handler, MemoryTcgDexResponseCache cache, CountingHandler inner) = BuildSeeded(time);
        await SeedAsync(cache, time, "\"a\"");
        time.Advance(TimeSpan.FromMinutes(10));

        using HttpClient client = new(new ResendAfterHookHandler(() => SeedAsync(cache, time, "\"b\"").AsTask())
        {
            InnerHandler = handler,
        });

        inner.Throw(new HttpRequestException("reset"));

        using HttpRequestMessage request = new(HttpMethod.Get, CardUrl);
        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        inner.Requests.Count.ShouldBe(1);
        handler.FreshHits.ShouldBe(1);
        request.Headers.IfNoneMatch.ShouldBeEmpty();
    }

    [Test]
    public async Task AnEntryStoredWithAnEmptyETag_IsRefetchedWithoutAValidator()
    {
        FakeTimeProvider time = new();
        (TcgDexCachingHandler handler, MemoryTcgDexResponseCache cache, CountingHandler inner) = BuildSeeded(time);
        await SeedAsync(cache, time, "");
        time.Advance(TimeSpan.FromMinutes(10));
        inner.Respond(HttpStatusCode.OK, """{"id":"new"}""", null);

        using HttpClient client = new(handler);
        string body = await GetAsync(client);

        inner.Requests[0].Headers.IfNoneMatch.ShouldBeEmpty();
        body.ShouldBe("""{"id":"new"}""");
    }

    [Test]
    public async Task AnUnparsableStoredETag_IsNeverSent_AndNeverMatchesA304()
    {
        // "abc" has no quotes, so it is not an entity-tag. It cannot be added to
        // If-None-Match, and the caller's 304 is not ours.
        FakeTimeProvider time = new();
        (TcgDexCachingHandler handler, MemoryTcgDexResponseCache cache, CountingHandler inner) = BuildSeeded(time);
        await SeedAsync(cache, time, "abc");
        time.Advance(TimeSpan.FromMinutes(10));
        inner.Respond(HttpStatusCode.NotModified, "", "\"x\"");

        using HttpClient client = new(handler);
        using HttpRequestMessage request = new(HttpMethod.Get, CardUrl);
        request.Headers.IfNoneMatch.ParseAdd("\"x\"");
        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        inner.Requests[0].Headers.GetValues("If-None-Match").ShouldBe(["\"x\""]);
        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        handler.Revalidations.ShouldBe(0);
    }

    [Test]
    public async Task AnUnparsableCallerIfNoneMatch_IsKeptBesideOurs()
    {
        FakeTimeProvider time = new();
        (TcgDexCachingHandler _, HttpClient client, CountingHandler inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"id":"old"}""", "\"a\"");
        inner.Respond(HttpStatusCode.NotModified, "", "\"a\"");

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));

        using HttpRequestMessage request = new(HttpMethod.Get, CardUrl);
        request.Headers.TryAddWithoutValidation("If-None-Match", "abc").ShouldBeTrue();
        using HttpResponseMessage _ = await client.SendAsync(request, CancellationToken.None);

        string.Join(",", inner.Requests[1].Headers.GetValues("If-None-Match")).ShouldContain("abc");
        string.Join(",", inner.Requests[1].Headers.GetValues("If-None-Match")).ShouldContain("\"a\"");
    }

    /// <summary>A cache whose eviction fails.</summary>
    private sealed class ThrowingRemoveCache : ITcgDexResponseCache
    {
        public ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default)
            => new((CachedResponse?)null);

        public ValueTask SetAsync(string key, CachedResponse response, TimeSpan timeToLive, CancellationToken cancellationToken = default)
            => default;

        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("cache backend down");
    }

    [Test]
    public async Task AFailingEviction_StillDisposesTheErrorResponse()
    {
        CountingHandler inner = new();
        TrackedResponse tracked = new(HttpStatusCode.InternalServerError);
        inner.RespondTracked(tracked, "", null);

        using HttpClient client = new(new TcgDexCachingHandler(new ThrowingRemoveCache()) { InnerHandler = inner });

        await Should.ThrowAsync<InvalidOperationException>(() => client.GetAsync(CardUrl, CancellationToken.None));

        tracked.WasDisposed.ShouldBeTrue();
    }

    [Test]
    public async Task Revalidation_ResetsTheFreshnessWindow()
    {
        FakeTimeProvider time = new();
        (TcgDexCachingHandler? handler, HttpClient? client, CountingHandler? inner) = Build(time);

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
        FakeTimeProvider time = new();
        (TcgDexCachingHandler _, HttpClient? client, CountingHandler? inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"v":1}""", Etag);
        inner.Respond(HttpStatusCode.OK, """{"v":2}""", "W/\"def456\"");

        (await GetAsync(client)).ShouldBe("""{"v":1}""");
        time.Advance(TimeSpan.FromMinutes(10));

        (await GetAsync(client)).ShouldBe("""{"v":2}""");
    }

    [Test]
    public async Task WithoutAnEtag_AStaleEntryIsRefetchedNotRevalidated()
    {
        FakeTimeProvider time = new();
        (TcgDexCachingHandler _, HttpClient? client, CountingHandler? inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"v":1}""", etag: null);
        inner.Respond(HttpStatusCode.OK, """{"v":2}""", etag: null);

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));
        string second = await GetAsync(client);

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
        (TcgDexCachingHandler _, HttpClient? client, CountingHandler? inner) = Build();
        inner.Respond(status, """{"error":"nope"}""", etag: null);
        inner.Respond(HttpStatusCode.OK, """{"id":"swsh3-136"}""", Etag);

        using HttpResponseMessage failed = await client.GetAsync(CardUrl, CancellationToken.None);
        failed.StatusCode.ShouldBe(status);

        string second = await GetAsync(client);

        second.ShouldBe("""{"id":"swsh3-136"}""", "the failure must not have been cached");
        inner.Requests.Count.ShouldBe(2);
    }

    [Test]
    public async Task AFailureAfterASuccess_EvictsTheStaleEntry()
    {
        FakeTimeProvider time = new();
        (TcgDexCachingHandler _, HttpClient? client, CountingHandler? inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"v":1}""", Etag);
        inner.Respond(HttpStatusCode.NotFound, "", etag: null);
        inner.Respond(HttpStatusCode.OK, """{"v":3}""", Etag);

        await GetAsync(client);
        time.Advance(TimeSpan.FromMinutes(10));

        using HttpResponseMessage failed = await client.GetAsync(CardUrl, CancellationToken.None);
        failed.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The evicted entry means this is a full fetch, not a revalidation.
        (await GetAsync(client)).ShouldBe("""{"v":3}""");
    }

    // ----- only GET is cached -----

    [Test]
    public async Task NonGetRequests_BypassTheCacheEntirely()
    {
        (TcgDexCachingHandler _, HttpClient? client, CountingHandler? inner) = Build();
        inner.Respond(HttpStatusCode.OK, """{"data":{}}""", Etag);
        inner.Respond(HttpStatusCode.OK, """{"data":{}}""", Etag);

        using StringContent content1 = new("{}");
        using StringContent content2 = new("{}");
        await client.PostAsync("https://api.tcgdex.net/v2/graphql", content1, CancellationToken.None);
        await client.PostAsync("https://api.tcgdex.net/v2/graphql", content2, CancellationToken.None);

        inner.Requests.Count.ShouldBe(2, "GraphQL POSTs must not be served from cache");
    }

    // ----- stampede protection -----

    [Test]
    public async Task ConcurrentIdenticalRequests_ShareOneNetworkCall()
    {
        (TcgDexCachingHandler _, HttpClient? client, CountingHandler? inner) = Build();
        inner.RespondSlowly(HttpStatusCode.OK, """{"id":"swsh3-136"}""", Etag, TimeSpan.FromMilliseconds(120));

        string[] results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => GetAsync(client)));

        inner.Requests.Count.ShouldBe(1, "twelve concurrent readers should share one fetch");
        results.ShouldAllBe(r => r == """{"id":"swsh3-136"}""");
    }

    [Test]
    public async Task WhenCoalescingDisabled_EachCallerFetches()
    {
        TcgDexCacheOptions options = new() { CoalesceConcurrentRequests = false };
        (TcgDexCachingHandler _, HttpClient? client, CountingHandler? inner) = Build(options: options);
        inner.RespondSlowly(HttpStatusCode.OK, """{"id":"x"}""", Etag, TimeSpan.FromMilliseconds(60), repeat: 5);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => GetAsync(client)));

        // Exactly five, not "more than one". This is the paired opposite of the
        // coalescing test above, which asserts 1 — so together they measure the
        // direction *and* the size of the effect. "Greater than one" would also
        // be satisfied by partial coalescing, which is neither behaviour and
        // would mean the option only half works.
        inner.Requests.Count.ShouldBe(5, "coalescing off means every caller fetches for itself");
    }

    [Test]
    public async Task WhenTheLeaderGetsAFailure_WaitersIssueTheirOwnRequest()
    {
        // A failure response cannot be shared: it has one content stream and is
        // never cached. Waiters must fall through to their own request rather
        // than receive nothing.
        (TcgDexCachingHandler? handler, HttpClient? client, CountingHandler? inner) = Build();

        inner.RespondSlowly(HttpStatusCode.ServiceUnavailable, """{"e":1}""", etag: null, TimeSpan.FromMilliseconds(120));
        inner.RespondSlowly(HttpStatusCode.OK, """{"id":"ok"}""", Etag, TimeSpan.FromMilliseconds(10), repeat: 5);

        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 4)
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
            foreach (HttpResponseMessage? response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Test]
    public async Task MissCounter_TracksFullFetches()
    {
        (TcgDexCachingHandler? handler, HttpClient? client, CountingHandler? inner) = Build();
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
        (TcgDexCachingHandler _, HttpClient? client, CountingHandler? inner) = Build();
        inner.Throw(new HttpRequestException("network down"), repeat: 12);

        Task<HttpRequestException[]> task = Task.WhenAll(Enumerable.Range(0, 12)
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
        FakeTimeProvider time = new();
        (TcgDexCachingHandler _, HttpClient? client, CountingHandler? inner) = Build(time);

        inner.Respond(HttpStatusCode.OK, """{"id":"swsh3-136"}""", Etag);

        TrackedResponse tracked = new(HttpStatusCode.NotModified);
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
        (TcgDexCachingHandler _, HttpClient? client, CountingHandler? inner) = Build();

        TrackedResponse tracked = new(HttpStatusCode.OK);
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
        (TcgDexCachingHandler _, HttpClient? client, CountingHandler? inner) = Build();

        inner.Throw(new HttpRequestException("network down"), repeat: 8);

        Task<HttpRequestException>[] attempts = Enumerable.Range(0, 8)
            .Select(_ => Should.ThrowAsync<HttpRequestException>(() => GetAsync(client)))
            .ToArray();

        Task<HttpRequestException[]> all = Task.WhenAll(attempts);

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
        (TcgDexCachingHandler _, HttpClient? client, CountingHandler? inner) = Build();

        inner.Respond(HttpStatusCode.InternalServerError, "boom", null);
        inner.Respond(HttpStatusCode.InternalServerError, "boom", null);

        using HttpResponseMessage first = await client.GetAsync(CardUrl, CancellationToken.None);
        using HttpResponseMessage second = await client.GetAsync(CardUrl, CancellationToken.None);

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
        FakeTimeProvider time = new();
        RecordingCache cache = new(new MemoryTcgDexResponseCache(timeProvider: time));

        CountingHandler inner = new();
        TcgDexCachingHandler handler = new(cache, new TcgDexCacheOptions(), time)
        {
            InnerHandler = inner,
        };

        using HttpClient client = new(handler);

        inner.Respond(HttpStatusCode.InternalServerError, "boom", null);

        using HttpResponseMessage response = await client.GetAsync(CardUrl, CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        cache.Removed.ShouldContain(CardUrl);
    }

    // ----- which time-to-live a path gets -----

    [TestCase("https://api.tcgdex.net/v2/en/cards/swsh3-136", true, TestName = "Ttl_SingleCard")]
    [TestCase("https://api.tcgdex.net/v2/en/cards/exu-%21", true, TestName = "Ttl_SingleCardOddId")]
    [TestCase("https://api.tcgdex.net/v2/en/cards", false, TestName = "Ttl_CardList")]
    [TestCase("https://api.tcgdex.net/v2/en/cards/", false, TestName = "Ttl_CardsWithTrailingSlashOnly")]
    [TestCase("https://api.tcgdex.net/v2/en/sets/swsh3", false, TestName = "Ttl_Set")]
    public void ASingleCardPath_GetsThePricingLifetime(string url, bool isSingleCard)
    {
        // A single card carries pricing and so gets a shorter lifetime than
        // anything else. The detection is `indexOf("/cards/") >= 0 && there is
        // something after it`, and every part of that was unprotected: the
        // index comparison, the arithmetic, and the segment being searched for.
        //
        // The trailing-slash case is the one that separates the two halves —
        // "/cards/" is present but nothing follows it, so it is a list rather
        // than a card.
        TcgDexCacheOptions options = new()
        {
            PricingTimeToLive = TimeSpan.FromSeconds(30),
            DefaultTimeToLive = TimeSpan.FromHours(1),
        };

        TimeSpan actual = options.GetTimeToLive(new Uri(url));

        actual.ShouldBe(isSingleCard ? TimeSpan.FromSeconds(30) : TimeSpan.FromHours(1));
    }

    [Test]
    public void GetTimeToLive_WithANullUri_Throws()
    {
        // TcgDexCacheOptions is public and GetTimeToLive is virtual for callers
        // who want their own policy, so this guard is part of that contract.
        Should.Throw<ArgumentNullException>(() => new TcgDexCacheOptions().GetTimeToLive(null!));
    }

    // ----- policy -----

    [Test]
    public async Task CatalogEndpoints_StayFreshLongerThanCards()
    {
        FakeTimeProvider time = new();
        (TcgDexCachingHandler _, HttpClient? client, CountingHandler? inner) = Build(time);
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
        FakeTimeProvider time = new();
        (TcgDexCachingHandler? handler, HttpClient? client, CountingHandler? inner) = Build(time);
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
        TcgDexCacheOptions options = new();

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
        FixedTtlOptions options = new(TimeSpan.FromSeconds(30));

        options.GetTimeToLive(new Uri("https://api.tcgdex.net/v2/en/rarities"))
            .ShouldBe(TimeSpan.FromSeconds(30));
    }

    private sealed class FixedTtlOptions(TimeSpan ttl) : TcgDexCacheOptions
    {
        public override TimeSpan GetTimeToLive(Uri requestUri) => ttl;
    }
}

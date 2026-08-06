namespace TcgDex.IntegrationTests;

using TcgDex.Caching;

/// <summary>
/// Caching against the live API.
/// </summary>
/// <remarks>
/// The unit tests prove the handler behaves correctly given a <c>304</c>. These
/// prove TCGdex actually issues one — the entire revalidation design rests on
/// that, and it is a property of the service rather than of this SDK.
/// </remarks>
[TestFixture]
public sealed class CachingTests : LiveApiFixture
{
    private static (ITcgDexClient Client, TcgDexCachingHandler Cache, HttpClient Http) CreateCachingClient(
        TcgDexCacheOptions? options = null)
    {
        var caching = new TcgDexCachingHandler(
            new MemoryTcgDexResponseCache(),
            options ?? new TcgDexCacheOptions())
        {
            InnerHandler = new HttpClientHandler(),
        };

        var http = new HttpClient(caching);

        return (new TcgDexClient(http, new TcgDexOptions()), caching, http);
    }

    [Test]
    public async Task TheApiIssuesAnETag()
    {
        // If this ever stops being true, revalidation silently degrades into
        // re-downloading and this test is the warning.
        using var http = new HttpClient();
        using var response = await http.GetAsync(
            new Uri("https://api.tcgdex.net/v2/en/cards/swsh3-136"),
            Timeout);

        response.Headers.ETag.ShouldNotBeNull("the caching layer depends on this");
    }

    [Test]
    public async Task TheApiHonoursIfNoneMatch()
    {
        // The load-bearing fact: an unchanged resource costs a 304 and no body.
        //
        // Asserted against the rarity list rather than a card. A card embeds
        // pricing that TCGdex updates server-side, so its ETag can legitimately
        // change between two reads and a 200 would be the correct answer — which
        // is a property of the data, not of conditional-request support.
        using var http = new HttpClient();
        var uri = new Uri("https://api.tcgdex.net/v2/en/rarities");

        using var first = await http.GetAsync(uri, Timeout);
        var etag = first.Headers.ETag.ShouldNotBeNull();

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.IfNoneMatch.Add(etag);

        using var second = await http.SendAsync(request, Timeout);

        second.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotModified);
        (await second.Content.ReadAsByteArrayAsync(Timeout)).ShouldBeEmpty();
    }

    [Test]
    public async Task RepeatedReads_HitTheCacheNotTheNetwork()
    {
        var (client, cache, http) = CreateCachingClient();

        using (http)
        {
            var first = await client.Cards.GetAsync("swsh3-136", Timeout);
            var second = await client.Cards.GetAsync("swsh3-136", Timeout);
            var third = await client.Cards.GetAsync("swsh3-136", Timeout);

            first.ShouldNotBeNull().Name.ShouldBe("Furret");
            second.ShouldNotBeNull().Name.ShouldBe("Furret");
            third.ShouldNotBeNull().Name.ShouldBe("Furret");

            cache.Misses.ShouldBe(1);
            cache.FreshHits.ShouldBe(2);
        }
    }

    [Test]
    public async Task WhenFreshnessExpires_TheApiRevalidatesRatherThanResending()
    {
        // Deliberately a catalog endpoint rather than a card. A card carries
        // market pricing, which TCGdex updates server-side; if an update lands
        // between the two reads the ETag changes and a 200 is the *correct*
        // answer, which made an earlier version of this test intermittently
        // fail for a reason that was never a defect. The rarity list has no
        // volatile data, so its ETag is stable across two immediate requests.
        var options = new TcgDexCacheOptions
        {
            DefaultTimeToLive = TimeSpan.Zero,
            PricingTimeToLive = TimeSpan.Zero,
            CatalogTimeToLive = TimeSpan.Zero,
        };

        var (client, cache, http) = CreateCachingClient(options);

        using (http)
        {
            var first = await client.Catalog.RaritiesAsync(Timeout);
            var second = await client.Catalog.RaritiesAsync(Timeout);

            second.ShouldBe(first);
            cache.Revalidations.ShouldBe(1, "the second read should have been a 304");
            cache.Misses.ShouldBe(1, "only the first read should have downloaded a body");
        }
    }

    [Test]
    public async Task AVolatileResource_IsStillServedCorrectlyWhetherItRevalidatesOrRefetches()
    {
        // The companion to the test above: for a resource whose content can
        // change between reads, the SDK must return the right answer either way.
        // Only the outcome is asserted, because which path the server chooses is
        // the server's business.
        var options = new TcgDexCacheOptions { PricingTimeToLive = TimeSpan.Zero };
        var (client, cache, http) = CreateCachingClient(options);

        using (http)
        {
            var first = await client.Cards.GetAsync("swsh3-136", Timeout);
            var second = await client.Cards.GetAsync("swsh3-136", Timeout);

            first.ShouldNotBeNull().Name.ShouldBe("Furret");
            second.ShouldNotBeNull().Name.ShouldBe("Furret");

            // Whichever path was taken, the entry was not served stale.
            (cache.Revalidations + cache.Misses).ShouldBe(2);
            cache.FreshHits.ShouldBe(0, "a zero freshness window must never serve without asking");
        }
    }

    [Test]
    public async Task CachedResponses_DeserializeIdentically()
    {
        // A replayed body must produce the same model as the original, including
        // the awkward shapes.
        var (client, _, http) = CreateCachingClient();

        using (http)
        {
            var live = await client.Cards.GetAsync("swsh3-136", Timeout);
            var cached = await client.Cards.GetAsync("swsh3-136", Timeout);

            live.ShouldNotBeNull();
            cached.ShouldNotBeNull();

            cached.Name.ShouldBe(live.Name);
            cached.Hp.ShouldBe(live.Hp);
            cached.Attacks.Count.ShouldBe(live.Attacks.Count);
            cached.Set.Id.ShouldBe(live.Set.Id);
            var livePrintings = live.Pricing?.Tcgplayer?.Printings.Count;
            var cachedPrintings = cached.Pricing?.Tcgplayer?.Printings.Count;
            cachedPrintings.ShouldBe(livePrintings);
        }
    }

    [Test]
    public async Task LargeResponses_AreWorthCaching()
    {
        // A full set is around 22 KB, so this is the case the cache earns its
        // keep on.
        var (client, cache, http) = CreateCachingClient();

        using (http)
        {
            var first = await client.Sets.GetAsync("swsh3", Timeout);
            var second = await client.Sets.GetAsync("swsh3", Timeout);

            first.ShouldNotBeNull().Cards.Count.ShouldBeGreaterThan(200);
            second.ShouldNotBeNull().Cards.Count.ShouldBe(first.Cards.Count);

            cache.Misses.ShouldBe(1);
            cache.FreshHits.ShouldBe(1);
        }
    }

    [Test]
    public async Task ConcurrentReads_ShareOneRequest()
    {
        var (client, cache, http) = CreateCachingClient();

        using (http)
        {
            var reads = Enumerable.Range(0, 10)
                .Select(_ => client.Cards.GetAsync("swsh3-136", Timeout));

            var cards = await Task.WhenAll(reads);

            cards.ShouldAllBe(c => c != null && c.Name == "Furret");
            cache.Misses.ShouldBe(1, "ten concurrent readers should share one fetch");
        }
    }

    [Test]
    public async Task MissingCard_IsNotCached()
    {
        var (client, _, http) = CreateCachingClient();

        using (http)
        {
            (await client.Cards.GetAsync("no-such-card-999", Timeout)).ShouldBeNull();

            // A cached 404 would suppress a card that later exists.
            (await client.Cards.GetAsync("swsh3-136", Timeout)).ShouldNotBeNull();
        }
    }

    [Test]
    public async Task GraphQlRequests_AreNotCached()
    {
        // GraphQL is a POST, so it must bypass the cache entirely.
        var (client, cache, http) = CreateCachingClient();

        using (http)
        {
            await client.Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" }, cancellationToken: Timeout);
            await client.Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" }, cancellationToken: Timeout);

            cache.FreshHits.ShouldBe(0);
            cache.Misses.ShouldBe(0);
        }
    }
}

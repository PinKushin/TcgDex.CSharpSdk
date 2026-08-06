namespace TcgDex.Tests;

using System.Net;
using System.Net.Http;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using TcgDex.Caching;
using TcgDex.Models;
using TcgDex.Querying;
using TcgDex.Serialization;
using TcgDex.Tests.Caching;
using TcgDex.Tests.Http;

/// <summary>
/// The untested half of conditions that line coverage cannot see.
/// </summary>
/// <remarks>
/// A line containing <c>flipped ? a : b</c> counts as covered the moment it runs
/// once, even though half its behaviour was never exercised. Every case here
/// targets a branch where taking the other path would produce a wrong result
/// rather than merely a different one.
/// </remarks>
[TestFixture]
public sealed class BranchCoverageTests
{
    // ----- operand orientation: the operator must flip with the operands -----

    [Test]
    public void ConstantOnTheLeft_FlipsEveryRelationalOperator()
    {
        // `100 <= c.Hp` means hp >= 100. Reading the node type without flipping
        // would emit `lte`, quietly returning the complement of what was asked.
        new CardQuery().Where(c => 100 <= c.Hp).ToQueryString().ShouldBe("hp=gte:100");
        new CardQuery().Where(c => 100 >= c.Hp).ToQueryString().ShouldBe("hp=lte:100");
        new CardQuery().Where(c => 100 < c.Hp).ToQueryString().ShouldBe("hp=gt:100");
        new CardQuery().Where(c => 100 > c.Hp).ToQueryString().ShouldBe("hp=lt:100");
    }

    [Test]
    public void MemberOnTheLeft_KeepsEveryRelationalOperator()
    {
        new CardQuery().Where(c => c.Hp >= 100).ToQueryString().ShouldBe("hp=gte:100");
        new CardQuery().Where(c => c.Hp <= 100).ToQueryString().ShouldBe("hp=lte:100");
        new CardQuery().Where(c => c.Hp > 100).ToQueryString().ShouldBe("hp=gt:100");
        new CardQuery().Where(c => c.Hp < 100).ToQueryString().ShouldBe("hp=lt:100");
    }

    // ----- GraphQL argument assembly -----

    private static async Task<string> GraphQlQueryAsync(
        CardFilter filter,
        int? page = null,
        int? itemsPerPage = null)
    {
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, """{"data":{"cards":[]}}""");
        var client = new TcgDexClient(new HttpClient(handler), new TcgDexOptions());

        await client.Cards.SearchDetailedAsync(filter, page, itemsPerPage, CancellationToken.None);

        using var document = JsonDocument.Parse(handler.SingleRequestBody);
        return document.RootElement.GetProperty("query").GetString()!;
    }

    [Test]
    public async Task PaginationWithoutFilters_OmitsTheLeadingComma()
    {
        // With no filter the pagination argument is first, so emitting a
        // separator would produce `cards(,pagination:{…})` — a syntax error.
        var query = await GraphQlQueryAsync(new CardFilter(), page: 2, itemsPerPage: 10);

        query.ShouldContain("cards(pagination:{page:2,itemsPerPage:10})");
    }

    [Test]
    public async Task PageWithoutItemsPerPage_EmitsOnlyThePage()
    {
        var query = await GraphQlQueryAsync(new CardFilter(), page: 3);

        query.ShouldContain("pagination:{page:3}");
        query.ShouldNotContain("itemsPerPage");
    }

    [Test]
    public async Task ItemsPerPageWithoutPage_OmitsTheSeparator()
    {
        // The comma between page and itemsPerPage is conditional on page being
        // present; emitting it unconditionally breaks this case.
        var query = await GraphQlQueryAsync(new CardFilter(), itemsPerPage: 25);

        query.ShouldContain("pagination:{itemsPerPage:25}");

        // Anchored on the opening brace and case-sensitive: "itemsPerPage:"
        // contains "page:" under Shouldly's default case-insensitive match.
        query.ShouldNotContain("{page:", Case.Sensitive);
    }

    [Test]
    public async Task NeitherFilterNorPagination_OmitsTheArgumentListEntirely()
    {
        var query = await GraphQlQueryAsync(new CardFilter());

        query.ShouldContain("{ cards {");
    }

    // ----- pricing converter: null metadata -----

    private static T Deserialize<T>(string json)
        where T : notnull
    {
        var info = (JsonTypeInfo<T>)TcgDexJsonContext.Default.Options.GetTypeInfo(typeof(T));
        return JsonSerializer.Deserialize(json, info)!;
    }

    private static string Serialize<T>(T value)
        where T : notnull
    {
        var info = (JsonTypeInfo<T>)TcgDexJsonContext.Default.Options.GetTypeInfo(typeof(T));
        return JsonSerializer.Serialize(value, info);
    }

    [Test]
    public void PricingWithNullUnit_ReadsAsNullRatherThanThrowing()
    {
        var pricing = Deserialize<TcgPlayerPricing>("""{"unit":null,"normal":{"marketPrice":1}}""");

        pricing.Unit.ShouldBeNull();
        pricing["normal"].ShouldNotBeNull().MarketPrice.ShouldBe(1m);
    }

    [Test]
    public void PricingWithNullUpdated_ReadsAsNull()
    {
        var pricing = Deserialize<TcgPlayerPricing>("""{"unit":"USD","updated":null}""");

        pricing.Updated.ShouldBeNull();
        pricing.Unit.ShouldBe("USD");
    }

    [Test]
    public void PricingWithBothTimestampAndUnit_ReadsBoth()
    {
        var pricing = Deserialize<TcgPlayerPricing>(
            """{"unit":"USD","updated":"2026-08-05T08:03:54.324Z"}""");

        pricing.Unit.ShouldBe("USD");
        pricing.Updated.ShouldNotBeNull();
    }

    [Test]
    public void WritingPricingWithNoUnit_OmitsTheProperty()
    {
        // The write path branches on Unit being present; the absent case is the
        // one a hand-built instance hits.
        var json = Serialize(new TcgPlayerPricing { Unit = null });

        Deserialize<TcgPlayerPricing>(json).Unit.ShouldBeNull();
    }

    // ----- cached response reconstruction -----

    [Test]
    public async Task ACachedResponseWithoutAnETagOrContentType_IsStillReplayable()
    {
        // Both are optional on the stored entry, and rebuilding must not assume
        // either is present.
        var inner = new CountingHandler();
        inner.Respond(HttpStatusCode.OK, """{"id":"x"}""", etag: null);

        var handler = new TcgDexCachingHandler(new MemoryTcgDexResponseCache())
        {
            InnerHandler = inner,
        };

        using var client = new HttpClient(handler);
        const string Url = "https://api.tcgdex.net/v2/en/cards/x";

        await client.GetAsync(Url, CancellationToken.None);

        using var replayed = await client.GetAsync(Url, CancellationToken.None);
        (await replayed.Content.ReadAsStringAsync(CancellationToken.None)).ShouldBe("""{"id":"x"}""");
        replayed.Headers.ETag.ShouldBeNull();
    }

    [Test]
    public async Task ACachedResponseWithAnETag_ReplaysIt()
    {
        var inner = new CountingHandler();
        inner.Respond(HttpStatusCode.OK, """{"id":"x"}""", "W/\"abc\"");

        var handler = new TcgDexCachingHandler(new MemoryTcgDexResponseCache())
        {
            InnerHandler = inner,
        };

        using var client = new HttpClient(handler);
        const string Url = "https://api.tcgdex.net/v2/en/cards/x";

        await client.GetAsync(Url, CancellationToken.None);

        using var replayed = await client.GetAsync(Url, CancellationToken.None);
        replayed.Headers.ETag.ShouldNotBeNull();
        replayed.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
    }

    // ----- problem document -----

    [Test]
    public void AProblemWithNoType_IsNotALanguageError()
    {
        // Guards the null half of `Type is not null && …`.
        new TcgDexProblem().IsLanguageError.ShouldBeFalse();
        new TcgDexProblem { Type = "https://tcgdex.dev/errors/not-found" }.IsLanguageError.ShouldBeFalse();
        new TcgDexProblem { Type = "https://tcgdex.dev/errors/language-invalid" }.IsLanguageError.ShouldBeTrue();
    }

    [Test]
    public void DescribeFallsBackThroughEveryField()
    {
        // Each rung of `Details ?? Title ?? Error ?? default`.
        new TcgDexProblem { Details = "d", Title = "t", Error = "e" }.Describe().ShouldBe("d");
        new TcgDexProblem { Title = "t", Error = "e" }.Describe().ShouldBe("t");
        new TcgDexProblem { Error = "e" }.Describe().ShouldBe("e");
        new TcgDexProblem().Describe().ShouldNotBeNullOrWhiteSpace();
    }

    // ----- language validation -----

    [Test]
    public void IsSupported_HandlesNullAndCasing()
    {
        TcgDexLanguages.IsSupported(null).ShouldBeFalse();
        TcgDexLanguages.IsSupported("").ShouldBeFalse();
        TcgDexLanguages.IsSupported("EN").ShouldBeTrue("the API treats the segment case-insensitively");
        TcgDexLanguages.IsSupported("en").ShouldBeTrue();
        TcgDexLanguages.IsSupported("zz").ShouldBeFalse();
    }

    // ----- failure description -----

    [Test]
    public void AFailureWithNoParseableBody_FallsBackToTheReasonPhrase()
    {
        // `problem?.Describe() ?? ReasonPhrase ?? default` — the middle rung is
        // reached only when the body cannot be parsed at all.
        var handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.ServiceUnavailable, "not json at all");

        var client = new TcgDexClient(new HttpClient(handler), new TcgDexOptions());

        var exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Result;

        exception.Problem.ShouldBeNull();
        exception.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void AFailureWithAParseableBody_UsesTheProblemDescription()
    {
        var handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.BadGateway, """{"title":"upstream exploded"}""");

        var client = new TcgDexClient(new HttpClient(handler), new TcgDexOptions());

        var exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Result;

        exception.Message.ShouldContain("upstream exploded");
    }

    // ----- collection guards on every model that has one -----

    [Test]
    public void EveryCollectionCoercesNullToEmpty()
    {
        // The same guard as on Card, on the models that also carry collections.
        // System.Text.Json's source generator discards property initializers, so
        // without this an omitted array deserializes to null.
        new Set { Id = "s", Name = "S", Cards = null! }.Cards.ShouldBeEmpty();
        new Serie { Id = "r", Name = "R", Sets = null! }.Sets.ShouldBeEmpty();
        new DetailedVariant { Stamp = null! }.Stamp.ShouldBeEmpty();
        new Attack { Name = "a", Cost = null! }.Cost.ShouldBeEmpty();
    }

    [Test]
    public void EveryCollectionKeepsASuppliedValue()
    {
        // The other half: the guard must not discard real data.
        new Set { Id = "s", Name = "S", Cards = [new CardBrief { Id = "c", LocalId = "1", Name = "C" }] }
            .Cards.Count.ShouldBe(1);
        new Serie { Id = "r", Name = "R", Sets = [new SetBrief { Id = "s", Name = "S" }] }
            .Sets.Count.ShouldBe(1);
        new DetailedVariant { Stamp = ["set-logo"] }.Stamp.Count.ShouldBe(1);
        new Attack { Name = "a", Cost = ["Grass"] }.Cost.Count.ShouldBe(1);
    }

    // ----- constructor null guards -----

    [Test]
    public void TheGraphQlPathRejectsNullDependencies()
    {
        // Reached through the client, which is the only way these are built.
        Should.Throw<ArgumentNullException>(() => new TcgDexClient(null!, new TcgDexOptions()));
    }

    [Test]
    public void AnEmptyGraphQlBody_IsReportedRatherThanReturningNull()
    {
        // `Deserialize(...) ?? throw` — the null half, which a literal "null"
        // body produces.
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, "null");
        var client = new TcgDexClient(new HttpClient(handler), new TcgDexOptions());

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.SearchDetailedAsync(
                new CardFilter { Name = "x" }, cancellationToken: CancellationToken.None))
            .Result.Message.ShouldNotBeNullOrWhiteSpace();
    }

    // ----- cache eviction with nothing to evict -----

    [Test]
    public async Task EvictionOnAnEmptyCache_IsHarmless()
    {
        // The eviction scan guards against finding no candidate, which happens
        // if entries expire between the overflow check and the scan.
        var cache = new MemoryTcgDexResponseCache(maxEntries: 1);

        await cache.SetAsync("a", new CachedResponse
        {
            Body = [1],
            StoredAt = UnixEpoch,
        }, TimeSpan.FromHours(1));

        await cache.SetAsync("b", new CachedResponse
        {
            Body = [2],
            StoredAt = UnixEpoch,
        }, TimeSpan.FromHours(1));

        cache.Count.ShouldBe(1, "the bound should hold after eviction");
    }

    // ----- method-call argument evaluation -----

    [Test]
    public void AMethodArgumentThatEvaluatesToNull_IsRejected()
    {
        // `Evaluate(...)?.ToString()` — the null half, which would otherwise
        // produce a filter matching everything.
        string? nothing = null;

        Should.Throw<NotSupportedException>(
            () => new CardQuery().Where(c => c.Name.Contains(nothing!)));
    }
}

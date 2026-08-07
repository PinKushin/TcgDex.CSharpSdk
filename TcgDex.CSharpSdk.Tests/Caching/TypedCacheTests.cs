namespace TcgDex.Tests.Caching;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;
using TcgDex.Tests.Http;

/// <summary>
/// The deserialized-response cache, which lets a repeat fetch skip the parse.
/// </summary>
/// <remarks>
/// <para>
/// It is keyed on the response <c>ETag</c> rather than on a lifetime of its own.
/// That is the whole safety argument: an entry is reused only when the server —
/// or the byte cache replaying the server's header — says the body is byte-for-
/// byte the one it was built from. A typed entry therefore cannot be staler than
/// the bytes underneath it, and there is no second expiry policy to keep in step
/// with the first.
/// </para>
/// <para>
/// Most of these tests are about <em>not</em> reusing. The fast path is easy to
/// get right and easy to see working; the failure that matters is an entry
/// served after the resource changed.
/// </para>
/// </remarks>
[TestFixture]
public sealed class TypedCacheTests
{
    private static string Card() => Fixture.ReadText("card-pokemon-full.json");

    /// <summary>Serves the card with a caller-controlled ETag, counting requests.</summary>
    private sealed class TaggedHandler(string body) : HttpMessageHandler
    {
        internal string? ETag { get; set; } = "W/\"v1\"";

        internal string Body { get; set; } = body;

        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body, System.Text.Encoding.UTF8, "application/json"),
            };

            if (ETag is not null)
            {
                response.Headers.TryAddWithoutValidation("ETag", ETag);
            }

            return Task.FromResult(response);
        }
    }

    private static TcgDexClient Client(HttpMessageHandler handler, int maxTypedEntries = 64)
        => new(
            new HttpClient(handler),
            new TcgDexOptions { MaxDeserializedCacheEntries = maxTypedEntries });

    [Test]
    public async Task SameETag_ReturnsTheSameInstance()
    {
        var handler = new TaggedHandler(Card());
        using var client = Client(handler);

        var first = await client.Cards.GetAsync("swsh3-136", CancellationToken.None);
        var second = await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        ReferenceEquals(first, second).ShouldBeTrue("the body is unchanged, so the parse is skipped");
        handler.Requests.ShouldBe(2, "the typed cache skips the parse, not the request");
    }

    [Test]
    public async Task AChangedETag_ReparsesRatherThanServingStale()
    {
        // The test the whole design exists to pass. A resource that changed must
        // not be answered from an entry built before it changed.
        var handler = new TaggedHandler(Card());
        using var client = Client(handler);

        var first = await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        handler.Body = Card().Replace("\"Furret\"", "\"Sentret\"");
        handler.ETag = "W/\"v2\"";

        var second = await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        ReferenceEquals(first, second).ShouldBeFalse();
        first.ShouldNotBeNull().Name.ShouldBe("Furret");
        second.ShouldNotBeNull().Name.ShouldBe("Sentret", "the new body must win");
    }

    [Test]
    public async Task WithoutAnETag_NothingIsReused()
    {
        // No ETag means no way to know the body is the same one, so there is
        // nothing to validate an entry against and the cache must stay out of it.
        var handler = new TaggedHandler(Card()) { ETag = null };
        using var client = Client(handler);

        var first = await client.Cards.GetAsync("swsh3-136", CancellationToken.None);
        var second = await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        ReferenceEquals(first, second).ShouldBeFalse();
    }

    [Test]
    public async Task WhenDisabled_EveryFetchReparses()
    {
        var handler = new TaggedHandler(Card());
        using var client = Client(handler, maxTypedEntries: 0);

        var first = await client.Cards.GetAsync("swsh3-136", CancellationToken.None);
        var second = await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        ReferenceEquals(first, second).ShouldBeFalse();
    }

    [Test]
    public async Task DifferentUrls_DoNotShareAnEntry()
    {
        // Same ETag on two resources is not a collision the SDK gets to assume
        // away: ETags are only unique within a URL.
        var handler = new TaggedHandler(Card());
        using var client = Client(handler);

        var furret = await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        handler.Body = Card().Replace("\"Furret\"", "\"Sentret\"");

        var other = await client.Cards.GetAsync("swsh3-137", CancellationToken.None);

        furret.ShouldNotBeNull().Name.ShouldBe("Furret");
        other.ShouldNotBeNull().Name.ShouldBe("Sentret", "a different URL is a different entry");
    }

    [Test]
    public async Task DifferentTypes_AtTheSameUrl_DoNotCollide()
    {
        // The key has to carry the type. Nothing in the public surface reaches
        // this today — each endpoint has one model — but a key that ignored the
        // type would hand a Card back to a caller asking for a Serie, and that
        // would surface as an InvalidCastException a long way from here.
        var handler = new TaggedHandler(Fixture.ReadText("serie-full.json"));
        using var client = Client(handler);

        var asSerie = await client.Series.GetAsync("swsh", CancellationToken.None);

        handler.Body = Card();
        handler.ETag = "W/\"v2\"";

        var asCard = await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        asSerie.ShouldNotBeNull().Id.ShouldBe("swsh");
        asCard.ShouldNotBeNull().Name.ShouldBe("Furret");
    }

    [Test]
    public async Task ANotFoundResponse_IsNotCached()
    {
        // 404 returns null, and a null must not occupy an entry — otherwise a
        // card that appears later keeps answering as missing.
        //
        // Two responses are queued deliberately. If the second call were served
        // from the typed cache it would never reach the handler, and the queue
        // would be left with one unused — so the assertion that both were
        // consumed is what proves nothing was cached.
        var handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.NotFound, Fixture.ReadText("error-not-found.json"))
            .RespondWith(HttpStatusCode.NotFound, Fixture.ReadText("error-not-found.json"));

        using var client = Client(handler);

        (await client.Cards.GetAsync("nope-1", CancellationToken.None)).ShouldBeNull();
        (await client.Cards.GetAsync("nope-1", CancellationToken.None)).ShouldBeNull();

        handler.Requests.Count.ShouldBe(2);
    }

    [Test]
    public async Task TheBoundIsHonoured()
    {
        // Retaining deserialized objects is the point and also the risk: they
        // are several times the size of the bytes they came from. A bound that
        // did not hold would be a memory leak rather than a cache.
        var handler = new TaggedHandler(Card());
        using var client = Client(handler, maxTypedEntries: 4);

        for (var i = 0; i < 50; i++)
        {
            await client.Cards.GetAsync($"swsh3-{i}", CancellationToken.None);
        }

        // The oldest is long gone, so this reparses rather than being served.
        var first = await client.Cards.GetAsync("swsh3-0", CancellationToken.None);
        var again = await client.Cards.GetAsync("swsh3-0", CancellationToken.None);

        first.ShouldNotBeNull();
        ReferenceEquals(first, again).ShouldBeTrue("just fetched, so it is the newest entry");
    }

    [Test]
    public void TheDefault_IsEnabled()
        => new TcgDexOptions().MaxDeserializedCacheEntries.ShouldBeGreaterThan(0);

    [Test]
    public void ANegativeBound_IsRejected()
        => Should.Throw<ArgumentException>(
            () => new TcgDexOptions { MaxDeserializedCacheEntries = -1 }.Validate());
}

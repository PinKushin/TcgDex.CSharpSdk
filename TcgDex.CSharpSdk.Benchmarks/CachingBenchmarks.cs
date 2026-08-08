namespace TcgDex.Benchmarks;

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex.Caching;
using TcgDex.Models;
using TCGDex.Models;

/// <summary>
/// The three paths a request can take through the caching handler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read these as in-process overhead, not as the value of caching.</b> The
/// inner handler here answers instantly from memory, so every number below
/// excludes the thing caching actually saves: a network round trip. Against a
/// real endpoint that round trip is roughly 20–50 ms — four orders of magnitude
/// larger than anything measured here — so the ranking of these three paths in
/// production is decided almost entirely by how many of them touch the network,
/// not by the microseconds counted below.
/// </para>
/// <para>
/// What they are good for is exactly that exclusion. Isolating the SDK's own
/// cost shows what the caching layer adds when it hits, what revalidation costs
/// beyond a bare request, and whether any of that ever regresses. A cache hit
/// that stopped being cheap would be invisible in an end-to-end measurement,
/// where network noise swamps it.
/// </para>
/// <para>
/// The bytes are the other half of the story and are not measured here because
/// they are already known: a <c>304</c> carries an empty body, against 2.4 MB
/// for the unpaginated card list. Revalidation is not merely faster than a
/// fetch, it transfers nothing.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class CachingBenchmarks : IDisposable
{
    private const string Url = "https://api.tcgdex.net/v2/en/cards/swsh3-136";
    private const string ETag = "W/\"benchmark\"";

    private string _body = string.Empty;

    /// <summary>Serves the recorded card, and honours <c>If-None-Match</c>.</summary>
    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;

            if (request.Headers.IfNoneMatch.Count > 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };

            response.Headers.TryAddWithoutValidation("ETag", ETag);

            return Task.FromResult(response);
        }
    }

    private HttpClient _freshHit = null!;
    private HttpClient _revalidating = null!;
    private HttpClient _uncached = null!;

    private HttpClient _mineEndToEndHttp = null!;
    private HttpClient _theirsHttp = null!;
    private TcgDexClient _mineEndToEnd = null!;
    private TCGDex.TCGDexClient _theirs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _body = File.ReadAllText(Path.Combine("Fixtures", "card-pokemon-full.json"));

        // Long lifetime, primed once: every measured call is a fresh hit and
        // never reaches the handler.
        _freshHit = Build(TimeSpan.FromHours(1));
        Prime(_freshHit);

        // Zero lifetime, primed once: the entry is always stale, so every
        // measured call revalidates and gets a 304.
        _revalidating = Build(TimeSpan.Zero);
        Prime(_revalidating);

        // No caching handler at all — the cost of the request without the
        // layer, so the hit and the revalidation have something to be compared
        // against rather than only to each other.
        _uncached = new HttpClient(new StubHandler(_body));

        // ----- the same warm hit, end to end, on both SDKs -----
        //
        // The three rows above stop at the HTTP boundary and return byte
        // counts. These two go all the way to a Card, because that is where the
        // architectural difference shows and a byte count would hide it.
        _mineEndToEndHttp = new HttpClient(new TcgDexCachingHandler(
            new MemoryTcgDexResponseCache(),
            new TcgDexCacheOptions
            {
                DefaultTimeToLive = TimeSpan.FromHours(1),
                PricingTimeToLive = TimeSpan.FromHours(1),
                CatalogTimeToLive = TimeSpan.FromHours(1),
            })
        {
            InnerHandler = new StubHandler(_body),
        });

        _mineEndToEnd = new TcgDexClient(_mineEndToEndHttp, new TcgDexOptions());

        _theirsHttp = new HttpClient(new StubHandler(_body));
        _theirs = new TCGDex.TCGDexClient(TCGDex.SupportedLanguages.En, _theirsHttp)
        {
            Cache = new TCGDex.MemoryTCGDexCache(),
            CacheTTL = 3600,
        };

        // Both primed, so every measured call is a warm hit on both sides.
        _mineEndToEnd.Cards.GetAsync(CardId, CancellationToken.None).GetAwaiter().GetResult();
        _theirs.FetchCardAsync(CardId, cancellationToken: CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private const string CardId = "swsh3-136";

    private HttpClient Build(TimeSpan timeToLive)
    {
        TcgDexCacheOptions options = new()
        {
            DefaultTimeToLive = timeToLive,
            PricingTimeToLive = timeToLive,
            CatalogTimeToLive = timeToLive,
        };

        TcgDexCachingHandler handler = new(new MemoryTcgDexResponseCache(), options)
        {
            InnerHandler = new StubHandler(_body),
        };

        return new HttpClient(handler);
    }

    private static void Prime(HttpClient client)
        => client.GetAsync(Url).GetAwaiter().GetResult().Dispose();

    /// <summary>Disposes the clients built in setup — CA1001 requires it.</summary>
    public void Dispose()
    {
        _freshHit?.Dispose();
        _revalidating?.Dispose();
        _uncached?.Dispose();
        _mineEndToEnd?.Dispose();
        _mineEndToEndHttp?.Dispose();
        _theirsHttp?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Baseline = true)]
    public async Task<int> NoCachingLayer()
    {
        using HttpResponseMessage response = await _uncached.GetAsync(Url).ConfigureAwait(false);
        return (await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Length;
    }

    [Benchmark]
    public async Task<int> CacheHit()
    {
        using HttpResponseMessage response = await _freshHit.GetAsync(Url).ConfigureAwait(false);
        return (await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Length;
    }

    [Benchmark]
    public async Task<int> Revalidation()
    {
        using HttpResponseMessage response = await _revalidating.GetAsync(Url).ConfigureAwait(false);
        return (await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Length;
    }

    // ----- a warm cache hit all the way to a Card, on both SDKs -----

    /// <summary>A cache hit on this SDK, deserialized.</summary>
    /// <remarks>
    /// <b>This SDK's cache stores bytes, so a hit re-deserializes.</b> That is a
    /// consequence of where the cache sits: on the <c>HttpMessageHandler</c>
    /// pipeline, below the typed clients, which is what lets one implementation
    /// serve every endpoint and lets a stale entry be revalidated with an
    /// <c>ETag</c> for a <c>304</c> and zero bytes. The cost is that the parse
    /// is paid again on every hit.
    /// </remarks>
    [Benchmark]
    public async Task<string?> WarmHitToCard_Mine()
    {
        Card? card = await _mineEndToEnd.Cards.GetAsync(CardId, CancellationToken.None)
            .ConfigureAwait(false);

        return card?.Name;
    }

    /// <summary>The same on the other SDK's cache.</summary>
    /// <remarks>
    /// <para>
    /// <b>Neither SDK caches deserialized objects.</b> Theirs caches at the
    /// resource layer and stores a <c>(string, DateTime)</c> — the response body
    /// as a decoded string — so a hit re-parses and returns a different instance
    /// each time, verified by reference equality. Ours caches at the handler
    /// layer and stores bytes. Same reparse cost, and the difference between the
    /// two rows is deserialization speed rather than cache design.
    /// </para>
    /// <para>
    /// This benchmark was written expecting the opposite — an object cache
    /// winning by orders of magnitude — and the prediction was wrong. Which is
    /// why it is a benchmark: their "warm hit" landing on top of their cold
    /// fetch time is what exposed that their cache was saving the transport and
    /// nothing else.
    /// </para>
    /// <para>
    /// Two differences that are real and do not show up in the time column:
    /// their cache is on by default and has no bound, so a long-lived process
    /// keeps every response body it has ever fetched, as a UTF-16 string at
    /// roughly twice the bytes. And storing the body rather than the object is
    /// what makes <c>ETag</c> revalidation possible at all — which theirs does
    /// not do.
    /// </para>
    /// </remarks>
    [Benchmark]
    public async Task<string?> WarmHitToCard_Theirs()
    {
        CardModel? card = await _theirs.FetchCardAsync(CardId, cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);

        return card?.Name;
    }
}

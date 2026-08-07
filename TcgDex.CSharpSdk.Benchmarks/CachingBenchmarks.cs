namespace TcgDex.Benchmarks;

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex.Caching;

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

            var response = new HttpResponseMessage(HttpStatusCode.OK)
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
    }

    private HttpClient Build(TimeSpan timeToLive)
    {
        var options = new TcgDexCacheOptions
        {
            DefaultTimeToLive = timeToLive,
            PricingTimeToLive = timeToLive,
            CatalogTimeToLive = timeToLive,
        };

        var handler = new TcgDexCachingHandler(new MemoryTcgDexResponseCache(), options)
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
        GC.SuppressFinalize(this);
    }

    [Benchmark(Baseline = true)]
    public async Task<int> NoCachingLayer()
    {
        using var response = await _uncached.GetAsync(Url).ConfigureAwait(false);
        return (await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Length;
    }

    [Benchmark]
    public async Task<int> CacheHit()
    {
        using var response = await _freshHit.GetAsync(Url).ConfigureAwait(false);
        return (await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Length;
    }

    [Benchmark]
    public async Task<int> Revalidation()
    {
        using var response = await _revalidating.GetAsync(Url).ConfigureAwait(false);
        return (await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Length;
    }
}

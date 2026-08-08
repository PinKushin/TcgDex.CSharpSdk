namespace TcgDex.Benchmarks;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Engines;
using TcgDex;
using TcgDex.Models;
using TcgDex.Querying;

/// <summary>
/// The one claim in this repository that decides a design and had never been
/// timed: that fetching full card detail over GraphQL in a single request beats
/// REST's one call per card.
/// </summary>
/// <remarks>
/// <para>
/// <b>Run this by hand, once, and not in CI.</b> Every other benchmark here uses
/// a stub transport and can run anywhere as often as you like. This one cannot:
/// the entire claim is about <em>round trips</em>, so a recorded response would
/// measure the wrong thing. It therefore issues real requests to a free public
/// API that somebody else pays for.
/// </para>
/// <para>
/// The whole run costs roughly <b>124 requests</b>. That is a defensible one-off
/// and an indefensible weekly job, which is why there is no workflow for it:
/// </para>
/// <code>
/// dotnet run -c Release --project TcgDex.CSharpSdk.Benchmarks -- --filter "*RoundTrip*"
/// </code>
/// <para>
/// <b>What is measured is what the SDK actually offers.</b> The GraphQL section
/// of <c>api-info.md</c> illustrates the win with <c>set(id){cards{…}}</c>, and
/// that nested fetch is real in the API but <em>not exposed by this SDK</em> —
/// <see cref="CardFilter"/> has no set field. What the SDK ships is the flat
/// detailed search, so that is what is compared here. Benchmarking the nested
/// form would produce a number for a feature nobody can call.
/// </para>
/// <para>
/// <b>Rules.</b> Both sides use the same client and the same network. Caching is
/// off on both — including the deserialized-response cache, which would
/// otherwise let the second iteration skip the parse and quietly turn this into
/// a cache benchmark. Expect wide error bars: this measures TCGdex's servers and
/// your connection as much as the SDK, and the useful output is the ratio and
/// the request count rather than any absolute number.
/// </para>
/// </remarks>
// RunStrategy.Monitoring with invocationCount 1 is the setting for expensive
// I/O work: one call per iteration, no unrolling, no attempt to run millions of
// operations to shrink the error bars. Using the default job here would issue
// tens of thousands of requests.
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 3, invocationCount: 1)]
[MemoryDiagnoser]
public class RoundTripBenchmarks : IDisposable
{
    private TcgDexClient _client = null!;
    private HttpClient _http = null!;

    /// <summary>
    /// Names chosen for how many cards they match: enough to show the shape,
    /// few enough that the REST leg stays a courteous number of requests.
    /// "Pikachu" would be 121 requests per iteration and is deliberately absent.
    /// </summary>
    [Params("Furret", "Sentret")]
    public string Name { get; set; } = "Furret";

    [GlobalSetup]
    public void Setup()
    {
        _http = new HttpClient();

        // No caching layer, and no typed cache either: a warm parse on the
        // second iteration would flatter the side that happens to run second.
        _client = new TcgDexClient(_http, new TcgDexOptions { MaxDeserializedCacheEntries = 0 });
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Disposes the client and its transport. Idempotent.</summary>
    public void Dispose()
    {
        _client?.Dispose();
        _http?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// What a caller must do over REST to get full detail for every match: one
    /// list request, then one request per card. This is the N+1.
    /// </summary>
    [Benchmark(Baseline = true, Description = "REST: list + one request per card")]
    public async Task<int> Rest_ListThenEachCard()
    {
        IReadOnlyList<CardBrief> briefs = await _client.Cards.ListAsync(
            new CardQuery().Where(c => c.Name == Name),
            CancellationToken.None).ConfigureAwait(false);

        int hp = 0;

        foreach (CardBrief brief in briefs)
        {
            Card? card = await _client.Cards.GetAsync(brief.Id, CancellationToken.None).ConfigureAwait(false);
            hp += card?.Hp ?? 0;
        }

        return hp;
    }

    /// <summary>The same result in one request.</summary>
    [Benchmark(Description = "GraphQL: one request")]
    public async Task<int> GraphQl_SingleRequest()
    {
        IReadOnlyList<Card> cards = await _client.Cards.SearchDetailedAsync(
            new CardFilter { Name = Name },
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        int hp = 0;

        foreach (Card card in cards)
        {
            hp += card.Hp ?? 0;
        }

        return hp;
    }
}

namespace TcgDex.Benchmarks;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using TcgDex;
using TcgDex.Models;
using TcgDex.Querying;

/// <summary>
/// Whether the GraphQL-over-REST win <em>scales</em> with the number of cards,
/// which <see cref="RoundTripBenchmarks"/> established at one size but could not
/// extrapolate.
/// </summary>
/// <remarks>
/// <para>
/// The base benchmark measured Furret (13 cards) and Sentret (16) and found
/// ~7-8x. It also found something it could not explain: the REST leg barely
/// moved between 13 and 16 requests — 560 ms vs 575 ms, under 3% for 23% more
/// requests. If per-request latency dominated, that should have been nearer
/// 690 ms. Two sizes three points apart cannot resolve why, so the writeup said
/// plainly that the scaling was not established and must not be extrapolated to
/// a large set.
/// </para>
/// <para>
/// <b>This resolves it by manipulating exactly one variable.</b> A single name
/// with many printings (<c>Gyarados</c>) is used throughout, and both legs are
/// capped to the same <see cref="Cards"/> count — the REST list with
/// <see cref="CardQuery.Page(int,int)"/>, the GraphQL search with
/// <c>itemsPerPage</c>. So the only thing that changes across params is how many
/// cards are fetched: same name, same list response, same network, caching off.
/// If REST time grows roughly linearly with N, per-request latency dominates and
/// the earlier flat result was noise from too narrow a range. If it stays flat,
/// something else does, and that is the finding.
/// </para>
/// <para>
/// <b>REST issues N+1 requests, GraphQL issues 1</b>, at every N. So GraphQL is
/// expected to be a near-flat line and REST a rising one; the ratio at each N is
/// the scaling story.
/// </para>
/// <para>
/// <b>Cost, and why this is a hand-run one-off.</b> Like the base benchmark this
/// hits the real API — nothing else can measure round trips honestly. The REST
/// leg alone issues <c>sum(N+1)</c> requests per iteration, which for the params
/// below is ~144, times warmup + iterations. Budget a few hundred requests
/// against a free API somebody else pays for: defensible once, indefensible on a
/// schedule, so there is no workflow for it.
/// </para>
/// <para>
/// <b>Why Gyarados and not Pikachu.</b> The obvious pick — Pikachu, 120 printings
/// — cannot be used, and the reason is itself a finding. TCGdex's GraphQL schema
/// marks <c>AttacksListItem.name</c> non-nullable, but the API serves cards whose
/// attacks have no name (<c>2017sm-5</c>). So a GraphQL search that returns such a
/// card fails outright with <c>Cannot return null for non-nullable field</c> — the
/// REST leg reads it (since 0.2.0), the GraphQL leg cannot. Measured 2026-08-19,
/// this poisons Pikachu, Eevee, Charizard, Mewtwo and Snorlax. Gyarados (71
/// printings, all with named attacks) is the largest name that runs both legs
/// clean, which is why N tops out at 70 here rather than 120.
/// </para>
/// <code>
/// dotnet run -c Release --project TcgDex.CSharpSdk.Benchmarks -- --filter "*Scaling*"
/// </code>
/// <para>
/// <b>Read the params as requests, not truth.</b> If Gyarados has fewer than the
/// largest N, that param silently measures the max available instead — the real
/// count is the list length, and the writeup records it rather than the label,
/// exactly as the base benchmark does.
/// </para>
/// </remarks>
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 3, invocationCount: 1)]
[MemoryDiagnoser]
public class RoundTripScalingBenchmarks : IDisposable
{
    private TcgDexClient _client = null!;
    private HttpClient _http = null!;

    /// <summary>
    /// A name with enough printings that the largest <see cref="Cards"/> value is
    /// reachable, AND whose every card has named attacks so the GraphQL leg does
    /// not hit the non-nullable-name schema bug. Gyarados (71 printings) is the
    /// largest such name; the marquee Pokémon with more printings all fail the
    /// GraphQL query. See the remarks above.
    /// </summary>
    private const string Name = "Gyarados";

    /// <summary>
    /// How many cards each leg fetches — the one variable this benchmark moves.
    /// A wide spread (14x) so a linear trend is distinguishable from a flat one;
    /// four points to stay courteous to a free API, all within Gyarados's 71.
    /// </summary>
    [Params(5, 20, 45, 70)]
    public int Cards { get; set; } = 20;

    [GlobalSetup]
    public void Setup()
    {
        _http = new HttpClient();
        // Caching off on both sides, including the deserialized-response cache:
        // a warm parse on a later iteration would flatter whichever leg ran
        // second and quietly turn a round-trip benchmark into a cache benchmark.
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
    /// The N+1: one list request capped to <see cref="Cards"/> results, then one
    /// detail request per card returned.
    /// </summary>
    [Benchmark(Baseline = true, Description = "REST: list + one request per card")]
    public async Task<int> Rest_ListThenEachCard()
    {
        CardQuery query = new CardQuery()
            .Where(c => c.Name == Name)
            .Page(1, Cards);

        IReadOnlyList<CardBrief> briefs = await _client.Cards.ListAsync(
            query, CancellationToken.None).ConfigureAwait(false);

        int hp = 0;
        foreach (CardBrief brief in briefs)
        {
            Card? card = await _client.Cards.GetAsync(brief.Id, CancellationToken.None).ConfigureAwait(false);
            hp += card?.Hp ?? 0;
        }

        return hp;
    }

    /// <summary>The same <see cref="Cards"/> results, full detail, in one request.</summary>
    [Benchmark(Description = "GraphQL: one request")]
    public async Task<int> GraphQl_SingleRequest()
    {
        IReadOnlyList<Card> cards = await _client.Cards.SearchDetailedAsync(
            new CardFilter { Name = Name },
            page: 1,
            itemsPerPage: Cards,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        int hp = 0;
        foreach (Card card in cards)
        {
            hp += card.Hp ?? 0;
        }

        return hp;
    }
}

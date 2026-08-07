namespace TcgDex.Benchmarks;

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex.Querying;

/// <summary>
/// This SDK against the other public C# TCGdex SDK, on identical work.
/// </summary>
/// <remarks>
/// <para>
/// The other package is <c>TCGdex</c> by luizaraujodev, MIT licensed, and it is
/// referenced here by the benchmark project only — never by the SDK. Both
/// clients accept an injected <see cref="HttpClient"/>, which is what makes a
/// fair comparison possible at all: without it the only option is measuring over
/// the live API, which reports TCGdex's servers and the local connection rather
/// than either library.
/// </para>
/// <para>
/// <b>Rules this comparison holds itself to.</b> Both sides get the same stub
/// transport serving the same recorded payload. Caching is off on both — both
/// libraries have it, and pitting a warm cache against a cold fetch would
/// measure a configuration difference and call it speed. Losses are reported
/// alongside wins; the point is a number a reader can reproduce, not a
/// favourable one.
/// </para>
/// <para>
/// <b>That second rule was stated here for weeks and was not true.</b> The other
/// SDK caches <em>by default</em> — a freshly constructed client already has a
/// <c>MemoryTCGDexCache</c> and <c>CacheTTL = 3600</c> — and this benchmark asks
/// for the same card id on every iteration. So every measured call after the
/// first was a cache hit, and the comparison was charging this SDK for a
/// transport round trip the other side was skipping. Verified by counting
/// requests at the handler: three calls, one request. <c>CacheTTL = 0</c> is
/// what actually disables it; assigning <c>Cache = null</c> throws.
/// </para>
/// <para>
/// The distortion turned out to be small, because their cache stores the
/// response <em>string</em> rather than the deserialized model — a hit re-parses,
/// returning a different instance each time — so it was saving the stub
/// transport and not the deserialization that dominates both sides. Small is not
/// the point. A stated fairness rule that nobody checked is worth less than no
/// rule at all, because it reads as evidence.
/// </para>
/// <para>
/// What this does <b>not</b> measure: network time, which dominates real usage
/// and is identical for both; feature coverage, which is a different question
/// entirely; and correctness, which the test suite covers.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class ComparisonBenchmarks : IDisposable
{
    private const string CardId = "swsh3-136";

    private string _body = string.Empty;
    private HttpClient _mineHttp = null!;
    private HttpClient _mineNoPricingHttp = null!;
    private HttpClient _theirsHttp = null!;
    private TcgDexClient _mine = null!;
    private TcgDexClient _mineNoPricing = null!;
    private TCGDex.TCGDexClient _theirs = null!;

    /// <summary>Answers every request from memory with the recorded card.</summary>
    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    [GlobalSetup]
    public void Setup()
    {
        _body = File.ReadAllText(Path.Combine("Fixtures", "card-pokemon-full.json"));

        _mineHttp = new HttpClient(new StubHandler(_body));
        _theirsHttp = new HttpClient(new StubHandler(_body));

        _mine = new TcgDexClient(_mineHttp, new TcgDexOptions());

        _mineNoPricingHttp = new HttpClient(new StubHandler(_body));
        _mineNoPricing = new TcgDexClient(
            _mineNoPricingHttp,
            new TcgDexOptions { DeserializePricing = false });

        // CacheTTL = 0 is the only way to switch their caching off: it is on by
        // default, and assigning Cache = null throws ArgumentNullException.
        _theirs = new TCGDex.TCGDexClient(TCGDex.SupportedLanguages.En, _theirsHttp)
        {
            CacheTTL = 0,
        };
    }

    public void Dispose()
    {
        _mine?.Dispose();
        _mineHttp?.Dispose();
        _mineNoPricing?.Dispose();
        _mineNoPricingHttp?.Dispose();
        _theirsHttp?.Dispose();
        GC.SuppressFinalize(this);
    }

    // ----- fetching and deserializing one card -----

    [Benchmark(Baseline = true)]
    public async Task<string?> FetchCard_Mine()
    {
        var card = await _mine.Cards.GetAsync(CardId, CancellationToken.None).ConfigureAwait(false);
        return card?.Name;
    }

    [Benchmark]
    public async Task<string?> FetchCard_Theirs()
    {
        var card = await _theirs.FetchCardAsync(CardId, cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);

        return card?.Name;
    }

    /// <summary>This SDK with <c>DeserializePricing = false</c>.</summary>
    /// <remarks>
    /// <para>
    /// The nearest thing to like-for-like on this row, and the reason it is
    /// here. Their <c>CardModel</c> has no pricing property at all — nor any
    /// pricing type anywhere in their assembly — so the block arrives on the
    /// wire and is discarded. The baseline row above is therefore charging this
    /// SDK for work the other side never does.
    /// </para>
    /// <para>
    /// This does not make the comparison fair, it makes the gap legible.
    /// Parsing pricing is a feature, and a consumer who wants prices has to
    /// write that code themselves against the other SDK. But a reader comparing
    /// deserialization speed deserves to see both numbers.
    /// </para>
    /// </remarks>
    [Benchmark]
    public async Task<string?> FetchCard_MineWithoutPricing()
    {
        var card = await _mineNoPricing.Cards.GetAsync(CardId, CancellationToken.None)
            .ConfigureAwait(false);

        return card?.Name;
    }

}

/// <summary>
/// Building a filtered query string: expression tree versus field names.
/// </summary>
/// <remarks>
/// <b>Deliberately not equivalent work, and that is the measurement.</b> This
/// SDK translates a LINQ expression tree, which the compiler checks; the other
/// takes field names and values as strings, which it cannot. So this is the
/// price of the type-safe form, not a race to concatenate strings — a query is
/// built once per request against a network round trip of tens of milliseconds,
/// so either result is irrelevant to real throughput.
/// </remarks>
[MemoryDiagnoser]
public class QueryComparisonBenchmarks
{
    [Benchmark(Baseline = true)]
    public string BuildQuery_Mine()
        => new CardQuery()
            .Where(c => c.Name.Contains("Pikachu"))
            .Where(c => c.Hp > 100)
            .OrderByDescending(c => c.Name)
            .Page(2, 50)
            .ToQueryString();

    [Benchmark]
    public int BuildQuery_Theirs()
        => TCGDex.Query.Create()
            .Contains("name", "Pikachu")
            .GreaterThan("hp", 100)
            .Sort("name", TCGDex.SortOrder.Desc)
            .Paginate(2, 50)
            .Params
            .Count;
}
namespace TcgDex.Benchmarks;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using TcgDex.Models;
using TcgDex.Serialization;

/// <summary>
/// The unpaginated card list — the largest response the API serves.
/// </summary>
/// <remarks>
/// <para>
/// Every other measurement in this project runs against a single card of
/// <b>2,938 bytes</b>. <c>GET /v2/en/cards</c> returned <b>2,356,046 bytes</b>
/// when measured on 2026-08-07 — roughly <b>800×</b> larger, and an endpoint
/// applications hit on startup to build an index. Nothing established at 2.9 KB
/// necessarily survives the jump: fixed per-call costs disappear into the noise,
/// and costs that scale with payload size take over.
/// </para>
/// <para>
/// <b>The payload is synthesized rather than recorded.</b> A 2.3 MB fixture in
/// the repository would outweigh every other file in it combined, and it would
/// go stale as cards are printed. What is reproduced instead is the shape:
/// entries are emitted from the recorded <c>list-cards-brief.json</c> templates,
/// so field names, the <c>{setId}-{localId}</c> id form, the full asset URL and
/// the occasional entry with no <c>image</c> all appear as they do live, and the
/// total is sized to the byte count measured above.
/// </para>
/// <para>
/// The list endpoint declares an accurate <c>Content-Length</c> and does not
/// compress — verified against the live headers — which is why the two fetch
/// benchmarks below are worth separating. <c>BoundedContent</c> pre-sizes its
/// buffer from that header, and that hint measured as doing <i>nothing</i> at
/// 2.9 KB. This is the size at which it either earns its place or does not.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class LargePayloadBenchmarks : IDisposable
{
    /// <summary>
    /// The measured size of <c>GET /v2/en/cards</c> on 2026-08-07. Used as the
    /// synthesis target so the benchmark reflects a real response rather than a
    /// round number.
    /// </summary>
    private const int LiveListBytes = 2_356_046;

    private const string Url = "https://api.tcgdex.net/v2/en/cards";

    private string _listJson = string.Empty;
    private byte[] _listUtf8 = [];
    private JsonTypeInfo<IReadOnlyList<CardBrief>>? _listTypeInfo;
    private JsonSerializerOptions _reflectionOptions = new();

    private HttpClient _declaredLengthHttp = null!;
    private HttpClient _chunkedHttp = null!;
    private HttpClient _theirsHttp = null!;
    private TcgDexClient _declaredLength = null!;
    private TcgDexClient _chunked = null!;
    private TCGDex.TCGDexClient _theirs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _listJson = SynthesizeCardList(LiveListBytes);
        _listUtf8 = Encoding.UTF8.GetBytes(_listJson);

        _listTypeInfo = (JsonTypeInfo<IReadOnlyList<CardBrief>>)TcgDexJsonContext.Default.Options
            .GetTypeInfo(typeof(IReadOnlyList<CardBrief>));

        _reflectionOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };

        _declaredLengthHttp = new HttpClient(new StubHandler(_listUtf8, declareLength: true));
        _chunkedHttp = new HttpClient(new StubHandler(_listUtf8, declareLength: false));

        _declaredLength = new TcgDexClient(_declaredLengthHttp, new TcgDexOptions());
        _chunked = new TcgDexClient(_chunkedHttp, new TcgDexOptions());

        // The other SDK on the identical body. Its list model, CardResumeModel,
        // carries exactly the same four fields as CardBrief, so this is the
        // closest to like-for-like anywhere in these benchmarks — unlike the
        // single-card comparison, where their model has no pricing at all.
        // CacheTTL = 0 disables their caching, which is ON by default with a
        // one-hour TTL. Without this the row measures a warm cache hit against
        // this SDK's full fetch — every iteration asks for the same URL.
        _theirsHttp = new HttpClient(new StubHandler(_listUtf8, declareLength: true));
        _theirs = new TCGDex.TCGDexClient(TCGDex.SupportedLanguages.En, _theirsHttp)
        {
            CacheTTL = 0,
        };
    }

    /// <summary>Disposes the clients built in setup — CA1001 requires it.</summary>
    public void Dispose()
    {
        _declaredLength?.Dispose();
        _chunked?.Dispose();
        _declaredLengthHttp?.Dispose();
        _chunkedHttp?.Dispose();
        _theirsHttp?.Dispose();
        GC.SuppressFinalize(this);
    }

    // ----- deserialization alone -----

    /// <summary>The path the SDK ships: source-generated metadata over UTF-8 bytes.</summary>
    [Benchmark(Baseline = true)]
    public int DeserializeSourceGenerated()
        => JsonSerializer.Deserialize(_listUtf8, _listTypeInfo!)!.Count;

    /// <summary>
    /// The same list through reflection-based metadata.
    /// </summary>
    /// <remarks>
    /// Reflection beat source generation by roughly 5 µs on a single card, and
    /// that gap was accepted because reflection is not AOT-safe. Whether it is
    /// still 5 µs — a fixed cost — or 800× that at 800× the payload decides
    /// whether the tradeoff is small or expensive.
    /// </remarks>
    [Benchmark]
    public int DeserializeReflection()
        => JsonSerializer.Deserialize<IReadOnlyList<CardBrief>>(_listUtf8, _reflectionOptions)!.Count;

    // ----- the whole request path -----

    /// <summary>Fetch and deserialize with an accurate <c>Content-Length</c>, as the live API sends.</summary>
    [Benchmark]
    public async Task<int> FetchList_DeclaredLength()
        => (await _declaredLength.Cards.ListAsync(CancellationToken.None).ConfigureAwait(false)).Count;

    /// <summary>
    /// The same fetch from a handler that declares no length, so
    /// <c>BoundedContent</c> cannot pre-size and its buffer doubles as it fills.
    /// </summary>
    [Benchmark]
    public async Task<int> FetchList_ChunkedNoLength()
        => (await _chunked.Cards.ListAsync(CancellationToken.None).ConfigureAwait(false)).Count;

    /// <summary>The other SDK fetching and deserializing the same list.</summary>
    /// <remarks>
    /// Their per-property reflection — <c>ModelBase.Fill</c> resolves each field
    /// with <c>GetType().GetProperty(...)</c> — is a fixed cost per property per
    /// object, and this response has roughly 21,000 objects. On one card it was
    /// cheap enough not to matter. Whether it stays cheap at this scale is the
    /// question, and it is the opposite of the one the single-card comparison
    /// answers.
    /// </remarks>
    [Benchmark]
    public async Task<int> FetchList_Theirs()
        => (await _theirs.Cards.ListAsync(TCGDex.Query.Create(), CancellationToken.None)
            .ConfigureAwait(false)).Count;

    // ----- fixture synthesis -----

    /// <summary>
    /// Series, set and card name triples taken from the recorded brief-list
    /// fixture, cycled so id and name lengths vary as they do in the real list.
    /// </summary>
    private static readonly (string Serie, string Set, string Name)[] Templates =
    [
        ("swsh", "swsh3", "Furret"),
        ("sv", "sv09", "Pikachu"),
        ("base", "base1", "Charizard"),
        ("ecard", "ecard2", "Sneasel"),
        ("xy", "xy2", "Wobbuffet"),
        ("neo", "neo1", "Dark Alakazam"),
    ];

    /// <summary>
    /// Builds a brief-card-list body of approximately <paramref name="targetBytes"/>.
    /// </summary>
    /// <remarks>
    /// Every hundredth entry omits <c>image</c>, matching cards such as
    /// <c>exu-!</c> that have no artwork on record — the field is genuinely
    /// absent rather than null, so the deserializer takes the missing-property
    /// path on a realistic fraction of entries instead of never.
    /// </remarks>
    private static string SynthesizeCardList(int targetBytes)
    {
        StringBuilder builder = new(targetBytes + 1024);
        builder.Append('[');

        for (int index = 0; builder.Length < targetBytes; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            (string? serie, string? set, string? name) = Templates[index % Templates.Length];
            string localId = index.ToString(System.Globalization.CultureInfo.InvariantCulture);

            builder.Append("{\"id\":\"").Append(set).Append('-').Append(localId)
                   .Append("\",\"localId\":\"").Append(localId)
                   .Append("\",\"name\":\"").Append(name).Append('"');

            if (index % 100 != 0)
            {
                builder.Append(",\"image\":\"https://assets.tcgdex.net/en/")
                       .Append(serie).Append('/').Append(set).Append('/').Append(localId)
                       .Append('"');
            }

            builder.Append('}');
        }

        return builder.Append(']').ToString();
    }

    /// <summary>
    /// Serves the synthesized list, optionally without declaring its length.
    /// </summary>
    /// <remarks>
    /// <see cref="ByteArrayContent"/> always sets <c>Content-Length</c>, so the
    /// unknown-length case needs content that refuses to compute one — the same
    /// shape a chunked transfer produces.
    /// </remarks>
    private sealed class StubHandler(byte[] body, bool declareLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpContent content = declareLength
                ? new ByteArrayContent(body)
                : new UnknownLengthContent(body);

            content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>Content that never reports a length.</summary>
    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => stream.WriteAsync(body, 0, body.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

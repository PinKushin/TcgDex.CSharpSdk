namespace TcgDex.Fuzz;

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SharpFuzz;
using TcgDex;
using TcgDex.Querying;

/// <summary>
/// Drives every path in the SDK that consumes input it did not produce.
/// </summary>
/// <remarks>
/// <para>
/// <b>The property under test.</b> Whatever arrives, exactly one of two things
/// may happen: a value comes back, or <see cref="TcgDexApiException"/> is
/// thrown. Anything else — an index out of range, a null dereference, an
/// unbounded allocation, a hang — is a defect a consumer cannot defend against,
/// because it arrives from someone else's server rather than from their code.
/// </para>
/// <para>
/// <b>Why one process and not seven.</b> libFuzzer prefers a narrow target, and
/// seven executables would be the textbook answer. It would also mean seven
/// projects, seven corpora and a workflow that divides a fixed budget by seven.
/// Multiplexing on the first byte is the standard alternative: the fuzzer sees
/// the selector as just another input byte, and coverage feedback teaches it to
/// exercise every branch — the mode byte is cheap to mutate and each mode
/// reaches code the others cannot.
/// </para>
/// <para>
/// <b>What each mode is for.</b> They are not variations on one theme; each
/// reaches a parser the others do not:
/// </para>
/// <list type="bullet">
///   <item><b>Card</b> — the richest model, and the only path through both
///   hand-written converters.</item>
///   <item><b>Card list</b> — collection handling and the coalescing backing
///   fields that turn an absent array into an empty one rather than null.</item>
///   <item><b>Set</b> — a different model graph entirely: nested card counts,
///   abbreviations, boosters.</item>
///   <item><b>Enumeration</b> — bare JSON arrays of strings and integers, which
///   no other mode produces.</item>
///   <item><b>Problem details</b> — the error path, reached only on a non-2xx.
///   It is the code that runs when something has already gone wrong, which is
///   exactly when a second failure is worst.</item>
///   <item><b>GraphQL</b> — a separate transport with its own envelope, and a
///   response shape the REST models never see.</item>
///   <item><b>Query building</b> — the odd one out, and deliberately included.
///   The input is not a response but a <em>caller-supplied string</em> that ends
///   up in a URL. Escaping is the thing being tested.</item>
/// </list>
/// <para>
/// Running it is documented in <c>docs/measuring.md</c>.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>
    /// Generous but finite. The point is to catch a body that allocates without
    /// bound, and an unbounded ceiling would let the fuzzer exhaust memory and
    /// report that as the finding every time.
    /// </summary>
    private const long MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>How many modes the first byte selects between.</summary>
    private const int ModeCount = 7;

    private static void Main() => Fuzzer.LibFuzzer.Run(Consume);

    private static void Consume(ReadOnlySpan<byte> bytes)
    {
        // An empty input carries no mode and no payload. Returning rather than
        // picking a default keeps the corpus honest: a zero-length file should
        // not be credited with exercising anything.
        if (bytes.IsEmpty)
        {
            return;
        }

        var mode = bytes[0] % ModeCount;

        // Copied because the span does not outlive the callback and the request
        // is served asynchronously.
        var payload = bytes.Slice(1).ToArray();

        try
        {
            switch (mode)
            {
                case 0: Run(c => c.Cards.GetAsync("swsh3-136", CancellationToken.None), payload); break;
                case 1: Run(c => c.Cards.ListAsync(CancellationToken.None), payload); break;
                case 2: Run(c => c.Sets.GetAsync("swsh3", CancellationToken.None), payload); break;
                case 3: Run(c => c.Catalog.RaritiesAsync(CancellationToken.None), payload); break;
                case 4: ProblemDetails(payload); break;
                case 5: GraphQl(payload); break;
                default: QueryBuilding(payload); break;
            }
        }
        catch (TcgDexApiException)
        {
            // The documented outcome. Everything else is deliberately left to
            // escape: an uncaught exception is how a finding reaches libFuzzer,
            // so catching broadly here would make this a program that proves
            // nothing.
        }
    }

    /// <summary>Serves <paramref name="payload"/> as a 200 and runs one call.</summary>
    /// <typeparam name="T">Whatever the call returns; discarded.</typeparam>
    /// <param name="call">The SDK method to drive.</param>
    /// <param name="payload">The fuzzer's bytes, used as the response body.</param>
    private static void Run<T>(Func<TcgDexClient, Task<T>> call, byte[] payload)
    {
        using var http = new HttpClient(new FuzzHandler(payload, HttpStatusCode.OK));
        using var client = new TcgDexClient(http, Options());

        call(client).GetAwaiter().GetResult();
    }

    /// <summary>
    /// The failure path: a non-2xx whose body is the fuzzer's, which is what
    /// the problem-details parser reads.
    /// </summary>
    /// <param name="payload">The fuzzer's bytes.</param>
    /// <remarks>
    /// A <c>TcgDexApiException</c> is the *expected* result here rather than a
    /// tolerated one, so this mode only fails when something else escapes —
    /// which is the point, since the parser is running inside the error handler
    /// and a throw from there replaces a useful message with a useless one.
    /// </remarks>
    private static void ProblemDetails(byte[] payload)
    {
        using var http = new HttpClient(new FuzzHandler(payload, HttpStatusCode.BadRequest));
        using var client = new TcgDexClient(http, Options());

        client.Cards.GetAsync("swsh3-136", CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>The GraphQL transport, whose envelope the REST models never see.</summary>
    /// <param name="payload">The fuzzer's bytes, used as the GraphQL response.</param>
    private static void GraphQl(byte[] payload)
    {
        using var http = new HttpClient(new FuzzHandler(payload, HttpStatusCode.OK));
        using var client = new TcgDexClient(http, Options());

        client.Cards
            .SearchDetailedAsync(
                new CardFilter { Name = "Furret" },
                cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Caller-supplied text on its way into a URL.
    /// </summary>
    /// <param name="payload">The fuzzer's bytes, decoded as the filter value.</param>
    /// <remarks>
    /// <para>
    /// The only mode whose input is not a server response. A consumer filters on
    /// a name that came from their own user, so the escaping in
    /// <c>CardFilter</c> and <c>QueryFilter</c> is what stands between a search
    /// box and a malformed or injected request.
    /// </para>
    /// <para>
    /// The assertion is deliberately strong: whatever went in, the result must
    /// still parse as a URI query. A value that escaped its parameter would
    /// produce something <see cref="Uri"/> rejects, and that is a finding rather
    /// than a curiosity.
    /// </para>
    /// </remarks>
    private static void QueryBuilding(byte[] payload)
    {
        var value = System.Text.Encoding.UTF8.GetString(payload);
        var query = new CardQuery().Where(c => c.Name == value).ToQueryString();

        if (query.Length == 0)
        {
            return;
        }

        // Throws on a malformed query, which is exactly the failure being hunted.
        _ = new Uri("https://api.tcgdex.net/v2/en/cards?" + query, UriKind.Absolute);
    }

    private static TcgDexOptions Options() => new() { MaxResponseBytes = MaxResponseBytes };

    /// <summary>Serves the fuzzer's bytes with a caller-chosen status.</summary>
    /// <remarks>
    /// No <c>Content-Length</c>: that reaches the streaming size check in
    /// <c>BoundedContent</c> rather than the early rejection, which is the
    /// harder path of the two.
    /// </remarks>
    private sealed class FuzzHandler(byte[] body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new UnknownLengthContent(body),
            });
    }

    /// <summary>Content that refuses to report its length, as a chunked response does.</summary>
    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            System.IO.Stream stream,
            TransportContext? context)
            => stream.WriteAsync(body, 0, body.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

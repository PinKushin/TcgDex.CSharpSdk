namespace TcgDex.Fuzz;

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SharpFuzz;
using TcgDex;

/// <summary>
/// Drives the SDK's response-reading path with fuzzer-supplied bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The property under test.</b> A response body arrives from a server this
/// SDK does not control, over a <see cref="TcgDexOptions.BaseAddress"/> the
/// caller is explicitly allowed to repoint at a mirror. Whatever those bytes
/// are, exactly one of two things may happen: a value comes back, or
/// <see cref="TcgDexApiException"/> is thrown. Anything else — an index out of
/// range, a null dereference, an unbounded allocation, a hang — is a defect a
/// consumer cannot defend against, because it arrives from the network rather
/// than from their own code.
/// </para>
/// <para>
/// The whole path is exercised rather than just the deserializer:
/// <c>BoundedContent</c> enforces the size ceiling, the two hand-written
/// converters normalise polymorphic fields, and the transport translates
/// failures. The hand-written parts are where a fuzzer earns its time —
/// System.Text.Json is already fuzzed continuously by its own team, and this is
/// not an attempt to re-do that.
/// </para>
/// <para>
/// <b>Running it.</b> SharpFuzz instruments the assembly under test, then
/// libFuzzer drives this process:
/// </para>
/// <code>
/// dotnet tool install --global SharpFuzz.CommandLine
/// dotnet publish TcgDex.CSharpSdk.Fuzz -c Release -o fuzz-out
/// sharpfuzz fuzz-out/TcgDex.CSharpSdk.dll
/// libfuzzer-dotnet --target_path=fuzz-out/TcgDex.CSharpSdk.Fuzz -max_total_time=300 corpus
/// </code>
/// <para>
/// A crash is written to the working directory as the exact bytes that caused
/// it, which becomes a regression fixture rather than a bug report.
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

    private static void Main() => Fuzzer.LibFuzzer.Run(Consume);

    /// <summary>
    /// Feeds one fuzzer-generated body through the client.
    /// </summary>
    /// <param name="bytes">Whatever libFuzzer produced this iteration.</param>
    private static void Consume(ReadOnlySpan<byte> bytes)
    {
        // Copied because the span does not outlive the callback and the request
        // is served asynchronously.
        var body = bytes.ToArray();

        try
        {
            Fetch(body).GetAwaiter().GetResult();
        }
        catch (TcgDexApiException)
        {
            // The documented outcome for a body that cannot be read. Everything
            // else is deliberately left to escape: an uncaught exception is how
            // a finding is reported to libFuzzer, so catching broadly here would
            // turn this into a program that proves nothing.
        }
    }

    private static async Task Fetch(byte[] body)
    {
        using var client = new TcgDexClient(
            new HttpClient(new FuzzHandler(body)),
            new TcgDexOptions { MaxResponseBytes = MaxResponseBytes });

        await client.Cards.GetAsync("swsh3-136", CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Serves the fuzzer's bytes as a successful response.</summary>
    /// <remarks>
    /// No <c>Content-Length</c> and no <c>ETag</c>: that reaches the streaming
    /// size check rather than the early rejection, and keeps the deserialization
    /// cache out of the way so every iteration parses.
    /// </remarks>
    private sealed class FuzzHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
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

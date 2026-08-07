namespace TcgDex.Tests.Http;

using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;
using TcgDex.Querying;

/// <summary>
/// The ceiling on how much of a response the client will buffer.
/// </summary>
/// <remarks>
/// <para>
/// A response body is read into memory before it is deserialized, so without a
/// limit the peak memory of any request is whatever the server decides to send.
/// That matters here because <see cref="TcgDexOptions.BaseAddress"/> is
/// deliberately overridable — pointing the client at a mirror is a supported
/// scenario, and a mirror is not necessarily trustworthy.
/// </para>
/// <para>
/// Every test drives the transport through a caller-supplied
/// <see cref="HttpClient"/>, which is the case
/// <see cref="HttpClient.MaxResponseContentBufferSize"/> cannot cover: a caller
/// who brings their own client would otherwise get no protection at all. The
/// limit therefore lives in the transport.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ResponseLimitTests
{
    private static TcgDexClient CreateClient(RecordingHandler handler, long maxResponseBytes)
        => new(new HttpClient(handler), new TcgDexOptions { MaxResponseBytes = maxResponseBytes });

    /// <summary>A recorded card response — valid, and well under any limit here.</summary>
    private static string ValidCard() => Fixture.ReadText("card-pokemon-full.json");

    /// <summary>
    /// The same recorded card, padded with an ignored property until the body
    /// exceeds <paramref name="minimumBytes"/>.
    /// </summary>
    /// <remarks>
    /// Padding a real response rather than inventing one matters: a body of
    /// <c>{"id":"xxxx…"}</c> is large but also fails to deserialize, so a test
    /// using it would throw with or without a size limit and would prove
    /// nothing. This stays a card the SDK can parse, which leaves the size
    /// limit as the only thing that can reject it.
    /// </remarks>
    private static string OversizedCard(int minimumBytes)
    {
        var card = ValidCard().TrimEnd();
        var padding = new string('x', minimumBytes);

        // Unknown properties are ignored by the deserializer, so this changes
        // the size and nothing else. Remove rather than Substring: the
        // span-based Concat that CA1845 prefers does not exist on net472, which
        // this suite also targets.
        return card.Remove(card.Length - 1) + ",\"_padding\":\"" + padding + "\"}";
    }

    [Test]
    public void Rest_ResponseOverTheLimit_Throws()
    {
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, OversizedCard(64 * 1024));

        var exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler, 32768).Cards.GetAsync("swsh3-136", CancellationToken.None)).Result;

        exception.Message.ShouldContain("32768");
    }

    [Test]
    public void Rest_ResponseUnderTheLimit_IsReadNormally()
    {
        // The limit must not break the ordinary case, and a test that only
        // proves the throw would not notice an off-by-one that rejects
        // everything.
        var handler = new RecordingHandler().RespondWith(
            HttpStatusCode.OK,
            ValidCard());

        var card = CreateClient(handler, 1024 * 1024).Cards
            .GetAsync("swsh3-136", CancellationToken.None).Result;

        card.ShouldNotBeNull().Name.ShouldBe("Furret");
    }

    [Test]
    public void Rest_LimitOfZero_ReadsWithoutALimit()
    {
        // Zero is the documented escape hatch. Without a case for it the option
        // could be enforced as "reject everything" and every other test here
        // would still pass.
        var handler = new RecordingHandler().RespondWith(
            HttpStatusCode.OK,
            ValidCard());

        var card = CreateClient(handler, 0).Cards
            .GetAsync("swsh3-136", CancellationToken.None).Result;

        card.ShouldNotBeNull().Name.ShouldBe("Furret");
    }

    [Test]
    public void Rest_OversizedBodyWithoutContentLength_StillThrows()
    {
        // The Content-Length shortcut is the easy half. A chunked response
        // declares no length, so the only way to enforce the limit is to count
        // bytes while reading — and a hostile sender simply omits the header.
        var handler = new RecordingHandler().RespondWith(_ =>
        {
            var content = new StreamContent(
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes(OversizedCard(64 * 1024))));

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler, 32768).Cards.GetAsync("swsh3-136", CancellationToken.None)).Result;

        exception.Message.ShouldContain("32768");
    }

    [Test]
    public void Rest_LyingContentLength_IsNotTrusted()
    {
        // A declared length under the limit while the body runs over it. If the
        // implementation checks only the header it will happily buffer the
        // whole thing, which is exactly the attack the limit exists to stop.
        var handler = new RecordingHandler().RespondWith(_ =>
        {
            var content = new StringContent(OversizedCard(64 * 1024));
            content.Headers.ContentLength = 10;

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler, 32768).Cards.GetAsync("swsh3-136", CancellationToken.None)).Result;

        // Asserting the message, not merely that something threw. Without this
        // the test passes whether the limit exists or not, since a truncated
        // body fails to deserialize anyway — it would prove nothing.
        exception.Message.ShouldContain("32768");
    }

    [Test]
    public void Rest_OversizedErrorBody_StillReportsTheStatusCode()
    {
        // A server that answers 500 with a megabyte of HTML is common enough to
        // be an accident rather than an attack. The body is bounded like any
        // other, but the caller needs to hear "500", not "your limit was
        // exceeded" — the status is the actionable part, and losing it to a
        // size complaint would make the SDK harder to debug against a broken
        // server.
        var handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.InternalServerError, OversizedCard(64 * 1024));

        var exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler, 32768).Cards.GetAsync("swsh3-136", CancellationToken.None)).Result;

        exception.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        exception.Message.ShouldNotContain("32768");
    }

    // No test here for deeply nested JSON. It is the obvious next thing to
    // reach for, and it would have been decoration: a payload of `[[[[…]]]]`
    // fails to deserialize into a Card at any nesting depth, so the test passes
    // whether or not a depth limit exists and proves only what the malformed-
    // JSON tests already cover.
    //
    // The underlying worry does not apply either. Utf8JsonReader tracks depth
    // with a bit stack rather than by recursing, and these models are shallow,
    // so there is no stack to exhaust — System.Text.Json's 64-level MaxDepth is
    // a backstop, not the thing standing between this SDK and a crash.

    // ----- the boundary itself -----

    [Test]
    public void Rest_ResponseExactlyAtTheLimit_IsAccepted()
    {
        // The limit is a maximum, not a threshold to stay under. Mutation
        // testing flipped `>` to `>=` here and nothing noticed, which would
        // reject a response of exactly MaxResponseBytes — an off-by-one that
        // only ever fires on the one payload size nobody tests by accident.
        var card = ValidCard();
        var exactLength = System.Text.Encoding.UTF8.GetByteCount(card);

        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, card);

        var result = CreateClient(handler, exactLength).Cards
            .GetAsync("swsh3-136", CancellationToken.None).Result;

        result.ShouldNotBeNull().Name.ShouldBe("Furret");
    }

    [Test]
    public void Rest_ResponseOneByteOverTheLimit_IsRejected()
    {
        // The other side of the same boundary. Without this the `>` could
        // become `>=` in the opposite direction — accepting one byte too many —
        // and the test above would still pass.
        var card = ValidCard();
        var oneByteShort = System.Text.Encoding.UTF8.GetByteCount(card) - 1;

        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, card);

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler, oneByteShort).Cards
                .GetAsync("swsh3-136", CancellationToken.None)).Result.ShouldNotBeNull();
    }

    // ----- the two ways a body can be found too large -----

    [Test]
    public void Rest_AnHonestContentLength_IsRejectedBeforeReadingTheBody()
    {
        // A truthful Content-Length over the limit is refused up front, without
        // transferring the body. The message says "declared" precisely because
        // nothing was read — that word is the only evidence from outside that
        // the early exit happened rather than the streaming check.
        var handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, OversizedCard(64 * 1024));

        var exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler, 32768).Cards
                .GetAsync("swsh3-136", CancellationToken.None)).Result;

        exception.Message.ShouldContain("declared");
    }

    [Test]
    public void Rest_AnUndeclaredLength_IsRejectedWhileReading()
    {
        // No Content-Length, so the limit can only be enforced by counting
        // bytes as they arrive. The message says "exceeded" rather than
        // "declared", which is what distinguishes this path from the one above.
        var handler = new RecordingHandler().RespondWith(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnknownLengthContent(OversizedCard(64 * 1024)),
            });

        var exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler, 32768).Cards
                .GetAsync("swsh3-136", CancellationToken.None)).Result;

        exception.Message.ShouldContain("exceeded");
    }

    [Test]
    public void Rest_AnUndeclaredLengthOneByteOver_IsRejected()
    {
        // The streaming twin of Rest_ResponseOneByteOverTheLimit_IsRejected,
        // and it exists because mutation testing changed the running total in
        // `buffered.Length + read > maxBytes` to a subtraction and every test
        // still passed.
        //
        // Why the test above missed it. That one sends 68 KB against a 32 KB
        // limit, and with the subtraction the *final partial chunk* is small
        // enough that `length - read` clears the ceiling anyway — so it still
        // throws, just later and for the wrong reason. The mutant only survives
        // for a body modestly over the limit, where 40,000 bytes would sail
        // past a 32,768-byte ceiling. That is the interesting size: a
        // decompression bomb does not have to be enormous to be over budget,
        // and this is the check standing between the SDK and one.
        //
        // Sizing it one byte over rather than picking a number keeps the test
        // independent of the 16 KB chunk size — with the subtraction, the
        // running total can never reach a limit that close to the body length.
        var card = OversizedCard(40 * 1024);
        var oneByteShort = System.Text.Encoding.UTF8.GetByteCount(card) - 1;

        var handler = new RecordingHandler().RespondWith(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnknownLengthContent(card),
            });

        var exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler, oneByteShort).Cards
                .GetAsync("swsh3-136", CancellationToken.None)).Result;

        exception.Message.ShouldContain("exceeded", Case.Insensitive);
    }

    /// <summary>
    /// Content that refuses to report its length, as a chunked response does.
    /// </summary>
    /// <remarks>
    /// StreamContent over a MemoryStream is not good enough here: the stream is
    /// seekable, so a Content-Length is computed and the early rejection fires
    /// instead of the streaming one. Overriding TryComputeLength to return
    /// false is what actually models a body of unknown size.
    /// </remarks>
    private sealed class UnknownLengthContent(string body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            return stream.WriteAsync(bytes, 0, bytes.Length);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Test]
    public void Rest_TheLimitMessage_NamesTheOptionThatSetsIt()
    {
        // Someone hitting this needs to know which knob to turn. Without the
        // assertion the whole explanatory half of the message can be blanked
        // while the exception type and the number survive.
        var handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, OversizedCard(64 * 1024));

        var exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler, 32768).Cards
                .GetAsync("swsh3-136", CancellationToken.None)).Result;

        exception.Message.ShouldContain(nameof(TcgDexOptions.MaxResponseBytes));
        exception.Message.ShouldContain("Raise that limit");
    }

    [Test]
    public void GraphQl_ResponseOverTheLimit_Throws()
    {
        // The GraphQL transport reads bodies too, and an unbounded read there
        // would leave the limit half-applied.
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, OversizedCard(64 * 1024));

        var exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler, 32768).Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" },
                cancellationToken: CancellationToken.None)).Result;

        exception.Message.ShouldContain("32768");
    }
}

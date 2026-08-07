namespace TcgDex.Tests.Http;

using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
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

namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Threading;
using TcgDex;

/// <summary>
/// The parts of the transport that existing tests execute without verifying.
/// </summary>
/// <remarks>
/// <para>
/// Written to kill surviving mutants in <c>TcgDexTransport.cs</c>, which scored
/// 64% while reading as fully line-covered. Every test here corresponds to a
/// mutation the suite did not previously notice — mostly exception *messages*,
/// which the older tests assert the type of but not the content of.
/// </para>
/// <para>
/// A message is not cosmetic on this type. <see cref="TcgDexApiException"/> is
/// the single error contract for the whole SDK, so its text is the only thing
/// distinguishing "the network died", "the body was not JSON" and "that
/// resource is missing" for a caller looking at a log.
/// </para>
/// </remarks>
[TestFixture]
public sealed class TransportDetailTests
{
    private static TcgDexClient CreateClient(RecordingHandler handler)
        => new(new HttpClient(handler), new TcgDexOptions());

    private static TcgDexApiException Failing(RecordingHandler handler, string id = "swsh3-136")
        => Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.GetAsync(id, CancellationToken.None)).Result;

    // ----- exception messages -----

    [Test]
    public void NetworkFailure_NamesTheUriAndTheFailure()
    {
        // Kills a mutant that blanked this message entirely: the old test
        // asserted only that TcgDexApiException was thrown, which stays true
        // with an empty message and leaves a caller with nothing to act on.
        var handler = new RecordingHandler()
            .RespondWith(_ => throw new HttpRequestException("connection reset"));

        var exception = Failing(handler);

        exception.Message.ShouldContain("could not be completed");
        exception.Message.ShouldContain("https://api.tcgdex.net/v2/en/cards/swsh3-136");
    }

    [Test]
    public void MalformedBody_NamesTheUriAndTheExpectedType()
    {
        // The type name matters: this is the message someone sees when a proxy
        // returns an HTML error page, and knowing which model failed to parse
        // is what points them at the right request.
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, "<html>nope</html>");

        var exception = Failing(handler);

        exception.Message.ShouldContain("was not valid JSON");
        exception.Message.ShouldContain("Card");
        exception.Message.ShouldContain("https://api.tcgdex.net/v2/en/cards/swsh3-136");
    }

    [Test]
    public void MissingRequiredResource_NamesThePath()
    {
        // GetRequiredAsync is for endpoints that must always answer, so its
        // message has to identify which one did not.
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.NotFound, "{}");

        var exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Catalog.RaritiesAsync(CancellationToken.None)).Result;

        exception.Message.ShouldContain("rarities");
        exception.Message.ShouldContain("expected always to be available");
    }

    // ----- the failure description, and its two fallbacks -----

    [Test]
    public void FailureDescription_PrefersTheProblemDocument()
    {
        var handler = new RecordingHandler().RespondWith(
            HttpStatusCode.InternalServerError,
            """{"type":"https://tcgdex.dev/errors/server","title":"Everything is on fire","status":500}""");

        Failing(handler).Message.ShouldContain("Everything is on fire");
    }

    [Test]
    public void FailureDescription_FallsBackToTheReasonPhrase()
    {
        // No problem document, so the HTTP reason phrase is the only detail
        // available. Without this the `?? response.ReasonPhrase` branch is
        // never exercised and could be deleted unnoticed.
        var handler = new RecordingHandler().RespondWith(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                ReasonPhrase = "Backend Exploded",
                Content = new StringContent(string.Empty),
            });

        Failing(handler).Message.ShouldContain("Backend Exploded");
    }

    [Test]
    public void FailureDescription_FallsBackToAPlaceholderWhenNothingIsAvailable()
    {
        // Neither a problem document nor a reason phrase. The placeholder is
        // the last fallback, and a mutant that removed it left the message
        // silently empty.
        //
        // A non-standard status code is required to get here: assigning
        // ReasonPhrase = null on a known status does not stick, because .NET
        // substitutes the standard phrase. Not a contrived case either —
        // HTTP/2 removed reason phrases from the protocol entirely, so a real
        // HTTP/2 response reaches this branch for any status.
        var handler = new RecordingHandler().RespondWith(_ =>
            new HttpResponseMessage((HttpStatusCode)599)
            {
                ReasonPhrase = null,
                Content = new StringContent("   "),
            });

        Failing(handler).Message.ShouldContain("no detail supplied");
    }

    [Test]
    public void WhitespaceErrorBody_IsTreatedAsNoProblemDocument()
    {
        // Distinguishes "blank body" from "parseable body", which is the branch
        // a mutant collapsed by forcing IsNullOrWhiteSpace to true. A body of
        // real JSON must still be read.
        var withProblem = new RecordingHandler().RespondWith(
            HttpStatusCode.BadGateway,
            """{"type":"https://tcgdex.dev/errors/gateway","title":"Upstream refused","status":502}""");

        Failing(withProblem).Message.ShouldContain("Upstream refused");

        var blank = new RecordingHandler().RespondWith(_ =>
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                ReasonPhrase = "Bad Gateway",
                Content = new StringContent("   "),
            });

        Failing(blank).Message.ShouldContain("Bad Gateway");
    }
}

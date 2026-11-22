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
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(_ => throw new HttpRequestException("connection reset"));

        TcgDexApiException exception = Failing(handler);

        exception.Message.ShouldContain("could not be completed");
        exception.Message.ShouldContain("https://api.tcgdex.net/v2/en/cards/swsh3-136");
    }

    [Test]
    public void MalformedBody_NamesTheUriAndTheExpectedType()
    {
        // The type name matters: this is the message someone sees when a proxy
        // returns an HTML error page, and knowing which model failed to parse
        // is what points them at the right request.
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, "<html>nope</html>");

        TcgDexApiException exception = Failing(handler);

        exception.Message.ShouldContain("was not valid JSON");
        exception.Message.ShouldContain("Card");
        exception.Message.ShouldContain("https://api.tcgdex.net/v2/en/cards/swsh3-136");
    }

    private static string ListFailure(string body)
        => Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(new RecordingHandler().RespondWith(HttpStatusCode.OK, body))
                .Cards.ListAsync(CancellationToken.None)).Result.Message;

    [Test]
    public void ListEntryMissingARequiredField_NamesTheEntryAndTheField()
    {
        // DECISIONS #37 row 11: one card without a name still fails the whole
        // list, by decision, so the message is what makes it actionable. It has
        // to say which entry ($[1], the second) and which field, or the caller
        // has a 20,000-card response and no idea what to report upstream.
        string message = ListFailure(
            """[{"id":"a-1","localId":"1","name":"Furret"},{"id":"a-2","localId":"2"}]""");

        // System.Text.Json words this differently by version (8.x: "missing
        // required properties, including the following: name"; 10.x: "missing
        // required properties including: 'name'"), so only the parts both say.
        message.ShouldContain("missing required properties");
        message.ShouldContain("name");
        message.ShouldEndWith(" The first entry that fails is $[1] (id 'a-2').");
    }

    [Test]
    public void ListEntryWithoutAnId_IsNamedByIndexAlone()
    {
        string message = ListFailure("""[{"localId":"1","name":"Furret"}]""");

        message.ShouldEndWith(" The first entry that fails is $[0].");
    }

    [TestCase("<html>nope</html>")]
    [TestCase("""{"not":"a list"}""")]
    public void ListBodyThatIsNotAnArray_NamesNoEntry(string body)
        => ListFailure(body).ShouldNotContain("first entry");

    [Test]
    public void MissingRequiredResource_NamesThePath()
    {
        // GetRequiredAsync is for endpoints that must always answer, so its
        // message has to identify which one did not.
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.NotFound, "{}");

        TcgDexApiException exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Catalog.GetRaritiesAsync(CancellationToken.None)).Result;

        exception.Message.ShouldContain("rarities");
        exception.Message.ShouldContain("expected always to be available");
    }

    // ----- the failure description, and its two fallbacks -----

    [Test]
    public void FailureDescription_PrefersTheProblemDocument()
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(
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
        RecordingHandler handler = new RecordingHandler().RespondWith(_ =>
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
        RecordingHandler handler = new RecordingHandler().RespondWith(_ =>
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
        RecordingHandler withProblem = new RecordingHandler().RespondWith(
            HttpStatusCode.BadGateway,
            """{"type":"https://tcgdex.dev/errors/gateway","title":"Upstream refused","status":502}""");

        Failing(withProblem).Message.ShouldContain("Upstream refused");

        RecordingHandler blank = new RecordingHandler().RespondWith(_ =>
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                ReasonPhrase = "Bad Gateway",
                Content = new StringContent("   "),
            });

        Failing(blank).Message.ShouldContain("Bad Gateway");
    }

    [Test]
    public void ANullPath_IsRejectedByName()
    {
        // No public method passes null, so this is the internal contract only.
        // Without the guard, Uri reports its own parameter name, or the request
        // goes out against the bare language root.
        RecordingHandler handler = new();
        TcgDexTransport transport = new(new HttpClient(handler), new TcgDexOptions());

        ArgumentNullException error = Should.ThrowAsync<ArgumentNullException>(
            () => transport.GetAsync<TcgDex.Models.Card>(null!, CancellationToken.None)).Result;

        error.ParamName.ShouldBe("relativePath");
        handler.Requests.ShouldBeEmpty();
    }
}

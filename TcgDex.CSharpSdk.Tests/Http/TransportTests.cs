namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;
using TcgDex.Models;

/// <summary>
/// Behaviour of the HTTP layer: URL construction, the error contract, and
/// cancellation.
/// </summary>
[TestFixture]
public sealed class TransportTests
{
    private static TcgDexTransport CreateTransport(RecordingHandler handler, string language = "en")
    {
        var options = new TcgDexOptions { Language = language };
        return new TcgDexTransport(new HttpClient(handler), options);
    }

    // ----- URL construction -----

    [Test]
    public async Task GetAsync_BuildsUrlFromBaseAddressAndLanguage()
    {
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        await CreateTransport(handler).GetAsync<Card>("cards/swsh3-136", CancellationToken.None);

        handler.SingleRequestUri.ShouldBe("https://api.tcgdex.net/v2/en/cards/swsh3-136");
    }

    [Test]
    public async Task GetAsync_UsesConfiguredLanguageSegment()
    {
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        await CreateTransport(handler, "fr").GetAsync<Card>("cards/swsh3-136", CancellationToken.None);

        handler.SingleRequestUri.ShouldBe("https://api.tcgdex.net/v2/fr/cards/swsh3-136");
    }

    [Test]
    public async Task GetAsync_IssuesGetRequest()
    {
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        await CreateTransport(handler).GetAsync<Card>("cards/swsh3-136", CancellationToken.None);

        handler.SingleRequest.Method.ShouldBe(HttpMethod.Get);
    }

    [Test]
    public async Task GetAsync_PreservesQueryStringVerbatim()
    {
        // Filters are top-level query parameters. Anything that rewrites or
        // wraps them — in a `?q=` parameter, say — breaks the API contract.
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, "[]");

        await CreateTransport(handler)
            .GetAsync<IReadOnlyList<CardBrief>>("cards?name=eq:Furret&hp=gt:100", CancellationToken.None);

        handler.SingleRequestUri.ShouldBe("https://api.tcgdex.net/v2/en/cards?name=eq:Furret&hp=gt:100");
        handler.SingleRequestUri.ShouldNotContain("?q=");
    }

    [Test]
    public async Task GetAsync_RespectsCustomBaseAddress()
    {
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        var options = new TcgDexOptions
        {
            BaseAddress = new Uri("https://mirror.example.test/v2/"),
            Language = "en",
        };
        var transport = new TcgDexTransport(new HttpClient(handler), options);

        await transport.GetAsync<Card>("cards/swsh3-136", CancellationToken.None);

        handler.SingleRequestUri.ShouldBe("https://mirror.example.test/v2/en/cards/swsh3-136");
    }

    // ----- deserialization -----

    [Test]
    public async Task GetAsync_DeserializesThroughTheSdkContext()
    {
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        var card = await CreateTransport(handler).GetAsync<Card>("cards/swsh3-136", CancellationToken.None);

        card.ShouldNotBeNull();
        card.Name.ShouldBe("Furret");
    }

    // ----- the error contract -----

    [Test]
    public async Task GetAsync_WhenResourceNotFound_ReturnsNull()
    {
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.NotFound, "error-not-found.json");

        var card = await CreateTransport(handler).GetAsync<Card>("cards/nope", CancellationToken.None);

        card.ShouldBeNull("a missing resource is an expected outcome, not an error");
    }

    [Test]
    public void GetAsync_WhenLanguageInvalid_ThrowsRatherThanReturningNull()
    {
        // A bad language is also a 404, but it is a caller mistake rather than a
        // missing card — returning null would silently hide a typo'd language.
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.NotFound, "error-bad-language.json");

        var transport = CreateTransport(handler, "en");

        var exception = Should.ThrowAsync<TcgDexApiException>(
            async () => await transport.GetAsync<Card>("cards/swsh3-136", CancellationToken.None)).Result;

        exception.IsLanguageError.ShouldBeTrue();
        exception.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        exception.Message.ShouldContain("zz");
    }

    [Test]
    public void GetAsync_WhenServerError_Throws()
    {
        var handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.InternalServerError, "{}");

        var transport = CreateTransport(handler);

        var exception = Should.ThrowAsync<TcgDexApiException>(
            async () => await transport.GetAsync<Card>("cards/swsh3-136", CancellationToken.None)).Result;

        exception.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Test]
    public void GetAsync_WhenBodyIsNotJson_ThrowsApiExceptionNotJsonException()
    {
        // A proxy or outage can return HTML. Callers should not have to catch
        // JsonException separately from the SDK's own exception type.
        var handler = new RecordingHandler()
            .RespondWith(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>gateway error</html>"),
            });

        var transport = CreateTransport(handler);

        Should.ThrowAsync<TcgDexApiException>(
            async () => await transport.GetAsync<Card>("cards/swsh3-136", CancellationToken.None))
            .Result.ShouldNotBeNull();
    }

    // ----- cancellation -----

    [Test]
    public void GetAsync_WhenCancelled_ThrowsOperationCanceled()
    {
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.ThrowAsync<OperationCanceledException>(
            async () => await CreateTransport(handler).GetAsync<Card>("cards/swsh3-136", cts.Token));
    }

    // ----- options validation -----

    [Test]
    public void Options_WithUnsupportedLanguage_AreRejected()
    {
        var options = new TcgDexOptions { Language = "zz" };

        var exception = Should.Throw<ArgumentException>(options.Validate);

        // The message should name the valid set rather than just saying "invalid".
        exception.Message.ShouldContain("zz");
        exception.Message.ShouldContain("en");
    }

    [Test]
    public void Options_WithSupportedLanguage_AreAccepted()
    {
        foreach (var language in TcgDexLanguages.All)
        {
            Should.NotThrow(new TcgDexOptions { Language = language }.Validate);
        }
    }

    [Test]
    public void Options_DefaultToEnglishAgainstTheOfficialHost()
    {
        var options = new TcgDexOptions();

        options.Language.ShouldBe("en");
        options.BaseAddress.ShouldBe(new Uri("https://api.tcgdex.net/v2/"));
        Should.NotThrow(options.Validate);
    }

    [Test]
    public void Languages_CoverEveryCodeTheApiAccepts()
    {
        // Verified against the API's own error payload, which enumerates them.
        TcgDexLanguages.All.ShouldBe(
            [
                "en", "fr", "es", "es-mx", "it", "pt", "pt-br", "pt-pt", "de",
                "nl", "pl", "ru", "ja", "ko", "zh-tw", "id", "th", "zh-cn",
            ],
            ignoreOrder: true);
    }
}

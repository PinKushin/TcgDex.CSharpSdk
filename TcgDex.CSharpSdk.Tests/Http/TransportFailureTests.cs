namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;
using TcgDex.Querying;

/// <summary>
/// What happens when things go wrong.
/// </summary>
/// <remarks>
/// These are the paths a caller reaches when the network drops, the service
/// fails, or a proxy returns something unexpected — and they are the paths
/// least likely to be exercised by ordinary use, so they get explicit tests.
/// </remarks>
[TestFixture]
public sealed class TransportFailureTests
{
    private static TcgDexClient CreateClient(RecordingHandler handler)
        => new(new HttpClient(handler), new TcgDexOptions());

    private static RecordingHandler Throwing(Exception exception)
        => new RecordingHandler().RespondWith(_ => throw exception);

    // ----- REST transport -----

    [Test]
    public void Rest_WhenNetworkFails_ThrowsApiExceptionNotHttpRequestException()
    {
        // Callers should catch one exception type, not the transport's.
        var handler = Throwing(new HttpRequestException("no such host"));

        var exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.GetAsync("swsh3-136", CancellationToken.None)).Result;

        exception.InnerException.ShouldBeOfType<HttpRequestException>();
    }

    [Test]
    public void Rest_WhenRequestTimesOut_ReportsATimeout()
    {
        // A TaskCanceledException with no cancellation requested is a client-side
        // timeout, which is a fault rather than the caller's own cancellation.
        var handler = Throwing(new TaskCanceledException("timed out"));

        var exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.GetAsync("swsh3-136", CancellationToken.None)).Result;

        exception.Message.ShouldContain("timed out");
    }

    [TestCase(HttpStatusCode.InternalServerError)]
    [TestCase(HttpStatusCode.BadGateway)]
    [TestCase(HttpStatusCode.ServiceUnavailable)]
    [TestCase(HttpStatusCode.TooManyRequests)]
    public void Rest_WhenServerFails_ThrowsWithTheStatus(HttpStatusCode status)
    {
        var handler = new RecordingHandler().RespondWith(status, "{}");

        var exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.GetAsync("swsh3-136", CancellationToken.None)).Result;

        exception.StatusCode.ShouldBe(status);
    }

    [Test]
    public void Rest_WhenErrorBodyIsUnparseable_StillThrowsWithTheStatus()
    {
        // An unreadable error body must not mask the underlying failure.
        var handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.ServiceUnavailable, "<html>down for maintenance</html>");

        var exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.GetAsync("swsh3-136", CancellationToken.None)).Result;

        exception.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        exception.Problem.ShouldBeNull();
    }

    [Test]
    public void Rest_WhenErrorBodyIsEmpty_StillThrows()
    {
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.InternalServerError, "");

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.GetAsync("swsh3-136", CancellationToken.None))
            .Result.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Test]
    public void Rest_WhenRequiredResourceIsMissing_Throws()
    {
        // Catalog endpoints must always answer. A 404 there is a fault, unlike a
        // missing card.
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.NotFound, "error-not-found.json");

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Catalog.RaritiesAsync(CancellationToken.None))
            .Result.ShouldNotBeNull();
    }

    // ----- GraphQL transport -----

    [Test]
    public void GraphQl_WhenNetworkFails_ThrowsApiException()
    {
        var handler = Throwing(new HttpRequestException("connection reset"));

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None))
            .Result.InnerException.ShouldBeOfType<HttpRequestException>();
    }

    [Test]
    public void GraphQl_WhenRequestTimesOut_ReportsATimeout()
    {
        var handler = Throwing(new TaskCanceledException("timed out"));

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None))
            .Result.Message.ShouldContain("timed out");
    }

    [Test]
    public void GraphQl_WhenServerFails_ThrowsWithTheStatus()
    {
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.BadGateway, "{}");

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None))
            .Result.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    [Test]
    public void GraphQl_WhenBodyIsNotJson_ThrowsApiException()
    {
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, "<html>proxy error</html>");

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None))
            .Result.InnerException.ShouldBeOfType<JsonException>();
    }

    [Test]
    public async Task GraphQl_WhenDataIsAbsent_ReturnsEmpty()
    {
        // A response with neither data nor errors is degenerate but must not
        // produce a null collection.
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, "{}");

        var cards = await CreateClient(handler).Cards.SearchDetailedAsync(
            new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None);

        cards.ShouldNotBeNull();
        cards.ShouldBeEmpty();
    }

    [Test]
    public async Task GraphQl_WhenCardsIsNull_ReturnsEmpty()
    {
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, """{"data":{"cards":null}}""");

        var cards = await CreateClient(handler).Cards.SearchDetailedAsync(
            new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None);

        cards.ShouldBeEmpty();
    }

    // ----- cancellation -----

    [Test]
    public void Rest_WhenCallerCancels_ThrowsOperationCanceledNotApiException()
    {
        // The caller's own cancellation is theirs to observe, and must not be
        // rewritten into an API failure.
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.ThrowAsync<OperationCanceledException>(async () =>
            await CreateClient(handler).Cards.GetAsync("swsh3-136", cts.Token));
    }

    [Test]
    public void GraphQl_WhenCallerCancels_ThrowsOperationCanceled()
    {
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, """{"data":{"cards":[]}}""");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.ThrowAsync<OperationCanceledException>(async () =>
            await CreateClient(handler).Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" }, cancellationToken: cts.Token));
    }

    // ----- argument guards -----

    [Test]
    public void Client_WithNullHttpClient_Throws()
        => Should.Throw<ArgumentNullException>(() => new TcgDexClient(null!, new TcgDexOptions()));

    [Test]
    public void Cards_ListAsync_WithNullQuery_Throws()
    {
        var handler = new RecordingHandler();

        Should.ThrowAsync<ArgumentNullException>(async () =>
            await CreateClient(handler).Cards.ListAsync(null!, CancellationToken.None));
    }

    [Test]
    public void Cards_SearchDetailedAsync_WithNullFilter_Throws()
    {
        var handler = new RecordingHandler();

        Should.ThrowAsync<ArgumentNullException>(async () =>
            await CreateClient(handler).Cards.SearchDetailedAsync(null!, cancellationToken: CancellationToken.None));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void Cards_GetAsync_WithBlankId_Throws(string? id)
    {
        var handler = new RecordingHandler();

        Should.ThrowAsync<ArgumentException>(async () =>
            await CreateClient(handler).Cards.GetAsync(id!, CancellationToken.None));
    }
}

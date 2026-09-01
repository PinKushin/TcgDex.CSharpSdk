namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;
using TcgDex.Models;
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
        RecordingHandler handler = Throwing(new HttpRequestException("no such host"));

        TcgDexApiException exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.GetAsync("swsh3-136", CancellationToken.None)).Result;

        exception.InnerException.ShouldBeOfType<HttpRequestException>();
    }

    [Test]
    public void Rest_WhenRequestTimesOut_ReportsATimeout()
    {
        // A TaskCanceledException with no cancellation requested is a client-side
        // timeout, which is a fault rather than the caller's own cancellation.
        RecordingHandler handler = Throwing(new TaskCanceledException("timed out"));

        TcgDexApiException exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.GetAsync("swsh3-136", CancellationToken.None)).Result;

        exception.Message.ShouldContain("timed out");
    }

    [TestCase(HttpStatusCode.InternalServerError)]
    [TestCase(HttpStatusCode.BadGateway)]
    [TestCase(HttpStatusCode.ServiceUnavailable)]
    // Cast rather than the named member: HttpStatusCode.TooManyRequests does
    // not exist in .NET Framework's enum, and these tests also run on net472 to
    // exercise the netstandard2.0 assembly. The wire value is what matters.
    [TestCase((HttpStatusCode)429)]
    public void Rest_WhenServerFails_ThrowsWithTheStatus(HttpStatusCode status)
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(status, "{}");

        TcgDexApiException exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.GetAsync("swsh3-136", CancellationToken.None)).Result;

        exception.StatusCode.ShouldBe(status);
    }

    [Test]
    public void Rest_WhenErrorBodyIsUnparseable_StillThrowsWithTheStatus()
    {
        // An unreadable error body must not mask the underlying failure.
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.ServiceUnavailable, "<html>down for maintenance</html>");

        TcgDexApiException exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.GetAsync("swsh3-136", CancellationToken.None)).Result;

        exception.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        exception.Problem.ShouldBeNull();
    }

    [Test]
    public void Rest_WhenErrorBodyIsEmpty_StillThrows()
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.InternalServerError, "");

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.GetAsync("swsh3-136", CancellationToken.None))
            .Result.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Test]
    public void Rest_WhenRequiredResourceIsMissing_Throws()
    {
        // Catalog endpoints must always answer. A 404 there is a fault, unlike a
        // missing card.
        RecordingHandler handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.NotFound, "error-not-found.json");

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Catalog.RaritiesAsync(CancellationToken.None))
            .Result.ShouldNotBeNull();
    }

    // ----- GraphQL transport -----

    [Test]
    public void GraphQl_WhenNetworkFails_ThrowsApiException()
    {
        RecordingHandler handler = Throwing(new HttpRequestException("connection reset"));

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None))
            .Result.InnerException.ShouldBeOfType<HttpRequestException>();
    }

    [Test]
    public void GraphQl_WhenRequestTimesOut_ReportsATimeout()
    {
        RecordingHandler handler = Throwing(new TaskCanceledException("timed out"));

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None))
            .Result.Message.ShouldContain("timed out");
    }

    [Test]
    public void GraphQl_WhenServerFails_ThrowsWithTheStatus()
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.BadGateway, "{}");

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None))
            .Result.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    [Test]
    public void GraphQl_WhenBodyIsNotJson_ThrowsApiException()
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, "<html>proxy error</html>");

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
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, "{}");

        IReadOnlyList<Card> cards = await CreateClient(handler).Cards.SearchDetailedAsync(
            new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None);

        cards.ShouldNotBeNull();
        cards.ShouldBeEmpty();
    }

    [Test]
    public async Task GraphQl_WhenCardsIsNull_ReturnsEmpty()
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, """{"data":{"cards":null}}""");

        IReadOnlyList<Card> cards = await CreateClient(handler).Cards.SearchDetailedAsync(
            new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None);

        cards.ShouldBeEmpty();
    }

    // ----- cancellation -----

    [Test]
    public async Task Rest_WhenCallerCancels_ThrowsOperationCanceledNotApiException()
    {
        // The caller's own cancellation is theirs to observe, and must not be
        // rewritten into an API failure.
        RecordingHandler handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await CreateClient(handler).Cards.GetAsync("swsh3-136", cts.Token));
    }

    [Test]
    public async Task GraphQl_WhenCallerCancels_ThrowsOperationCanceled()
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, """{"data":{"cards":[]}}""");

        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await CreateClient(handler).Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" }, cancellationToken: cts.Token));
    }

    // ----- argument guards -----

    [Test]
    public void Client_WithNullHttpClient_Throws()
        => Should.Throw<ArgumentNullException>(() => new TcgDexClient(null!, new TcgDexOptions()));

    [Test]
    public async Task Cards_ListAsync_WithNullQuery_Throws()
    {
        RecordingHandler handler = new();

        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await CreateClient(handler).Cards.ListAsync(null!, CancellationToken.None));
    }

    [Test]
    public async Task Cards_SearchDetailedAsync_WithNullFilter_Throws()
    {
        RecordingHandler handler = new();

        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await CreateClient(handler).Cards.SearchDetailedAsync(null!, cancellationToken: CancellationToken.None));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public async Task Cards_GetAsync_WithBlankId_Throws(string? id)
    {
        RecordingHandler handler = new();

        await Should.ThrowAsync<ArgumentException>(async () =>
            await CreateClient(handler).Cards.GetAsync(id!, CancellationToken.None));
    }
}

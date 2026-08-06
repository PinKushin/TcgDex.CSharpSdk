namespace TcgDex.Tests.Diagnostics;

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TcgDex;
using TcgDex.Diagnostics;
using TcgDex.Querying;
using TcgDex.Tests.Http;

/// <summary>
/// What the SDK logs, at what level, and what it costs when nobody is listening.
/// </summary>
[TestFixture]
public sealed class LoggingTests
{
    private static (TcgDexClient Client, RecordingLogger Log) Build(
        RecordingHandler handler,
        LogLevel minimum = LogLevel.Trace)
    {
        var log = new RecordingLogger(minimum);
        var client = new TcgDexClient(new HttpClient(handler), new TcgDexOptions(), log.Factory);

        return (client, log);
    }

    [Test]
    public async Task ASuccessfulRequest_LogsAtDebug()
    {
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        var (client, log) = Build(handler);
        await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        // Debug, not Information: a library that logs at Information for every
        // request floods its consumer's default configuration.
        log.Entries.ShouldContain(e => e.Level == LogLevel.Debug && e.EventId == 1000);
        log.Entries.ShouldContain(e => e.Level == LogLevel.Debug && e.EventId == 1001);
        log.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
    }

    [Test]
    public async Task AMissingResource_LogsAtDebugNotWarning()
    {
        // Asking for a card that does not exist is an ordinary outcome, and
        // logging it louder would make normal use look faulty.
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.NotFound, "error-not-found.json");

        var (client, log) = Build(handler);
        await client.Cards.GetAsync("nope", CancellationToken.None);

        log.Entries.ShouldContain(e => e.EventId == 1002 && e.Level == LogLevel.Debug);
        log.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
    }

    [Test]
    public void AServerError_LogsAtErrorWithTheStatusAndDetail()
    {
        var handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.InternalServerError, """{"title":"boom"}""");

        var (client, log) = Build(handler);

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Wait();

        var entry = log.Entries.Where(e => e.EventId == 1003).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Message.ShouldContain("500");
        entry.Message.ShouldContain("boom");
    }

    [Test]
    public void ANetworkFailure_LogsAtErrorWithTheException()
    {
        var handler = new RecordingHandler()
            .RespondWith(_ => throw new HttpRequestException("no route to host"));

        var (client, log) = Build(handler);

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Wait();

        var entry = log.Entries.Where(e => e.EventId == 1004).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Exception.ShouldBeOfType<HttpRequestException>();
    }

    [Test]
    public void ATimeout_LogsAtWarning()
    {
        // Warning rather than Error: a timeout is usually transient and the
        // caller may well retry.
        var handler = new RecordingHandler()
            .RespondWith(_ => throw new TaskCanceledException("timed out"));

        var (client, log) = Build(handler);

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Wait();

        log.Entries.ShouldContain(e => e.EventId == 1005 && e.Level == LogLevel.Warning);
    }

    [Test]
    public void AMalformedBody_LogsAtWarningWithTheTargetType()
    {
        // The request succeeded, so this points at the API changing shape rather
        // than at the caller doing something wrong.
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, "<html>nope</html>");

        var (client, log) = Build(handler);

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Wait();

        var entry = log.Entries.Where(e => e.EventId == 1006).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("Card");
    }

    [Test]
    public async Task GraphQlSearch_LogsTheResultCount()
    {
        var handler = new RecordingHandler().RespondWith(
            HttpStatusCode.OK,
            """{"data":{"cards":[{"id":"a","name":"A","category":"Pokemon","localId":"1","set":{"id":"s","name":"S"}}]}}""");

        var (client, log) = Build(handler);
        await client.Cards.SearchDetailedAsync(new CardFilter { Name = "A" }, cancellationToken: CancellationToken.None);

        log.Entries.ShouldContain(e => e.EventId == 1300);
    }

    [Test]
    public async Task GraphQlDroppingAnUnresolvableEntry_LogsAtWarning()
    {
        // Dropping silently would leave a caller wondering why a card vanished.
        var handler = new RecordingHandler().RespondWith(
            HttpStatusCode.OK,
            """{"data":{"cards":[null,{"id":"a","name":"A","category":"Pokemon","localId":"1","set":{"id":"s","name":"S"}}]}}""");

        var (client, log) = Build(handler);
        await client.Cards.SearchDetailedAsync(new CardFilter { Name = "A" }, cancellationToken: CancellationToken.None);

        var entry = log.Entries.Where(e => e.EventId == 1302).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
    }

    [Test]
    public void ConfigurationIsLoggedOnceAtInformation()
    {
        var (_, log) = Build(new RecordingHandler());

        var entry = log.Entries.Where(e => e.EventId == 1200).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldContain("en");
    }

    // ----- cost when disabled -----

    [Test]
    public async Task WithLoggingDisabled_NoMessagesAreFormatted()
    {
        // The source generator checks IsEnabled before formatting, so a disabled
        // level must not even build the message string.
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        var (client, log) = Build(handler, LogLevel.None);
        await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        log.Entries.ShouldBeEmpty();
        log.FormatterInvocations.ShouldBe(0, "a disabled level must not format anything");
    }

    [Test]
    public async Task WithNoLoggerAtAll_TheClientStillWorks()
    {
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        var client = new TcgDexClient(new HttpClient(handler), new TcgDexOptions());

        (await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).ShouldNotBeNull();
    }

    [Test]
    public async Task OnlyWarningsAndAbove_AreSeenWhenFilteredThatWay()
    {
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        var (client, log) = Build(handler, LogLevel.Warning);
        await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        log.Entries.ShouldBeEmpty("a successful request has nothing worth warning about");
    }

    // ----- tracing -----

    [Test]
    public async Task AnOperation_EmitsAnActivityWithSemanticTags()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TcgDexActivity.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };

        var recorded = new List<Activity>();
        listener.ActivityStopped = recorded.Add;
        ActivitySource.AddActivityListener(listener);

        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        var (client, _) = Build(handler);
        await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        var activity = recorded.ShouldHaveSingleItem();
        activity.Kind.ShouldBe(ActivityKind.Client);
        activity.GetTagItem("url.full")!.ToString()!.ShouldContain("/cards/swsh3-136");
        activity.GetTagItem("http.request.method").ShouldBe("GET");
        activity.GetTagItem("http.response.status_code").ShouldBe(200);
    }

    [Test]
    public void AFailedOperation_MarksTheActivityAsError()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TcgDexActivity.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };

        var recorded = new List<Activity>();
        listener.ActivityStopped = recorded.Add;
        ActivitySource.AddActivityListener(listener);

        var handler = new RecordingHandler().RespondWith(HttpStatusCode.BadGateway, "{}");
        var (client, _) = Build(handler);

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Wait();

        var activity = recorded.ShouldHaveSingleItem();
        activity.Status.ShouldBe(ActivityStatusCode.Error);
        activity.GetTagItem("error.type").ShouldNotBeNull();
    }

    [Test]
    public async Task WithNoListener_NoActivityIsCreated()
    {
        // Tracing must be free for consumers who do not subscribe.
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        var (client, _) = Build(handler);
        await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        Activity.Current.ShouldBeNull();
    }

    [Test]
    public void TheActivitySourceName_IsStableAndPublic()
        => TcgDexActivity.SourceName.ShouldBe("TcgDex.CSharpSdk");
}

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
        RecordingLogger log = new(minimum);
        TcgDexClient client = new(new HttpClient(handler), new TcgDexOptions(), log.Factory);

        return (client, log);
    }

    /// <summary>Never answers; completes only by being cancelled.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            TaskCompletionSource<HttpResponseMessage> never =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            using (cancellationToken.Register(() => never.TrySetCanceled(cancellationToken)))
            {
                return await never.Task.ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A client whose SDK budget (200 ms) is the only limit, so the timeout it
    /// reports is genuinely the SDK's. A handler throwing TaskCanceledException
    /// is the pipeline cancelling, not the budget.
    /// </summary>
    private static (TcgDexClient Client, RecordingLogger Log) BuildTimingOut()
    {
        RecordingLogger log = new(LogLevel.Trace);
        TcgDexClient client = new(
            new HttpClient(new HangingHandler()) { Timeout = Timeout.InfiniteTimeSpan },
            new TcgDexOptions { Timeout = TimeSpan.FromMilliseconds(200) },
            log.Factory);

        return (client, log);
    }

    [Test]
    public void AnHttpBaseAddress_WarnsThatTrafficIsPlaintext()
    {
        // BaseAddress is deliberately overridable so callers can target a
        // mirror or a local stub, and http://localhost is a legitimate use of
        // that. But plaintext against a real host exposes every request and
        // response, and this SDK trusts the body enough to deserialize it — so
        // the case is worth saying out loud rather than validating away.
        RecordingLogger log = new(LogLevel.Trace);

        _ = new TcgDexClient(
            new HttpClient(new RecordingHandler()),
            new TcgDexOptions { BaseAddress = new Uri("http://api.tcgdex.net/v2/") },
            log.Factory);

        // Filtered to warnings: the client also logs ClientConfigured at
        // Information on construction, so asserting on every entry would be
        // asserting on unrelated output.
        LogEntry warning = log.Entries.Where(e => e.Level == LogLevel.Warning).ShouldHaveSingleItem();

        warning.Message.ShouldContain("plaintext");
        warning.Message.ShouldContain("http://api.tcgdex.net/v2/");
    }

    [Test]
    public void AnHttpsBaseAddress_WarnsAboutNothing()
    {
        // The ordinary case must stay silent, or the warning becomes noise that
        // everyone filters out — and a test that only proves the warning fires
        // would not notice it firing always.
        RecordingLogger log = new(LogLevel.Trace);

        _ = new TcgDexClient(new HttpClient(new RecordingHandler()), new TcgDexOptions(), log.Factory);

        log.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
    }

    [Test]
    public async Task ASuccessfulRequest_LogsAtDebug()
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        (TcgDexClient? client, RecordingLogger? log) = Build(handler);
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
        RecordingHandler handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.NotFound, "error-not-found.json");

        (TcgDexClient? client, RecordingLogger? log) = Build(handler);
        await client.Cards.GetAsync("nope", CancellationToken.None);

        log.Entries.ShouldContain(e => e.EventId == 1002 && e.Level == LogLevel.Debug);
        log.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
    }

    [Test]
    public void AServerError_LogsAtErrorWithTheStatusAndDetail()
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.InternalServerError, """{"title":"boom"}""");

        (TcgDexClient? client, RecordingLogger? log) = Build(handler);

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Wait();

        LogEntry entry = log.Entries.Where(e => e.EventId == 1003).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Message.ShouldContain("500");
        entry.Message.ShouldContain("boom");
    }

    [Test]
    public void ANetworkFailure_LogsAtErrorWithTheException()
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(_ => throw new HttpRequestException("no route to host"));

        (TcgDexClient? client, RecordingLogger? log) = Build(handler);

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Wait();

        LogEntry entry = log.Entries.Where(e => e.EventId == 1004).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Exception.ShouldBeOfType<HttpRequestException>();
    }

    [Test]
    public void ATimeout_LogsAtWarning()
    {
        // Warning rather than Error: a timeout is usually transient and the
        // caller may well retry.
        (TcgDexClient client, RecordingLogger log) = BuildTimingOut();
        using TcgDexClient _ = client;

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Wait();

        log.Entries.ShouldContain(e => e.EventId == 1005 && e.Level == LogLevel.Warning);
    }

    [Test]
    public void APipelineCancellation_LogsOnceAsAnErrorNotATimeout()
    {
        // Not the SDK's budget, so not the "timed out" event: that would name a
        // limit the caller never reached.
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(_ => throw new TaskCanceledException("cancelled"));

        (TcgDexClient client, RecordingLogger log) = Build(handler);

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Wait();

        log.Entries.Where(e => e.EventId == 1004).ShouldHaveSingleItem()
            .Exception.ShouldBeOfType<TaskCanceledException>();
        log.Entries.ShouldNotContain(e => e.EventId == 1005);
    }

    [Test]
    public void AMalformedBody_LogsAtWarningWithTheTargetType()
    {
        // The request succeeded, so this points at the API changing shape rather
        // than at the caller doing something wrong.
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, "<html>nope</html>");

        (TcgDexClient? client, RecordingLogger? log) = Build(handler);

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Wait();

        LogEntry entry = log.Entries.Where(e => e.EventId == 1006).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("Card");
    }

    [Test]
    public async Task GraphQLSearch_LogsTheResultCount()
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(
            HttpStatusCode.OK,
            """{"data":{"cards":[{"id":"a","name":"A","category":"Pokemon","localId":"1","set":{"id":"s","name":"S"}}]}}""");

        (TcgDexClient? client, RecordingLogger? log) = Build(handler);
        await client.Cards.SearchDetailedAsync(new CardFilter { Name = "A" }, cancellationToken: CancellationToken.None);

        log.Entries.ShouldContain(e => e.EventId == 1300);
    }

    [Test]
    public async Task GraphQLDroppingAnUnresolvableEntry_LogsAtWarning()
    {
        // Dropping silently would leave a caller wondering why a card vanished.
        RecordingHandler handler = new RecordingHandler().RespondWith(
            HttpStatusCode.OK,
            """{"data":{"cards":[null,{"id":"a","name":"A","category":"Pokemon","localId":"1","set":{"id":"s","name":"S"}}]}}""");

        (TcgDexClient? client, RecordingLogger? log) = Build(handler);
        await client.Cards.SearchDetailedAsync(new CardFilter { Name = "A" }, cancellationToken: CancellationToken.None);

        LogEntry entry = log.Entries.Where(e => e.EventId == 1302).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
    }

    [Test]
    public void ConfigurationIsLoggedOnceAtInformation()
    {
        (TcgDexClient _, RecordingLogger? log) = Build(new RecordingHandler());

        LogEntry entry = log.Entries.Where(e => e.EventId == 1200).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldContain("en");
    }

    // ----- cost when disabled -----

    [Test]
    public async Task WithLoggingDisabled_NoMessagesAreFormatted()
    {
        // The source generator checks IsEnabled before formatting, so a disabled
        // level must not even build the message string.
        RecordingHandler handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        (TcgDexClient? client, RecordingLogger? log) = Build(handler, LogLevel.None);
        await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        log.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
        log.FormatterInvocations.ShouldBe(0, "a disabled level must not format anything");
    }

    [Test]
    public async Task WithNoLoggerAtAll_TheClientStillWorks()
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        TcgDexClient client = new(new HttpClient(handler), new TcgDexOptions());

        // Named, not merely non-null: the risk with no logger is a null factory
        // reaching the logging call and throwing, and a card that deserialized
        // to the right name proves the whole path ran rather than that some
        // object came back.
        (await client.Cards.GetAsync("swsh3-136", CancellationToken.None))
            .ShouldNotBeNull().Name.ShouldBe("Furret");
    }

    [Test]
    public async Task OnlyWarningsAndAbove_AreSeenWhenFilteredThatWay()
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        (TcgDexClient? client, RecordingLogger? log) = Build(handler, LogLevel.Warning);
        await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        log.Entries.ShouldBeEmpty("a successful request has nothing worth warning about");
    }

    // ----- tracing -----

    /// <summary>
    /// Builds a listener that records every stopped SDK activity.
    /// </summary>
    private static (ActivityListener Listener, List<Activity> Recorded) ListenForActivities()
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == TcgDexActivity.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };

        List<Activity> recorded = new();
        listener.ActivityStopped = recorded.Add;
        ActivitySource.AddActivityListener(listener);

        return (listener, recorded);
    }

    [Test]
    public void ANetworkFailure_MarksTheActivityAsError()
    {
        // The existing error-marking test uses a 502, which reaches the failure
        // path through a *response*. This reaches it through an *exception*,
        // which is a different call site — and one where removing the
        // RecordFailure call went unnoticed.
        (ActivityListener? listener, List<Activity>? recorded) = ListenForActivities();
        using ActivityListener _ = listener;

        RecordingHandler handler = new RecordingHandler()
            .RespondWith(_ => throw new HttpRequestException("connection reset"));

        (TcgDexClient? client, RecordingLogger _) = Build(handler);

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Wait();

        Activity activity = recorded.ShouldHaveSingleItem();
        activity.Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Test]
    public void AnOversizedBody_LogsOnceAndMarksTheActivityAsError()
    {
        // The body read is a third failure site after the send and the status
        // check, and it reported neither a log entry nor a failed span.
        (ActivityListener? listener, List<Activity>? recorded) = ListenForActivities();
        using ActivityListener _ = listener;

        RecordingLogger log = new(LogLevel.Trace);
        using TcgDexClient client = new(
            new HttpClient(new RecordingHandler().RespondWith(HttpStatusCode.OK, """{"id":"swsh3-136"}""")),
            new TcgDexOptions { MaxResponseBytes = 4 },
            log.Factory);

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Wait();

        log.Entries.Where(e => e.Level >= LogLevel.Warning).ShouldHaveSingleItem().EventId.ShouldBe(1004);
        recorded.ShouldHaveSingleItem().Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Test]
    public void AMalformedBody_MarksTheActivityAsError()
    {
        (ActivityListener? listener, List<Activity>? recorded) = ListenForActivities();
        using ActivityListener _ = listener;

        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, "<html>");

        (TcgDexClient? client, RecordingLogger _) = Build(handler);

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Wait();

        recorded.ShouldHaveSingleItem().Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Test]
    public void ATimeout_MarksTheActivityAsError()
    {
        // The other exception-shaped failure, and the other RecordFailure call.
        (ActivityListener? listener, List<Activity>? recorded) = ListenForActivities();
        using ActivityListener _ = listener;

        (TcgDexClient client, RecordingLogger _) = BuildTimingOut();
        using TcgDexClient __ = client;

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Wait();

        Activity activity = recorded.ShouldHaveSingleItem();
        activity.Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Test]
    public async Task ARequest_NamesItsActivityAndLogsItsMethod()
    {
        // Both strings are what an operator filters on; blanking either left
        // every other assertion green.
        (ActivityListener? listener, List<Activity>? recorded) = ListenForActivities();
        using ActivityListener _ = listener;

        RecordingHandler handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        (TcgDexClient? client, RecordingLogger? log) = Build(handler);
        await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        recorded.ShouldHaveSingleItem().DisplayName.ShouldBe("TCGdex Card");
        log.Entries.Single(e => e.EventId == 1000).Message
            .ShouldBe("TCGdex request GET https://api.tcgdex.net/v2/en/cards/swsh3-136");
    }

    [Test]
    public void APipelineCancellation_MarksTheActivityAsError_AndNamesHttpClientTimeout()
    {
        // Neither the caller nor the SDK's budget cancelled: a handler did. That
        // is its own call site for RecordFailure, and its message must point at
        // the pipeline rather than at TcgDexOptions.Timeout.
        (ActivityListener? listener, List<Activity>? recorded) = ListenForActivities();
        using ActivityListener _ = listener;

        RecordingHandler handler = new RecordingHandler()
            .RespondWith(_ => throw new TaskCanceledException("a handler gave up"));

        (TcgDexClient? client, RecordingLogger _) = Build(handler);

        TcgDexApiException error = Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Result;

        error.Message.ShouldContain("HttpClient.Timeout");
        recorded.ShouldHaveSingleItem().Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Test]
    public void AHandlersOwnApiException_IsLoggedAndMarksTheActivityAsError()
    {
        // A handler in the pipeline (the caching handler failing a body read)
        // throws the SDK's own type. It is rethrown as-is, but must still be
        // reported like every other failure.
        (ActivityListener? listener, List<Activity>? recorded) = ListenForActivities();
        using ActivityListener _ = listener;

        RecordingHandler handler = new RecordingHandler()
            .RespondWith(_ => throw new TcgDexApiException("a handler failed"));

        (TcgDexClient? client, RecordingLogger? log) = Build(handler);

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Result
            .Message.ShouldBe("a handler failed");

        log.Entries.Where(e => e.Level >= LogLevel.Warning).ShouldHaveSingleItem().EventId.ShouldBe(1004);
        recorded.ShouldHaveSingleItem().Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Test]
    public void ATimeout_TellsTheCallerWhichOptionToRaise()
    {
        (TcgDexClient client, RecordingLogger _) = BuildTimingOut();
        using TcgDexClient __ = client;

        TcgDexApiException error = Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Result;

        error.Message.ShouldContain("Raise TcgDexOptions.Timeout");
    }

    [Test]
    public async Task TheLoggedDuration_IsAPlausibleNumberOfMilliseconds()
    {
        // The elapsed time is computed by hand from Stopwatch timestamps, and
        // arithmetic mutations there produced values in the billions while
        // every test still passed — nothing asserted the number at all.
        //
        // A range rather than an exact value: the point is to catch a broken
        // scale factor, not to measure the machine.
        RecordingHandler handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        (TcgDexClient? client, RecordingLogger? log) = Build(handler);
        await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        string text = log.Entries.Single(e => e.Message.Contains("returned 200 in")).Message;

        // "... returned 200 in 3ms"
        // Substring rather than Split: the Split(string) overload is .NET Core
        // 2.0+ and this suite also runs on net472, while the array overload
        // trips CA1861 on a literal argument.
        int start = text.LastIndexOf(" in ", StringComparison.Ordinal) + " in ".Length;
        string digits = text.Substring(start).Replace("ms", string.Empty);
        long milliseconds = long.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);

        milliseconds.ShouldBeGreaterThanOrEqualTo(0);
        milliseconds.ShouldBeLessThan(60_000);
    }

    [Test]
    public async Task AnOperation_EmitsAnActivityWithSemanticTags()
    {
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == TcgDexActivity.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };

        List<Activity> recorded = new();
        listener.ActivityStopped = recorded.Add;
        ActivitySource.AddActivityListener(listener);

        RecordingHandler handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        (TcgDexClient? client, RecordingLogger _) = Build(handler);
        await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        Activity activity = recorded.ShouldHaveSingleItem();
        activity.Kind.ShouldBe(ActivityKind.Client);
        activity.GetTagItem("url.full")!.ToString()!.ShouldContain("/cards/swsh3-136");
        activity.GetTagItem("http.request.method").ShouldBe("GET");
        activity.GetTagItem("http.response.status_code").ShouldBe(200);
    }

    [Test]
    public void AFailedOperation_MarksTheActivityAsError()
    {
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == TcgDexActivity.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };

        List<Activity> recorded = new();
        listener.ActivityStopped = recorded.Add;
        ActivitySource.AddActivityListener(listener);

        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.BadGateway, "{}");
        (TcgDexClient? client, RecordingLogger _) = Build(handler);

        Should.ThrowAsync<TcgDexApiException>(async () =>
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).Wait();

        Activity activity = recorded.ShouldHaveSingleItem();
        activity.Status.ShouldBe(ActivityStatusCode.Error);
        activity.GetTagItem("error.type").ShouldNotBeNull();
    }

    [Test]
    public async Task WithNoListener_NoActivityIsCreated()
    {
        // Tracing must be free for consumers who do not subscribe.
        RecordingHandler handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        (TcgDexClient? client, RecordingLogger _) = Build(handler);
        await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        Activity.Current.ShouldBeNull();
    }

    [Test]
    public void TheActivitySourceName_IsStableAndPublic()
        => TcgDexActivity.SourceName.ShouldBe("TcgDex.CSharpSdk");

    [Test]
    public async Task ACardWithANamelessAttack_WarnsThatTheApiDataIsMalformed()
    {
        // 2017sm-5 is served with a nameless second attack ("Electro Ball" on the
        // physical card). The SDK reads it — Name is null — and says so in the log
        // rather than fabricating a name, so a caller can tell the API, not the
        // SDK, produced the hole.
        RecordingHandler handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-nameless-attack.json");
        (TcgDexClient client, RecordingLogger log) = Build(handler);

        await client.Cards.GetAsync("2017sm-5", CancellationToken.None);

        LogEntry entry = log.Entries.Where(e => e.EventId == 1400).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("2017sm-5");
        entry.Message.ShouldContain("attack 2 has no name");
    }

    [Test]
    public async Task ACleanCard_LogsNoMalformedDataWarning()
    {
        // The control: a card with every attack named must produce no 1400 event.
        // Without it, a warning that fired on every card would pass the test above
        // and be pure noise.
        RecordingHandler handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");
        (TcgDexClient client, RecordingLogger log) = Build(handler);

        await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        log.Entries.ShouldNotContain(e => e.EventId == 1400);
    }

    [Test]
    public async Task ACardWithANamelessAbility_WarnsAndReadsTheCard()
    {
        // Synthetic — no confirmed real card has a nameless ability, but
        // Ability.Name is nullable for the same robustness reason as Attack.Name,
        // so the ability path warns too. The card also has a *named* attack, which
        // must NOT warn, so this pins that the two branches are independent.
        RecordingHandler handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-nameless-ability.json");
        (TcgDexClient client, RecordingLogger log) = Build(handler);

        TcgDex.Models.Card card = (await client.Cards.GetAsync(
            "synthetic-nameless-ability", CancellationToken.None)).ShouldNotBeNull();

        card.Abilities.ShouldHaveSingleItem().Name.ShouldBeNull();
        LogEntry entry = log.Entries.Where(e => e.EventId == 1400).ShouldHaveSingleItem();
        entry.Message.ShouldContain("ability 1 has no name");
    }
}

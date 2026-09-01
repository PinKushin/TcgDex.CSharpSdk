namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;
using TcgDex.Tests.Caching;

/// <summary>
/// Rotation to another endpoint when one cannot serve a request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every response body names the host that produced it.</b> If both stub
/// endpoints returned the same payload, a test asserting only "a card came back"
/// would pass whether or not failover ever happened — it would be measuring that
/// <i>something</i> answered rather than <i>which node</i> did. Naming the host
/// is what makes the assertion sensitive to the behaviour under test.
/// </para>
/// <para>
/// <b>Half of these are controls.</b> A rotation feature is only correct if it
/// also declines to rotate: <c>404</c> is a real answer about a card that does
/// not exist, and rotating would send every missing card to every node in the
/// list; <c>429</c> is a rate limit, and spreading it across endpoints is
/// evasion rather than resilience. Tests that only prove failover happens would
/// pass against a handler that retries everything.
/// </para>
/// <para>
/// Nothing here touches the network: every request terminates at an in-process
/// handler. The hosts are real TCGdex node names because URI rewriting is under
/// test, but no socket is ever opened.
/// </para>
/// </remarks>
[TestFixture]
public sealed class FailoverHandlerTests
{
    private static readonly Uri Primary = new("https://api.tcgdex.net/v2/");
    private static readonly Uri Secondary = new("https://api.eu2.tcgdex.net/v2/");
    private static readonly Uri Tertiary = new("https://api.na1.tcgdex.net/v2/");

    /// <summary>A body naming the host, so a test can tell which node answered.</summary>
    private static HttpResponseMessage From(HttpRequestMessage request, HttpStatusCode status)
        => new(status)
        {
            Content = new StringContent(
                $"{{\"servedBy\":\"{request.RequestUri!.Host}\"}}",
                System.Text.Encoding.UTF8,
                "application/json"),
        };

    private static TcgDexFailoverHandler Handler(
        HttpMessageHandler inner,
        IReadOnlyList<Uri>? endpoints = null,
        TimeSpan? attemptTimeout = null,
        TimeSpan? cooldown = null,
        TimeProvider? time = null)
        => new(
            Primary,
            endpoints ?? [Secondary],
            attemptTimeout ?? TimeSpan.FromSeconds(10),
            cooldown ?? TimeSpan.FromMinutes(5),
            time)
        {
            InnerHandler = inner,
        };

    private static Uri Card => new(Primary, "en/cards/swsh3-136");

    [Test]
    public async Task ABadGateway_IsRetriedAgainstTheNextEndpoint()
    {
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, HttpStatusCode.BadGateway))
            .RespondWith(request => From(request, HttpStatusCode.OK));

        using HttpClient client = new(Handler(inner));

        using HttpResponseMessage response = await client.GetAsync(Card, CancellationToken.None);

        // Which node served it, not merely that something did.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("api.eu2.tcgdex.net");

        // And the rewrite kept the path, rather than only swapping the host of a
        // request that had lost its way.
        inner.Requests.Count.ShouldBe(2);
        inner.Requests[0].RequestUri!.ToString()
            .ShouldBe("https://api.tcgdex.net/v2/en/cards/swsh3-136");
        inner.Requests[1].RequestUri!.ToString()
            .ShouldBe("https://api.eu2.tcgdex.net/v2/en/cards/swsh3-136");
    }

    [TestCase(HttpStatusCode.ServiceUnavailable)]
    [TestCase(HttpStatusCode.GatewayTimeout)]
    public async Task TheOtherGatewayFailures_AreAlsoRetried(HttpStatusCode status)
    {
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, status))
            .RespondWith(request => From(request, HttpStatusCode.OK));

        using HttpClient client = new(Handler(inner));

        using HttpResponseMessage response = await client.GetAsync(Card, CancellationToken.None);

        (await response.Content.ReadAsStringAsync()).ShouldContain("api.eu2.tcgdex.net");
    }

    // ---- Controls: statuses that are answers, and must terminate ----

    [Test]
    public async Task ANotFound_IsNotRetried()
    {
        // The control that matters most. A missing card is a normal outcome, and
        // rotating on it would send every absent card to every configured node —
        // turning the most common non-success response into an amplifier.
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, HttpStatusCode.NotFound));

        using HttpClient client = new(Handler(inner, [Secondary, Tertiary]));

        using HttpResponseMessage response = await client.GetAsync(Card, CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        inner.Requests.Count.ShouldBe(1);
        inner.Requests[0].RequestUri!.Host.ShouldBe("api.tcgdex.net");
    }

    [Test]
    public async Task TooManyRequests_IsNotRetried()
    {
        // Rotating on a rate limit spreads the same load across nodes to get
        // around it. That is evasion, not resilience.
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, (HttpStatusCode)429));

        using HttpClient client = new(Handler(inner, [Secondary, Tertiary]));

        using HttpResponseMessage response = await client.GetAsync(Card, CancellationToken.None);

        ((int)response.StatusCode).ShouldBe(429);
        inner.Requests.Count.ShouldBe(1);
    }

    [TestCase(HttpStatusCode.OK)]
    [TestCase(HttpStatusCode.NotModified)]
    [TestCase(HttpStatusCode.InternalServerError)]
    [TestCase(HttpStatusCode.Forbidden)]
    public async Task AnAnsweredRequest_IsNotRetried(HttpStatusCode status)
    {
        // A 500 is included deliberately: it is a fault, but one the next node
        // will almost certainly reproduce, so it is an answer rather than a
        // failure to serve.
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, status));

        using HttpClient client = new(Handler(inner, [Secondary, Tertiary]));

        using HttpResponseMessage response = await client.GetAsync(Card, CancellationToken.None);

        response.StatusCode.ShouldBe(status);
        inner.Requests.Count.ShouldBe(1);
    }

    [Test]
    public async Task ARequestWithContent_IsNotRetried()
    {
        // GraphQL posts a body. Replaying one would assume it is safe to repeat,
        // which is an assumption this handler does not make: only GET rotates.
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, HttpStatusCode.BadGateway));

        using HttpClient client = new(Handler(inner));
        using StringContent body = new("{}", System.Text.Encoding.UTF8, "application/json");
        using HttpRequestMessage post = new(HttpMethod.Post, new Uri(Primary, "graphql"))
        {
            Content = body,
        };

        using HttpResponseMessage response = await client.SendAsync(post, CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        inner.Requests.Count.ShouldBe(1);
    }

    // ---- Transport failures ----

    [Test]
    public async Task AConnectionFailure_IsRetried()
    {
        ThrowingHandler inner = new(
            new HttpRequestException("connection refused"), succeedOnAttempt: 2);

        using HttpClient client = new(Handler(inner));

        using HttpResponseMessage response = await client.GetAsync(Card, CancellationToken.None);

        (await response.Content.ReadAsStringAsync()).ShouldContain("api.eu2.tcgdex.net");
        inner.Seen.Count.ShouldBe(2);
    }

    [Test]
    public async Task AHungEndpoint_IsAbandonedAtTheAttemptTimeout()
    {
        // The reason a per-attempt budget exists. The caller's token carries no
        // deadline at all here, so the ONLY thing that can end the first attempt
        // is the attempt timeout — which is what distinguishes this from a test
        // that would pass just as well if the total request budget had fired.
        HangsOnceHandler inner = new();

        using HttpClient client = new(
            Handler(inner, attemptTimeout: TimeSpan.FromMilliseconds(100)));

        using HttpResponseMessage response = await client.GetAsync(Card, CancellationToken.None);

        (await response.Content.ReadAsStringAsync()).ShouldContain("api.eu2.tcgdex.net");
        inner.Seen.Count.ShouldBe(2);
    }

    [Test]
    public async Task CallerCancellation_IsNotTreatedAsAnEndpointFailure()
    {
        // Cancellation the caller asked for is an instruction to stop, not a
        // fault to route around. Rotating here would ignore it.
        HangsOnceHandler inner = new();
        using CancellationTokenSource caller = new();

        using HttpClient client = new(
            Handler(inner, attemptTimeout: Timeout.InfiniteTimeSpan));

        Task<HttpResponseMessage> pending = client.GetAsync(Card, caller.Token);
        caller.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        inner.Seen.Count.ShouldBe(1);
    }

    [Test]
    public async Task WhenEveryEndpointFails_TheLastResponseIsReturned()
    {
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, HttpStatusCode.BadGateway))
            .RespondWith(request => From(request, HttpStatusCode.BadGateway));

        using HttpClient client = new(Handler(inner));

        using HttpResponseMessage response = await client.GetAsync(Card, CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        inner.Requests.Count.ShouldBe(2);
    }

    [Test]
    public async Task NoMoreThanThreeEndpointsAreTried()
    {
        // Trying every configured endpoint would multiply load on an API that is
        // already struggling. Four are offered; three are used.
        ThrowingHandler inner = new(new HttpRequestException("down"));

        using HttpClient client = new(
            Handler(inner, [Secondary, Tertiary, new Uri("https://api.as1.tcgdex.net/v2/")]));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.GetAsync(Card, CancellationToken.None));

        inner.Seen.Count.ShouldBe(3);
    }

    // ---- Cooldown ----

    [Test]
    public async Task AFailedEndpoint_IsSkippedWhileItIsCoolingOff()
    {
        // Without this every request pays the dead endpoint's failure before
        // reaching a live one, which is exactly the load amplification the
        // cooldown exists to prevent.
        FakeTimeProvider time = new();
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, HttpStatusCode.BadGateway))
            .RespondWith(request => From(request, HttpStatusCode.OK))
            .RespondWith(request => From(request, HttpStatusCode.OK));

        using HttpClient client = new(Handler(inner, time: time));

        using (await client.GetAsync(Card, CancellationToken.None))
        {
        }

        using (HttpResponseMessage second = await client.GetAsync(Card, CancellationToken.None))
        {
            (await second.Content.ReadAsStringAsync()).ShouldContain("api.eu2.tcgdex.net");
        }

        // Three in total: the first call spent two (the primary's 502, then the
        // rotation), and the second spent ONE by going straight to the endpoint
        // that works. A fourth would mean the dead primary was probed again,
        // which is the cost the cooldown exists to remove.
        inner.Requests.Count.ShouldBe(3);
        inner.Requests[0].RequestUri!.Host.ShouldBe("api.tcgdex.net");
        inner.Requests[1].RequestUri!.Host.ShouldBe("api.eu2.tcgdex.net");
        inner.Requests[2].RequestUri!.Host.ShouldBe("api.eu2.tcgdex.net");
    }

    [Test]
    public async Task AFailedEndpoint_IsTriedAgainOnceTheCooldownExpires()
    {
        // The other half: a cooldown that never expires is an outage the client
        // never recovers from.
        FakeTimeProvider time = new();
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, HttpStatusCode.BadGateway))
            .RespondWith(request => From(request, HttpStatusCode.OK))
            .RespondWith(request => From(request, HttpStatusCode.OK));

        using HttpClient client = new(
            Handler(inner, cooldown: TimeSpan.FromMinutes(5), time: time));

        using (await client.GetAsync(Card, CancellationToken.None))
        {
        }

        time.Advance(TimeSpan.FromMinutes(6));

        using (HttpResponseMessage third = await client.GetAsync(Card, CancellationToken.None))
        {
            (await third.Content.ReadAsStringAsync()).ShouldContain("api.tcgdex.net");
        }

        inner.Requests[2].RequestUri!.Host.ShouldBe("api.tcgdex.net");
    }

    [Test]
    public async Task AZeroCooldown_RetriesAFailedEndpointOnTheNextRequest()
    {
        // Zero is documented as meaningful rather than as "off": it re-probes a
        // failed endpoint every time, which is what a caller wants when the
        // fallback is a slower or less preferred server.
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, HttpStatusCode.BadGateway))
            .RespondWith(request => From(request, HttpStatusCode.OK))
            .RespondWith(request => From(request, HttpStatusCode.BadGateway))
            .RespondWith(request => From(request, HttpStatusCode.OK));

        using HttpClient client = new(Handler(inner, cooldown: TimeSpan.Zero));

        using (await client.GetAsync(Card, CancellationToken.None))
        {
        }

        using (await client.GetAsync(Card, CancellationToken.None))
        {
        }

        // The second call tried the primary again rather than skipping it.
        inner.Requests.Count.ShouldBe(4);
        inner.Requests[2].RequestUri!.Host.ShouldBe("api.tcgdex.net");
    }

    [Test]
    public async Task WhenEveryEndpointIsCoolingOff_ThePrimaryIsTriedAnyway()
    {
        // Refusing to send at all would turn a transient outage into a hard
        // failure that outlives it — something has to discover the service is
        // back, and the alternative is a client that never recovers.
        FakeTimeProvider time = new();
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, HttpStatusCode.BadGateway))
            .RespondWith(request => From(request, HttpStatusCode.BadGateway))
            .RespondWith(request => From(request, HttpStatusCode.OK));

        using HttpClient client = new(Handler(inner, time: time));

        // Both endpoints fail, so both are now cooling off.
        using (await client.GetAsync(Card, CancellationToken.None))
        {
        }

        using (HttpResponseMessage recovered = await client.GetAsync(Card, CancellationToken.None))
        {
            (await recovered.Content.ReadAsStringAsync()).ShouldContain("api.tcgdex.net");
        }

        inner.Requests.Count.ShouldBe(3);
        inner.Requests[2].RequestUri!.Host.ShouldBe("api.tcgdex.net");
    }

    [Test]
    public async Task TheRetry_CarriesTheRequestHeaders()
    {
        // The response cache sits above this handler and attaches If-None-Match.
        // Dropping it on the rewrite would silently turn a revalidation that
        // costs a 304 and no body into a full fetch — invisible to every
        // assertion about status codes.
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, HttpStatusCode.BadGateway))
            .RespondWith(request => From(request, HttpStatusCode.OK));

        using HttpClient client = new(Handler(inner));
        using HttpRequestMessage request = new(HttpMethod.Get, Card);
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"etag-1\"");

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        inner.Requests[1].Headers.GetValues("If-None-Match").ShouldBe(["\"etag-1\""]);
    }

    [Test]
    public async Task TheRewrite_PreservesTheQueryString()
    {
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, HttpStatusCode.BadGateway))
            .RespondWith(request => From(request, HttpStatusCode.OK));

        using HttpClient client = new(Handler(inner));

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(Primary, "en/cards?name=eq:Furret&hp=gt:100"), CancellationToken.None);

        inner.Requests[1].RequestUri!.ToString()
            .ShouldBe("https://api.eu2.tcgdex.net/v2/en/cards?name=eq:Furret&hp=gt:100");
    }

    /// <summary>
    /// Fails every request with the same transport error, except optionally the
    /// nth, which succeeds.
    /// </summary>
    private sealed class ThrowingHandler(Exception error, int? succeedOnAttempt = null)
        : HttpMessageHandler
    {
        internal List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Seen.Add(request.RequestUri!);

            return Seen.Count == succeedOnAttempt
                ? Task.FromResult(From(request, HttpStatusCode.OK))
                : Task.FromException<HttpResponseMessage>(error);
        }
    }

    /// <summary>
    /// Never answers the first request; answers every later one.
    /// </summary>
    /// <remarks>
    /// The wait is on the cancellation token, not on a duration — the test
    /// synchronises on the attempt budget firing rather than guessing how long
    /// that takes.
    /// </remarks>
    private sealed class HangsOnceHandler : HttpMessageHandler
    {
        internal List<Uri> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Seen.Add(request.RequestUri!);

            if (Seen.Count == 1)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            return From(request, HttpStatusCode.OK);
        }
    }
}

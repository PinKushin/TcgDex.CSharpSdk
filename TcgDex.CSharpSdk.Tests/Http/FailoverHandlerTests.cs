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

    private static readonly Uri GraphQl = new("https://api.tcgdex.net/v2/graphql");

    private static TcgDexFailoverHandler Handler(
        HttpMessageHandler inner,
        IReadOnlyList<Uri>? endpoints = null,
        TimeSpan? attemptTimeout = null,
        TimeSpan? cooldown = null,
        TimeProvider? time = null)
        // Deduplicated the same way the real construction sites do, so the
        // cooldown state is sized against the endpoints actually used.
        => Build(inner, TcgDexFailoverHandler.Deduplicate(endpoints ?? [Secondary], Primary),
            attemptTimeout, cooldown, time);

    private static TcgDexFailoverHandler Build(
        HttpMessageHandler inner,
        IReadOnlyList<Uri> endpoints,
        TimeSpan? attemptTimeout,
        TimeSpan? cooldown,
        TimeProvider? time)
        => new(
            Primary,
            GraphQl,
            endpoints,
            attemptTimeout ?? TimeSpan.FromSeconds(10),
            cooldown ?? TimeSpan.FromMinutes(5),
            new FailoverCooldowns(endpoints.Count + 1),
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
    public async Task AGraphQlPost_IsRetried_WithItsBody()
    {
        // Admitted by address, not by method: TCGdex's GraphQL schema has queries
        // and no mutations, and this SDK built the body — so replaying it is
        // knowledge rather than an assumption.
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, HttpStatusCode.BadGateway))
            .RespondWith(request => From(request, HttpStatusCode.OK));

        using HttpClient client = new(Handler(inner));
        using StringContent body = new(
            """{"query":"{ card(id:\"swsh3-136\"){ name } }"}""",
            System.Text.Encoding.UTF8,
            "application/json");
        using HttpRequestMessage post = new(HttpMethod.Post, GraphQl) { Content = body };

        using HttpResponseMessage response = await client.SendAsync(post, CancellationToken.None);

        (await response.Content.ReadAsStringAsync()).ShouldContain("api.eu2.tcgdex.net");

        inner.Requests.Count.ShouldBe(2);
        inner.Requests[1].RequestUri!.ToString()
            .ShouldBe("https://api.eu2.tcgdex.net/v2/graphql");

        // The method too. HttpRequestMessage permits a GET with content, so a
        // clone built as `new(HttpMethod.Get, uri)` would satisfy the URI, the
        // body and the content type while silently degrading the query to a GET.
        inner.Requests[1].Method.ShouldBe(HttpMethod.Post);

        // The body has to survive the rewrite, or the retry reaches the right
        // address with an empty query — which the server answers with an error
        // that looks nothing like a failover problem.
        inner.RequestBodies[1].ShouldBe(inner.RequestBodies[0]);
        inner.RequestBodies[1].ShouldContain("swsh3-136");

        // And its content headers, or the server rejects the media type.
        inner.Requests[1].Content.ShouldNotBeNull()
            .Headers.ContentType.ShouldNotBeNull()
            .MediaType.ShouldBe("application/json");
    }

    [Test]
    public async Task TheRetry_DoesNotCarryTheCallersCredentials()
    {
        // A consumer may pass in an HttpClient they share with the rest of their
        // application — the SDK documents that as supported — and
        // DefaultRequestHeaders are merged into every request before the handler
        // chain runs. Copying the whole collection would send that client's
        // Authorization to whatever host is in the failover list, including an
        // unofficial mirror. The runtime strips these across a redirect origin
        // change; a failover changes origin by definition.
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, HttpStatusCode.BadGateway))
            .RespondWith(request => From(request, HttpStatusCode.OK));

        using HttpClient client = new(Handler(inner));
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer secret");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "session=secret");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Trace", "keep-me");

        using HttpResponseMessage response = await client.GetAsync(Card, CancellationToken.None);

        // The primary is the host the caller aimed at, so it keeps them.
        inner.Requests[0].Headers.Contains("Authorization").ShouldBeTrue();

        // The fallback is a different host and must not.
        inner.Requests[1].Headers.Contains("Authorization").ShouldBeFalse();
        inner.Requests[1].Headers.Contains("Cookie").ShouldBeFalse();

        // Control: ordinary headers still travel, or the retry would lose the
        // If-None-Match the cache above depends on.
        inner.Requests[1].Headers.GetValues("X-Trace").ShouldBe(["keep-me"]);
    }

    [Test]
    public async Task AConnectionFailure_CoolsTheEndpointOff()
    {
        // Every other cooldown test drives failure through a 502, which reaches
        // MarkFailed on the status path only. Deleting MarkFailed from the
        // HttpRequestException catch left the whole suite green — and a refused
        // connection is the MOST common outage shape, so every later request
        // would pay a full connect failure before rotating.
        FakeTimeProvider time = new();
        AlwaysHandler inner = new(refuseConnection: true);

        using HttpClient client = new(Handler(inner, time: time));

        using (await client.GetAsync(Card, CancellationToken.None))
        {
        }

        using (await client.GetAsync(Card, CancellationToken.None))
        {
        }

        // Three, not four: the second request skipped the endpoint that refused.
        inner.Seen.Count.ShouldBe(3);
        inner.Seen[2].Host.ShouldBe("api.eu2.tcgdex.net");
    }

    [Test]
    public async Task AHungEndpoint_CoolsTheEndpointOff()
    {
        // The same gap on the per-attempt-timeout path. Without the cooldown a
        // hung node costs the full attempt budget on every request, forever.
        FakeTimeProvider time = new();
        HangsOnceHandler inner = new();

        using HttpClient client = new(
            Handler(inner, attemptTimeout: TimeSpan.FromMilliseconds(100), time: time))
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        using (await client.GetAsync(Card, CancellationToken.None))
        {
        }

        using (await client.GetAsync(Card, CancellationToken.None))
        {
        }

        inner.Seen.Count.ShouldBe(3);
        inner.Seen[2].Host.ShouldBe("api.eu2.tcgdex.net");
    }

    [Test]
    public async Task APostAnywhereElse_IsNotRetried()
    {
        // The control that keeps the rule narrow. Replaying a request the SDK did
        // not author would be deciding on a caller's behalf that it is safe to
        // repeat — which is exactly what this handler refuses to do.
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, HttpStatusCode.BadGateway));

        using HttpClient client = new(Handler(inner));
        using StringContent body = new("{}", System.Text.Encoding.UTF8, "application/json");
        using HttpRequestMessage post = new(HttpMethod.Post, new Uri(Primary, "en/cards"))
        {
            Content = body,
        };

        using HttpResponseMessage response = await client.SendAsync(post, CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        inner.Requests.Count.ShouldBe(1);
    }

    // ---- Transport failures ----

    [Test]
    public void CooldownStateSizedForTheWrongNumberOfEndpoints_Throws()
    {
        // The state is created by the caller so it can be shared across handler
        // instances, which means the caller can also size it wrongly. An
        // undersized array would throw IndexOutOfRange on the first failure of a
        // later endpoint — at the moment of an outage, from inside a handler.
        // Failing at construction turns that into an immediate, readable error.
        using RecordingHandler inner = new();

        Should.Throw<ArgumentException>(() => new TcgDexFailoverHandler(
            Primary,
            GraphQl,
            [Secondary, Tertiary],
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(5),
            new FailoverCooldowns(2))
        {
            InnerHandler = inner,
        });
    }

    [Test]
    public async Task ARequestOutsideTheBaseAddress_IsNotRetried()
    {
        // Removing the IsBaseOf guard does not merely disable rewriting: for a
        // URI outside the base, MakeRelativeUri returns the target absolute, so
        // the rebuilt address is the SAME foreign host — and it would be sent
        // three times. The DI path hands consumers an IHttpClientBuilder for this
        // client, so a request to another host is reachable.
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, HttpStatusCode.BadGateway));

        using HttpClient client = new(Handler(inner));

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("https://other.example/something"), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        inner.Requests.Count.ShouldBe(1);
    }

    [Test]
    public async Task APutToTheGraphQlEndpoint_IsNotRetried()
    {
        // Separates the two halves of the replay rule. The GraphQL endpoint is
        // admitted for POST specifically; without the method check, any verb
        // aimed at that address would be replayed.
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, HttpStatusCode.BadGateway));

        using HttpClient client = new(Handler(inner));
        using StringContent body = new("{}", System.Text.Encoding.UTF8, "application/json");
        using HttpRequestMessage put = new(HttpMethod.Put, GraphQl) { Content = body };

        using HttpResponseMessage response = await client.SendAsync(put, CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        inner.Requests.Count.ShouldBe(1);
    }

    [Test]
    public async Task AnEndpointEqualToThePrimary_IsNotTriedTwice()
    {
        // `UseMirror(Eu2).UseFailover()` is the natural way to write this, since
        // the two are documented side by side — and it would otherwise send a
        // failed request straight back to the node that just failed.
        RecordingHandler inner = new RecordingHandler()
            .RespondWith(request => From(request, HttpStatusCode.BadGateway))
            .RespondWith(request => From(request, HttpStatusCode.OK));

        using HttpClient client = new(Handler(inner, [Primary, Secondary]));

        using HttpResponseMessage response = await client.GetAsync(Card, CancellationToken.None);

        (await response.Content.ReadAsStringAsync()).ShouldContain("api.eu2.tcgdex.net");

        // Two requests, not three: the duplicate primary was dropped.
        inner.Requests.Count.ShouldBe(2);
        inner.Requests[0].RequestUri!.Host.ShouldBe("api.tcgdex.net");
        inner.Requests[1].RequestUri!.Host.ShouldBe("api.eu2.tcgdex.net");
    }

    [Test]
    public async Task ASupersededResponse_IsDisposed()
    {
        // Status and body are faithful to "which node answered" and completely
        // blind to a leaked connection. Dropping the Dispose on the response
        // being replaced holds a socket per failed attempt — during the outage
        // when sockets are scarcest — with every other assertion still green.
        // Mutation testing found this exact gap in the cache, which is why
        // TrackedResponse exists.
        TrackedResponse superseded = new(HttpStatusCode.BadGateway);

        RecordingHandler inner = new RecordingHandler()
            .RespondWith(_ => superseded)
            .RespondWith(request => From(request, HttpStatusCode.OK));

        using HttpClient client = new(Handler(inner));

        using (await client.GetAsync(Card, CancellationToken.None))
        {
        }

        superseded.WasDisposed.ShouldBeTrue();
    }

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
        // The reason a per-attempt budget exists — and the assertion that
        // distinguishes a PER-ATTEMPT budget from a single one covering the whole
        // request. Rotation alone does not: with one shared budget, attempt one
        // is still cancelled and attempt two still runs, so a test asserting only
        // "the fallback answered" passes against the very defect it names.
        //
        // What separates them is the token attempt two receives. A fresh budget
        // arrives uncancelled; a shared one arrives already cancelled, and would
        // die instantly against a real transport.
        HangsOnceHandler inner = new();

        using HttpClient client = new(
            Handler(inner, attemptTimeout: TimeSpan.FromMilliseconds(100)))
        {
            // Set explicitly so the total budget is a controlled variable. Left
            // alone it is HttpClient's own 100 seconds — which would eventually
            // catch a missing attempt budget, but only after burning 100 seconds
            // per case, the exact shape of the incident in docs/learnings.md.
            Timeout = TimeSpan.FromSeconds(30),
        };

        using HttpResponseMessage response = await client.GetAsync(Card, CancellationToken.None);

        (await response.Content.ReadAsStringAsync()).ShouldContain("api.eu2.tcgdex.net");
        inner.Seen.Count.ShouldBe(2);

        inner.CancelledOnEntry[0].ShouldBeFalse();
        inner.CancelledOnEntry[1].ShouldBeFalse("the retry must get its own budget, not the expired one");
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
            .RespondWith(request => From(request, HttpStatusCode.OK))
            .RespondWith(request => From(request, HttpStatusCode.OK));

        using HttpClient client = new(
            Handler(inner, cooldown: TimeSpan.FromMinutes(5), time: time));

        using (await client.GetAsync(Card, CancellationToken.None))
        {
        }

        // Before advancing, the primary must still be skipped. Without this the
        // test passes even if the cooldown were never recorded at all — it would
        // be pinning only "no cooldown longer than six minutes", and leaning on a
        // neighbouring test to prove one exists.
        using (HttpResponseMessage duringCooldown =
            await client.GetAsync(Card, CancellationToken.None))
        {
            (await duringCooldown.Content.ReadAsStringAsync())
                .ShouldContain("api.eu2.tcgdex.net");
        }

        time.Advance(TimeSpan.FromMinutes(6));

        using (HttpResponseMessage afterCooldown =
            await client.GetAsync(Card, CancellationToken.None))
        {
            (await afterCooldown.Content.ReadAsStringAsync()).ShouldContain("api.tcgdex.net");
        }

        inner.Requests.Count.ShouldBe(4);
        inner.Requests[2].RequestUri!.Host.ShouldBe("api.eu2.tcgdex.net");
        inner.Requests[3].RequestUri!.Host.ShouldBe("api.tcgdex.net");
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

    [Test]
    public async Task ConcurrentRequests_AllGetACorrectAnswer()
    {
        // The cooldown state is a long[] shared by every request on the client,
        // read and written with Interlocked. This asserts the property that
        // actually matters under concurrency: every caller gets a correct answer
        // and nothing throws from a torn read of that array.
        //
        // It deliberately does NOT assert a request count. Threads racing
        // through the window before the primary is marked failed will each spend
        // one attempt on it, and how many do so depends on scheduling. That race
        // is benign and bounded — each of those requests was going to contact the
        // primary anyway, so the worst case equals the traffic that would have
        // been sent with no failover configured at all. Asserting an exact count
        // would be asserting the thread scheduler.
        const int Callers = 50;

        AlwaysHandler inner = new();

        using HttpClient client = new(Handler(inner));

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, Callers)
                .Select(_ => client.GetAsync(Card, CancellationToken.None)));

        try
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                (await response.Content.ReadAsStringAsync()).ShouldContain("api.eu2.tcgdex.net");
            }
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }

        // Every caller was served, and only ever by the fallback.
        inner.Served.Count.ShouldBe(Callers);
    }

    /// <summary>
    /// Fails anything addressed to the primary and serves everything else. Has
    /// no response queue, so it can answer any number of concurrent requests.
    /// </summary>
    private sealed class AlwaysHandler(bool refuseConnection = false) : HttpMessageHandler
    {
        internal System.Collections.Concurrent.ConcurrentBag<Uri> Served { get; } = [];

        /// <summary>Every request seen, in order, primary attempts included.</summary>
        internal List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (Seen)
            {
                Seen.Add(request.RequestUri!);
            }

            if (request.RequestUri!.Host == "api.tcgdex.net")
            {
                // By HOST rather than by attempt number, so the primary keeps
                // failing across several requests while the fallback keeps
                // working — which is what a cooldown test needs.
                return refuseConnection
                    ? Task.FromException<HttpResponseMessage>(
                        new HttpRequestException("refused"))
                    : Task.FromResult(From(request, HttpStatusCode.BadGateway));
            }

            Served.Add(request.RequestUri);
            return Task.FromResult(From(request, HttpStatusCode.OK));
        }
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

        /// <summary>
        /// Whether each attempt arrived with an already-cancelled token — the
        /// only observable difference between a per-attempt budget and one
        /// budget shared by the whole request.
        /// </summary>
        internal List<bool> CancelledOnEntry { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Seen.Add(request.RequestUri!);
            CancelledOnEntry.Add(cancellationToken.IsCancellationRequested);

            if (Seen.Count == 1)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            return From(request, HttpStatusCode.OK);
        }
    }
}

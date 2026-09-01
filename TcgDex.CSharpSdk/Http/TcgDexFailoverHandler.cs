namespace TcgDex;

using System.Net;

/// <summary>
/// Retries a request against the next configured endpoint when the current one
/// cannot serve it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This sits below the response cache on purpose.</b> The cache keys on the
/// request URI, so rewriting the host above it would give the same resource a
/// different key per endpoint and throw away every hit the moment a failover
/// happened. Here the cache only ever sees the canonical address and the swap
/// happens on the wire.
/// </para>
/// <para>
/// <b>GET, and POST to the GraphQL endpoint. Nothing else.</b> Resending is only
/// safe for a request that changes nothing, and rather than assume that of any
/// request with a body, this replays exactly the set the SDK authored itself:
/// TCGdex's GraphQL schema exposes queries and no mutations, and
/// <c>GraphQlTransport</c> is what built the body. A POST to any other address
/// passes straight through to a single endpoint — the SDK will not decide on a
/// caller's behalf that their request is safe to repeat.
/// </para>
/// <para>
/// Rotation is deliberately narrow. A node that refuses a connection, hangs past
/// the per-attempt timeout, or answers with a gateway error has failed to serve
/// the request, and another node plausibly can. Everything else is an answer:
/// <c>404</c> means the card does not exist and would otherwise send every
/// missing card to every node in the list, and <c>429</c> means slow down —
/// spreading that across endpoints is evading a rate limit rather than
/// surviving an outage.
/// </para>
/// </remarks>
internal sealed class TcgDexFailoverHandler : DelegatingHandler
{
    /// <summary>
    /// Endpoints tried for one request, the primary included.
    /// </summary>
    /// <remarks>
    /// Three at the 10-second default attempt timeout fills the 30-second
    /// request budget exactly, so failover divides the caller's ceiling rather
    /// than extending it. Trying every configured endpoint would multiply load
    /// on an API that is already struggling, for a diminishing chance that the
    /// fourth answers when three did not.
    /// </remarks>
    private const int MaxAttempts = 3;

    private readonly Uri _primary;
    private readonly Uri _graphQlEndpoint;
    private readonly IReadOnlyList<Uri> _endpoints;
    private readonly TimeSpan _attemptTimeout;
    private readonly TimeSpan _cooldown;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Which endpoints are cooling off. Supplied rather than owned, so it
    /// survives the handler being rebuilt — see <see cref="FailoverCooldowns"/>.
    /// </summary>
    private readonly FailoverCooldowns _cooldowns;

    internal TcgDexFailoverHandler(
        Uri primary,
        Uri graphQlEndpoint,
        IReadOnlyList<Uri> endpoints,
        TimeSpan attemptTimeout,
        TimeSpan cooldown,
        FailoverCooldowns cooldowns,
        TimeProvider? timeProvider = null)
    {
        Guard.NotNull(primary);
        Guard.NotNull(graphQlEndpoint);
        Guard.NotNull(endpoints);
        Guard.NotNull(cooldowns);

        // The primary is candidate 0, so listing it again as a fallback would
        // send a failed request straight back to the node that just failed.
        // Writing `UseMirror(Eu2).UseFailover()` is the natural way to hit this,
        // since the two are documented side by side.
        _endpoints = [.. endpoints.Where(endpoint => endpoint != primary)];

        if (cooldowns.Count != _endpoints.Count + 1)
        {
            throw new ArgumentException(
                $"Cooldown state tracks {cooldowns.Count} endpoints but " +
                $"{_endpoints.Count + 1} are configured.",
                nameof(cooldowns));
        }

        _primary = primary;
        _graphQlEndpoint = graphQlEndpoint;
        _attemptTimeout = attemptTimeout;
        _cooldown = cooldown;
        _cooldowns = cooldowns;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The endpoints this handler will actually use, after removing any that
    /// duplicate the primary.
    /// </summary>
    internal static IReadOnlyList<Uri> Deduplicate(IReadOnlyList<Uri> endpoints, Uri primary)
        => [.. endpoints.Where(endpoint => endpoint != primary)];

    /// <summary>The endpoint at a candidate index; 0 is the primary.</summary>
    private Uri Candidate(int index) => index == 0 ? _primary : _endpoints[index - 1];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(request);

        // Anything this handler cannot safely replay goes straight through, so
        // the pipeline behaves exactly as it would without failover configured.
        if (request.RequestUri is null
            || !IsReplayable(request)
            || !_primary.IsBaseOf(request.RequestUri))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        Uri requested = request.RequestUri;

        // Read once, before anything is sent. A retry needs the body again, and
        // the content of a request that has already been dispatched cannot be
        // relied on to still be readable.
        byte[]? body = request.Content is null
            ? null
            : await request.Content
                .ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);

        List<int> order = CandidateOrder();

        HttpResponseMessage? lastResponse = null;
        Exception? lastError = null;

        // The loop is wrapped so a gateway response already in hand is released
        // when something escapes rather than being returned. Caller cancellation
        // and the outer request budget both leave through here — they are
        // deliberately NOT caught below, because neither is an endpoint's fault —
        // and an undisposed 502 holds its connection out of the pool until
        // finalisation. That is invisible to every assertion about status codes,
        // and it happens during an outage, when connections are scarcest.
        try
        {
        for (int position = 0; position < order.Count; position++)
        {
            int candidate = order[position];

            // Keyed on the endpoint, not on the position: after a cooldown has
            // ruled the primary out, the FIRST attempt of a request already
            // targets another endpoint and still needs rewriting. Keying this on
            // "first attempt" instead sent those straight back to the endpoint
            // that was being skipped, while every assertion about failover
            // within a single call still passed.
            //
            // Candidate 0 is only ever reached at position 0, so reusing the
            // caller's request there cannot resend an already-sent message. The
            // caller owns that one; only a copy made here is disposed here.
            //
            // A copy now carries ByteArrayContent on the GraphQL path, so
            // disposing it does dispose that content — safe because the request
            // body has already been written by the time a response is returned,
            // and the response reads from the connection rather than from it.
            HttpRequestMessage? copy = candidate == 0
                ? null
                : Clone(request, Rewrite(requested, Candidate(candidate)), body);

            using CancellationTokenSource? budget = CreateAttemptBudget(cancellationToken);
            CancellationToken deadline = budget?.Token ?? cancellationToken;

            try
            {
                HttpResponseMessage response =
                    await base.SendAsync(copy ?? request, deadline).ConfigureAwait(false);

                if (!IsNodeFailure(response.StatusCode))
                {
                    lastResponse?.Dispose();
                    return response;
                }

                MarkFailed(candidate);
                lastResponse?.Dispose();
                lastResponse = response;
            }
            catch (HttpRequestException ex)
            {
                // Refused, unresolvable, or the connection dropped: the node is
                // not serving, which is precisely what another one might.
                MarkFailed(candidate);
                lastError = ex;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The per-attempt budget expired rather than the caller giving
                // up. Cancellation the caller requested is theirs to observe and
                // falls through this filter untouched — retrying it would ignore
                // the instruction to stop.
                MarkFailed(candidate);
                lastError = ex;
            }
            finally
            {
                copy?.Dispose();
            }
        }

        }
        catch
        {
            lastResponse?.Dispose();
            throw;
        }

        if (lastResponse is not null)
        {
            return lastResponse;
        }

        throw lastError ?? new HttpRequestException(
            "Every configured TCGdex endpoint failed to serve the request.");
    }

    /// <summary>
    /// The candidate indices to try, in order, skipping those still cooling off.
    /// </summary>
    /// <remarks>
    /// If every endpoint is cooling off the primary is tried anyway: refusing to
    /// send at all would turn a transient outage into a hard failure that
    /// outlives it, and something has to discover that the service is back.
    /// </remarks>
    private List<int> CandidateOrder()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<int> order = [];

        for (int i = 0; i <= _endpoints.Count && order.Count < MaxAttempts; i++)
        {
            if (_cooldowns.IsAvailable(i, now))
            {
                order.Add(i);
            }
        }

        if (order.Count == 0)
        {
            order.Add(0);
        }

        return order;
    }

    private void MarkFailed(int candidate)
        => _cooldowns.MarkFailed(candidate, _timeProvider.GetUtcNow(), _cooldown);

    private CancellationTokenSource? CreateAttemptBudget(CancellationToken cancellationToken)
    {
        if (_attemptTimeout == Timeout.InfiniteTimeSpan)
        {
            return null;
        }

        CancellationTokenSource source =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        source.CancelAfter(_attemptTimeout);
        return source;
    }

    /// <summary>
    /// The same resource on another endpoint, preserving path and query.
    /// </summary>
    private Uri Rewrite(Uri requested, Uri endpoint)
        => new(endpoint, _primary.MakeRelativeUri(requested));

    /// <summary>
    /// Whether this request is one the SDK may send a second time.
    /// </summary>
    /// <remarks>
    /// A GET changes nothing by definition. The GraphQL endpoint is admitted by
    /// address rather than by method because the safety comes from knowing what
    /// is in the body: TCGdex's schema has queries and no mutations, and the
    /// body was built by this SDK. A POST anywhere else is a request the SDK did
    /// not author and cannot vouch for.
    /// </remarks>
    private bool IsReplayable(HttpRequestMessage request)
        => request.Method == HttpMethod.Get
            || (request.Method == HttpMethod.Post && request.RequestUri == _graphQlEndpoint);

    /// <summary>
    /// A resendable copy, carrying the body when there is one.
    /// </summary>
    private static HttpRequestMessage Clone(HttpRequestMessage source, Uri uri, byte[]? body)
    {
        HttpRequestMessage clone = new(source.Method, uri) { Version = source.Version };

        if (body is not null)
        {
            // Rebuilt from the bytes read up front rather than by reusing the
            // original HttpContent, which a completed send may have disposed.
            ByteArrayContent content = new(body);

            foreach (KeyValuePair<string, IEnumerable<string>> header in source.Content!.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = content;
        }

        // Conditional headers matter here — the cache above this handler may have
        // attached an If-None-Match, and dropping it would turn a revalidation
        // into a full fetch. So headers are carried across…
        //
        // …except the ones that authenticate the caller. A consumer may pass in
        // an HttpClient they share with the rest of their application, which this
        // SDK explicitly supports, and HttpClient.DefaultRequestHeaders are merged
        // into every request before the handler chain runs. Copying the lot would
        // send that client's Authorization or Cookie to whatever host is in the
        // failover list — including an unofficial mirror the consumer added.
        //
        // The runtime already strips these when a redirect crosses origins; a
        // failover crosses origins by definition, so it has to do the same rather
        // than hand-roll its way around that protection. TCGdex itself is keyless,
        // so nothing of the SDK's own is at stake — the credential being protected
        // is the consumer's, for some other service entirely.
        foreach (KeyValuePair<string, IEnumerable<string>> header in source.Headers)
        {
            if (IsCredential(header.Key))
            {
                continue;
            }

            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    /// <summary>
    /// Whether a header authenticates the caller and must not follow a request
    /// to a different host.
    /// </summary>
    private static bool IsCredential(string name)
        => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a status means the node could not serve the request, as opposed
    /// to having answered it.
    /// </summary>
    private static bool IsNodeFailure(HttpStatusCode status)
        => status is HttpStatusCode.BadGateway
                  or HttpStatusCode.ServiceUnavailable
                  or HttpStatusCode.GatewayTimeout;
}

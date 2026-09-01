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
/// <b>Only GET is retried.</b> Resending is safe because the API is read-only
/// and a GET carries no body to replay; a request with content — the opt-in
/// GraphQL path — passes straight through to a single endpoint rather than
/// being replayed on an assumption about whether it is safe to repeat.
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
    private readonly IReadOnlyList<Uri> _endpoints;
    private readonly TimeSpan _attemptTimeout;
    private readonly TimeSpan _cooldown;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// When each endpoint may be tried again, in UTC ticks. Index 0 is the
    /// primary; the rest follow <see cref="_endpoints"/>.
    /// </summary>
    private readonly long[] _availableAt;

    internal TcgDexFailoverHandler(
        Uri primary,
        IReadOnlyList<Uri> endpoints,
        TimeSpan attemptTimeout,
        TimeSpan cooldown,
        TimeProvider? timeProvider = null)
    {
        Guard.NotNull(primary);
        Guard.NotNull(endpoints);

        _primary = primary;
        _endpoints = endpoints;
        _attemptTimeout = attemptTimeout;
        _cooldown = cooldown;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _availableAt = new long[endpoints.Count + 1];
    }

    /// <summary>The endpoint at a candidate index; 0 is the primary.</summary>
    private Uri Candidate(int index) => index == 0 ? _primary : _endpoints[index - 1];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(request);

        // Anything this handler cannot safely replay goes straight through, so
        // the pipeline behaves exactly as it would without failover configured.
        if (request.Method != HttpMethod.Get
            || request.Content is not null
            || request.RequestUri is null
            || !_primary.IsBaseOf(request.RequestUri))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        Uri requested = request.RequestUri;
        List<int> order = CandidateOrder();

        HttpResponseMessage? lastResponse = null;
        Exception? lastError = null;

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
            // caller owns that one; only a copy made here is disposed here, and a
            // copy carries no content, so disposing it cannot disturb the
            // response being returned.
            HttpRequestMessage? copy = candidate == 0
                ? null
                : Clone(request, Rewrite(requested, Candidate(candidate)));

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
        long now = _timeProvider.GetUtcNow().UtcTicks;
        List<int> order = [];

        for (int i = 0; i <= _endpoints.Count && order.Count < MaxAttempts; i++)
        {
            if (Interlocked.Read(ref _availableAt[i]) <= now)
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
    {
        if (_cooldown <= TimeSpan.Zero)
        {
            return;
        }

        Interlocked.Exchange(
            ref _availableAt[candidate],
            _timeProvider.GetUtcNow().Add(_cooldown).UtcTicks);
    }

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
    /// A resendable copy. No content is carried: this handler only retries GET.
    /// </summary>
    private static HttpRequestMessage Clone(HttpRequestMessage source, Uri uri)
    {
        HttpRequestMessage clone = new(source.Method, uri) { Version = source.Version };

        // Conditional headers matter here — the cache above this handler may have
        // attached an If-None-Match, and dropping it would turn a revalidation
        // into a full fetch.
        foreach (KeyValuePair<string, IEnumerable<string>> header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    /// <summary>
    /// Whether a status means the node could not serve the request, as opposed
    /// to having answered it.
    /// </summary>
    private static bool IsNodeFailure(HttpStatusCode status)
        => status is HttpStatusCode.BadGateway
                  or HttpStatusCode.ServiceUnavailable
                  or HttpStatusCode.GatewayTimeout;
}

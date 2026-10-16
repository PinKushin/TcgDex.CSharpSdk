namespace TcgDex;

using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TcgDex.Caching;
using TcgDex.Diagnostics;
using TcgDex.Models;
using TcgDex.Serialization;

/// <summary>
/// Issues requests against the REST API and turns responses into models or a
/// single, predictable exception type.
/// </summary>
/// <remarks>
/// <para>
/// One error contract applies everywhere: a genuinely missing resource yields
/// <see langword="null"/>, and anything else throws
/// <see cref="TcgDexApiException"/>. Splitting this — returning null from
/// single-item getters while list methods propagate a raw
/// <see cref="HttpRequestException"/> — would make identical failures surface
/// differently depending on which method the caller happened to use.
/// </para>
/// <para>
/// All deserialization goes through <see cref="TcgDexJsonContext"/>, so the
/// SDK stays free of reflection and remains AOT- and trim-safe.
/// </para>
/// </remarks>
internal sealed class TcgDexTransport
{
    private readonly HttpClient _httpClient;
    private readonly Uri _languageBase;
    private readonly ILogger _logger;

    /// <summary>Ceiling on a buffered response body. See <see cref="TcgDexOptions.MaxResponseBytes"/>.</summary>
    private readonly long _maxResponseBytes;

    /// <summary>
    /// The deserialization contract, resolved once. See
    /// <see cref="TcgDexOptions.DeserializePricing"/>.
    /// </summary>
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>Ceiling on one request. See <see cref="TcgDexOptions.Timeout"/>.</summary>
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Retained parses, or <see langword="null"/> when disabled. See
    /// <see cref="TcgDexOptions.MaxDeserializedCacheEntries"/>.
    /// </summary>
    private readonly DeserializedResponseCache? _deserialized;

    internal TcgDexTransport(HttpClient httpClient, TcgDexOptions options, ILogger? logger = null)
    {
        Guard.NotNull(httpClient);
        Guard.NotNull(options);

        options.Validate();

        // NullLogger rather than a nullable field: the source-generated log
        // methods short-circuit on IsEnabled, so this costs a branch and keeps
        // every call site free of null checks.
        _logger = logger ?? NullLogger.Instance;
        _httpClient = httpClient;

        // The trailing slash is what makes the resource path append to the
        // language segment rather than replace it.
        _languageBase = new Uri(options.BaseAddress, options.Language + "/");
        _maxResponseBytes = options.MaxResponseBytes;
        _timeout = options.Timeout;
        _jsonOptions = TcgDexJsonContracts.For(options);

        _deserialized = options.MaxDeserializedCacheEntries > 0
            ? new DeserializedResponseCache(options.MaxDeserializedCacheEntries)
            : null;

        // Both endpoints are checked here rather than in Validate(): this is
        // advice, not a rule, and Validate has no logger to give it through.
        // Checked once at construction rather than per request, so a misconfigured
        // client says so immediately instead of once per call.
        WarnIfInsecure(options.BaseAddress);
        WarnIfInsecure(options.GraphQlEndpoint);
    }

    private void WarnIfInsecure(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            _logger.InsecureEndpoint(uri);
        }
    }

    /// <summary>
    /// Fetches and deserializes a resource, returning <see langword="null"/>
    /// when it does not exist.
    /// </summary>
    /// <typeparam name="T">The model to deserialize into.</typeparam>
    /// <param name="relativePath">
    /// Path below the language segment, query string included — for example
    /// <c>cards/swsh3-136</c> or <c>cards?name=eq:Furret</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The deserialized resource, or <see langword="null"/> if absent.</returns>
    /// <exception cref="TcgDexApiException">
    /// The request failed for any reason other than the resource being missing.
    /// </exception>
    internal async Task<T?> GetAsync<T>(string relativePath, CancellationToken cancellationToken)
        where T : class
    {
        Guard.NotNull(relativePath);

        Uri uri = new(_languageBase, relativePath);

        using Activity? activity = TcgDexActivity.Start($"TCGdex {typeof(T).Name}");
        activity?.AddTag("url.full", uri.ToString());
        activity?.AddTag("http.request.method", "GET");

        long timestamp = Stopwatch.GetTimestamp();
        _logger.SendingRequest("GET", uri);

        // The budget covers the body as well as the headers. Responses are read
        // with ResponseHeadersRead, so a server that answers and then stops
        // sending would otherwise sit until HttpClient.Timeout — the 100-second
        // default this option exists to replace.
        using CancellationTokenSource? budget = CreateBudget(cancellationToken);
        CancellationToken deadline = budget?.Token ?? cancellationToken;

        // One translation for every await under the budget — the send, the
        // problem-document read and the success body alike. Three copies of
        // this catch existed before, and the body read was found missing one
        // by a test; a single catch cannot be forgotten by the next await.
        try
        {
            return await GetUnderBudgetAsync<T>(uri, activity, timestamp, deadline).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
            when (!cancellationToken.IsCancellationRequested || ex.CancellationToken != cancellationToken)
        {
            // Three owners, three outcomes. The caller's cancellation stays
            // theirs, re-issued on their token so `when (ex.CancellationToken ==
            // mine)` matches; only the SDK's budget firing is "timed out"; any
            // other canceller (HttpClient.Timeout, a handler) must not name a
            // limit that was never reached.
            if (cancellationToken.IsCancellationRequested)
            {
                throw RequestBudget.CallerCancelled(ex, cancellationToken);
            }

            if (budget?.IsCancellationRequested == true)
            {
                throw TimedOut(uri, activity, ex);
            }

            _logger.RequestErrored(ex, uri);
            TcgDexActivity.RecordFailure(activity, ex);
            throw RequestBudget.PipelineCancelled(uri, ex);
        }
    }

    /// <summary>The body of <see cref="GetAsync{T}"/> that runs under the budget.</summary>
    private async Task<T?> GetUnderBudgetAsync<T>(Uri uri, Activity? activity, long timestamp, CancellationToken deadline)
        where T : class
    {
        using HttpResponseMessage response = await SendAsync(uri, activity, deadline).ConfigureAwait(false);

        long elapsed = (long)ElapsedSince(timestamp).TotalMilliseconds;
        activity?.AddTag("http.response.status_code", (int)response.StatusCode);
        _logger.RequestCompleted(uri, (int)response.StatusCode, elapsed);

        if (!response.IsSuccessStatusCode)
        {
            return await HandleFailureAsync<T>(uri, response, activity, deadline).ConfigureAwait(false);
        }

        // Checked before the body is read, not just before it is parsed: on a
        // hit there is nothing to read either.
        string? etag = response.Headers.ETag?.ToString();

        if (_deserialized is not null && _deserialized.TryGet<T>(uri, etag, out T? cached))
        {
            _logger.ReusedDeserializedResponse(uri, typeof(T).Name);

            return cached;
        }

        ArraySegment<byte> body;

        try
        {
            body = await BoundedContent
                .ReadAsBytesAsync(response.Content, _maxResponseBytes, uri, deadline)
                .ConfigureAwait(false);
        }
        catch (TcgDexApiException ex)
        {
            // Reset or oversized: reported like the send and status failures.
            Errored(uri, activity, ex);
            throw;
        }

        T? value = Deserialize<T>(body, uri, activity);

        // Report a card the API sent malformed, once, here on the deserialize
        // path rather than on the cache-hit path above, so a warm hit does not
        // re-log what a caller has already been told.
        if (value is Card card)
        {
            CardDiagnostics.WarnOnAnomalies(_logger, card);
        }

        if (value is not null)
        {
            _deserialized?.Set(uri, etag, value);
        }

        return value;
    }

    /// <summary>
    /// Fetches a resource that must exist, such as one of the enumeration
    /// endpoints.
    /// </summary>
    /// <typeparam name="T">The model to deserialize into.</typeparam>
    /// <param name="relativePath">Path below the language segment.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The deserialized resource.</returns>
    /// <exception cref="TcgDexApiException">
    /// The request failed, or the resource was absent when the endpoint is
    /// expected always to answer.
    /// </exception>
    internal async Task<T> GetRequiredAsync<T>(string relativePath, CancellationToken cancellationToken)
        where T : class
    {
        T? result = await GetAsync<T>(relativePath, cancellationToken).ConfigureAwait(false);

        return result ?? throw new TcgDexApiException(
            $"The TCGdex API returned no content for '{relativePath}', which is " +
            "expected always to be available.");
    }

    /// <summary>
    /// Reports an expiry the SDK imposed, identically wherever it happened.
    /// </summary>
    /// <param name="uri">The resource being fetched.</param>
    /// <param name="activity">The span to record the failure on, if any.</param>
    /// <param name="ex">The cancellation that ended the request.</param>
    /// <returns>The exception to throw, so call sites read as <c>throw</c>.</returns>
    /// <remarks>
    /// Shared because the send and the body read are two ways to hit the same
    /// deadline, and a caller reading a log should not be able to tell which one
    /// it was from the wording. Naming the configured value matters more: it is
    /// the number they would change.
    /// </remarks>
    private TcgDexApiException TimedOut(Uri uri, Activity? activity, Exception ex)
    {
        _logger.RequestTimedOut(ex, uri);
        TcgDexActivity.RecordFailure(activity, ex);

        return new TcgDexApiException(
            $"The request to '{uri}' timed out after {_timeout}. Raise " +
            $"{nameof(TcgDexOptions)}.{nameof(TcgDexOptions.Timeout)} if this endpoint " +
            "is legitimately this slow.",
            ex);
    }

    /// <summary>
    /// A source that expires after <see cref="TcgDexOptions.Timeout"/>, or
    /// <see langword="null"/> when the limit is infinite.
    /// </summary>
    /// <param name="cancellationToken">The caller's token, linked into it.</param>
    /// <returns>The budget, which the caller disposes.</returns>
    /// <remarks>
    /// Returning null rather than an un-cancelled source for the infinite case
    /// keeps that path free of a timer and an allocation, and makes "no limit"
    /// visible at the use site rather than hidden inside a sentinel.
    /// </remarks>
    private CancellationTokenSource? CreateBudget(CancellationToken cancellationToken)
        => RequestBudget.Create(_timeout, cancellationToken);

    /// <summary>Sends the request under the deadline.</summary>
    /// <param name="uri">The resource being fetched.</param>
    /// <param name="activity">The span to record a failure on, if any.</param>
    /// <param name="deadline">
    /// The effective token: the caller's, plus this SDK's timeout. Telling the
    /// two apart is <see cref="GetAsync{T}"/>'s job, which holds the caller's
    /// token alone.
    /// </param>
    /// <returns>The response, for the caller to dispose.</returns>
    private async Task<HttpResponseMessage> SendAsync(Uri uri, Activity? activity, CancellationToken deadline)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            TcgDexUserAgent.Apply(request);

            return await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            Errored(uri, activity, ex);

            throw new TcgDexApiException($"The request to '{uri}' could not be completed.", ex);
        }
        catch (TcgDexApiException ex)
        {
            // Thrown by a handler in the pipeline, such as the caching handler
            // failing to read a body it was storing.
            Errored(uri, activity, ex);
            throw;
        }
    }

    /// <summary>Logs and records a failure that has no error status to report.</summary>
    private void Errored(Uri uri, Activity? activity, Exception ex)
    {
        _logger.RequestErrored(ex, uri);
        TcgDexActivity.RecordFailure(activity, ex);
    }

    /// <summary>
    /// Decides whether a non-success response is an absent resource or a fault.
    /// </summary>
    /// <remarks>
    /// The API answers both "no such card" and "no such language" with
    /// <c>404</c>, so the status code alone is not enough — the problem
    /// document's <c>type</c> is the discriminator. Treating a language typo as
    /// "not found" would hide a caller mistake behind an empty result.
    /// </remarks>
    private async Task<T?> HandleFailureAsync<T>(
        Uri uri,
        HttpResponseMessage response,
        Activity? activity,
        CancellationToken cancellationToken)
        where T : class
    {
        TcgDexProblem? problem;

        try
        {
            problem = await ReadProblemAsync(response, _maxResponseBytes, uri, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TcgDexApiException ex)
        {
            // Only a 404 lets this through (see ReadProblemAsync). The status is
            // still the news; the read failure rides along as the cause.
            throw Failed(uri, response, activity, "the response body could not be read", problem: null, ex);
        }

        if (response.StatusCode == HttpStatusCode.NotFound && problem?.IsLanguageError != true)
        {
            // Debug, not Warning: a missing resource is an ordinary result.
            _logger.ResourceNotFound(uri);
            return null;
        }

        string description = problem?.Describe() ?? response.ReasonPhrase ?? "no detail supplied";

        throw Failed(uri, response, activity, description, problem, innerException: null);
    }

    /// <summary>Logs, records and builds the exception for an error status.</summary>
    private TcgDexApiException Failed(
        Uri uri,
        HttpResponseMessage response,
        Activity? activity,
        string description,
        TcgDexProblem? problem,
        Exception? innerException)
    {
        _logger.RequestFailed(uri, (int)response.StatusCode, description);

        TcgDexApiException exception = new(
            $"The TCGdex API returned {(int)response.StatusCode} for '{uri}': {description}",
            response.StatusCode,
            problem,
            innerException);

        TcgDexActivity.RecordFailure(activity, exception);

        return exception;
    }

    /// <summary>
    /// Time since a <see cref="Stopwatch.GetTimestamp"/> reading.
    /// </summary>
    /// <remarks>
    /// <c>Stopwatch.GetElapsedTime(long)</c> is .NET 7+. This is what it
    /// does: raw ticks scaled by the platform's timer frequency. Measuring from
    /// a timestamp rather than allocating a <see cref="Stopwatch"/> keeps the
    /// request path allocation-free.
    /// </remarks>
    private static TimeSpan ElapsedSince(long timestamp)
        => TimeSpan.FromTicks(
            (long)((Stopwatch.GetTimestamp() - timestamp) * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)));

    private static async Task<TcgDexProblem?> ReadProblemAsync(
        HttpResponseMessage response,
        long maxResponseBytes,
        Uri uri,
        CancellationToken cancellationToken)
    {
        try
        {
            ArraySegment<byte> body = await BoundedContent
                .ReadAsBytesAsync(response.Content, maxResponseBytes, uri, cancellationToken)
                .ConfigureAwait(false);

            // An empty or all-whitespace body carries no problem document. The
            // span is trimmed rather than decoded to a string first — the whole
            // point of reading bytes is to not pay for that conversion.
            ReadOnlySpan<byte> span = new(body.Array, body.Offset, body.Count);

            return IsBlank(span)
                ? null
                : JsonSerializer.Deserialize(span, TcgDexJsonContext.Default.TcgDexProblem);
        }
        catch (JsonException)
        {
            // An unparseable error body must not mask the underlying failure —
            // the caller still gets a TcgDexApiException, just without detail.
            return null;
        }
        // Not on a 404: that status means "missing" only once the body shows it
        // is not a language error, and an unreadable body shows nothing.
        catch (TcgDexApiException) when (response.StatusCode != HttpStatusCode.NotFound)
        {
            // An oversized or reset error body is bounded like any other, but here
            // the status code is the real news. Swallowing this keeps the caller's
            // exception describing the 500 they got rather than the page the
            // server sent to explain it.
            return null;
        }
    }

    /// <summary>Whether a body is empty or entirely ASCII whitespace.</summary>
    private static bool IsBlank(ReadOnlySpan<byte> body)
    {
        foreach (byte b in body)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Deserializes a UTF-8 body without decoding it to a string first.
    /// </summary>
    /// <remarks>
    /// System.Text.Json reads UTF-8 natively, so passing the buffer straight
    /// through avoids a full copy of every response body.
    /// </remarks>
    private T? Deserialize<T>(ArraySegment<byte> body, Uri uri, Activity? activity)
        where T : class
    {
        try
        {
            JsonTypeInfo<T> typeInfo = (JsonTypeInfo<T>)_jsonOptions.GetTypeInfo(typeof(T));

            return JsonSerializer.Deserialize(
                new ReadOnlySpan<byte>(body.Array, body.Offset, body.Count),
                typeInfo);
        }
        catch (JsonException ex)
        {
            _logger.DeserializationFailed(ex, uri, typeof(T).Name);
            TcgDexActivity.RecordFailure(activity, ex);

            // Callers should need to catch only one exception type, so a
            // malformed body (an HTML error page from a proxy, say) is reported
            // as an API failure rather than leaking JsonException.
            throw new TcgDexApiException(
                $"The response from '{uri}' was not valid JSON for {typeof(T).Name}.",
                HttpStatusCode.OK,
                problem: null,
                innerException: ex);
        }
    }
}

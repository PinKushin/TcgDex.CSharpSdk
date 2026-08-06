namespace TcgDex;

using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    internal TcgDexTransport(HttpClient httpClient, TcgDexOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        // NullLogger rather than a nullable field: the source-generated log
        // methods short-circuit on IsEnabled, so this costs a branch and keeps
        // every call site free of null checks.
        _logger = logger ?? NullLogger.Instance;
        _httpClient = httpClient;

        // The trailing slash is what makes the resource path append to the
        // language segment rather than replace it.
        _languageBase = new Uri(options.BaseAddress, options.Language + "/");
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
        ArgumentNullException.ThrowIfNull(relativePath);

        var uri = new Uri(_languageBase, relativePath);

        using var activity = TcgDexActivity.Start($"TCGdex {typeof(T).Name}");
        activity?.AddTag("url.full", uri.ToString());
        activity?.AddTag("http.request.method", "GET");

        var timestamp = Stopwatch.GetTimestamp();
        _logger.SendingRequest("GET", uri);

        using var response = await SendAsync(uri, activity, cancellationToken).ConfigureAwait(false);

        var elapsed = (long)Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
        activity?.AddTag("http.response.status_code", (int)response.StatusCode);
        _logger.RequestCompleted(uri, (int)response.StatusCode, elapsed);

        if (!response.IsSuccessStatusCode)
        {
            return await HandleFailureAsync<T>(uri, response, activity, cancellationToken).ConfigureAwait(false);
        }

        var body = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        return Deserialize<T>(body, uri);
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
        var result = await GetAsync<T>(relativePath, cancellationToken).ConfigureAwait(false);

        return result ?? throw new TcgDexApiException(
            $"The TCGdex API returned no content for '{relativePath}', which is " +
            "expected always to be available.");
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri uri,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            return await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.RequestErrored(ex, uri);
            TcgDexActivity.RecordFailure(activity, ex);

            throw new TcgDexApiException($"The request to '{uri}' could not be completed.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Cancellation requested by the caller is theirs to observe; this
            // branch is specifically a client-side timeout, which is a fault.
            _logger.RequestTimedOut(ex, uri);
            TcgDexActivity.RecordFailure(activity, ex);

            throw new TcgDexApiException($"The request to '{uri}' timed out.", ex);
        }
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
        var problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound && problem?.IsLanguageError != true)
        {
            // Debug, not Warning: a missing resource is an ordinary result.
            _logger.ResourceNotFound(uri);
            return null;
        }

        var description = problem?.Describe() ?? response.ReasonPhrase ?? "no detail supplied";

        _logger.RequestFailed(uri, (int)response.StatusCode, description);

        var exception = new TcgDexApiException(
            $"The TCGdex API returned {(int)response.StatusCode} for '{uri}': {description}",
            response.StatusCode,
            problem);

        TcgDexActivity.RecordFailure(activity, exception);

        throw exception;
    }

    private static async Task<TcgDexProblem?> ReadProblemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(body)
                ? null
                : JsonSerializer.Deserialize(body, TcgDexJsonContext.Default.TcgDexProblem);
        }
        catch (JsonException)
        {
            // An unparseable error body must not mask the underlying failure —
            // the caller still gets a TcgDexApiException, just without detail.
            return null;
        }
    }

    private T? Deserialize<T>(string body, Uri uri)
        where T : class
    {
        try
        {
            var typeInfo = (JsonTypeInfo<T>)TcgDexJsonContext.Default.Options.GetTypeInfo(typeof(T));
            return JsonSerializer.Deserialize(body, typeInfo);
        }
        catch (JsonException ex)
        {
            _logger.DeserializationFailed(ex, uri, typeof(T).Name);

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

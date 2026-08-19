namespace TcgDex.Diagnostics;

using Microsoft.Extensions.Logging;

/// <summary>
/// Every log message the SDK emits.
/// </summary>
/// <remarks>
/// <para>
/// Written with the <c>LoggerMessage</c> source generator rather than
/// <c>logger.LogDebug($"…")</c>. The generator emits a cached delegate and an
/// <c>IsEnabled</c> check per message, so a disabled level costs a branch —
/// no string formatting, no boxing of arguments, no allocation. Interpolated
/// logging pays all three whether or not anyone is listening, which is the usual
/// way a library ends up measurably slower with logging "off".
/// </para>
/// <para>
/// It is also AOT-safe: the delegates are generated at compile time rather than
/// built by reflection.
/// </para>
/// <para>
/// <b>Scope.</b> These are <em>semantic</em> events — what the SDK decided and
/// why. Raw HTTP request/response logging is deliberately absent: when
/// registered through <c>AddTcgDex</c>, <c>IHttpClientFactory</c> already logs
/// every request and its timing under the
/// <c>System.Net.Http.HttpClient</c> category, and duplicating that would double
/// the noise while disagreeing on detail.
/// </para>
/// <para>
/// <b>Event id ranges.</b> 1000 request lifecycle · 1100 caching ·
/// 1200 configuration · 1300 GraphQL · 1400 data quality. Ids are part of the contract and are not
/// renumbered.
/// </para>
/// </remarks>
internal static partial class TcgDexLog
{
    // ----- 1000: request lifecycle -----

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Debug,
        Message = "TCGdex request {Method} {Uri}")]
    internal static partial void SendingRequest(this ILogger logger, string method, Uri uri);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Debug,
        Message = "TCGdex {Uri} returned {StatusCode} in {ElapsedMilliseconds}ms")]
    internal static partial void RequestCompleted(
        this ILogger logger,
        Uri uri,
        int statusCode,
        long elapsedMilliseconds);

    /// <remarks>
    /// Debug, not Warning: a missing card is an ordinary outcome that returns
    /// null, and logging it louder would make normal use look faulty.
    /// </remarks>
    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Debug,
        Message = "TCGdex resource not found at {Uri}")]
    internal static partial void ResourceNotFound(this ILogger logger, Uri uri);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Error,
        Message = "TCGdex request to {Uri} failed with {StatusCode}: {Problem}")]
    internal static partial void RequestFailed(
        this ILogger logger,
        Uri uri,
        int statusCode,
        string problem);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Error,
        Message = "TCGdex request to {Uri} could not be completed")]
    internal static partial void RequestErrored(this ILogger logger, Exception exception, Uri uri);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Warning,
        Message = "TCGdex request to {Uri} timed out")]
    internal static partial void RequestTimedOut(this ILogger logger, Exception exception, Uri uri);

    /// <remarks>
    /// Warning rather than Error: the request itself succeeded, so this points
    /// at the API having changed shape — which is worth noticing but is not the
    /// caller's fault.
    /// </remarks>
    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Warning,
        Message = "TCGdex response from {Uri} could not be deserialized as {TypeName}")]
    internal static partial void DeserializationFailed(
        this ILogger logger,
        Exception exception,
        Uri uri,
        string typeName);

    /// <summary>
    /// A response whose <c>ETag</c> matched a model already parsed, so the parse
    /// was skipped.
    /// </summary>
    /// <remarks>
    /// Trace rather than Debug, and separate from <c>CacheHit</c>: this says the
    /// deserialization was avoided, which is a different saving from avoiding
    /// the request. Both can happen on one call, and someone tuning cache sizes
    /// needs to tell them apart.
    /// </remarks>
    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Trace,
        Message = "TCGdex reused the parsed {TypeName} for {Uri}; the ETag was unchanged")]
    internal static partial void ReusedDeserializedResponse(
        this ILogger logger,
        Uri uri,
        string typeName);

    // ----- 1100: caching -----

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Trace,
        Message = "TCGdex cache hit for {Uri}")]
    internal static partial void CacheHit(this ILogger logger, string uri);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Debug,
        Message = "TCGdex revalidated {Uri} with no body transferred")]
    internal static partial void CacheRevalidated(this ILogger logger, string uri);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Trace,
        Message = "TCGdex cache miss for {Uri}, storing {Bytes} bytes")]
    internal static partial void CacheMiss(this ILogger logger, string uri, int bytes);

    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Trace,
        Message = "TCGdex coalesced a concurrent request for {Uri}")]
    internal static partial void RequestCoalesced(this ILogger logger, string uri);

    [LoggerMessage(
        EventId = 1104,
        Level = LogLevel.Debug,
        Message = "TCGdex evicted cache entry for {Uri} after a failed response")]
    internal static partial void CacheEvicted(this ILogger logger, string uri);

    // ----- 1200: configuration -----

    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Information,
        Message = "TCGdex client configured for language '{Language}' against {BaseAddress}")]
    internal static partial void ClientConfigured(this ILogger logger, string language, Uri baseAddress);

    /// <remarks>
    /// Warning rather than a validation failure. A plaintext base address is
    /// almost always a mistake — it exposes every request and response to
    /// anyone on the path, and this SDK trusts the response body enough to
    /// deserialize it. But it is not always a mistake: pointing at
    /// <c>http://localhost</c> for a stub server is a documented, legitimate
    /// use, and rejecting it outright would break that with no way around it.
    /// So this is loud and ignorable rather than fatal.
    /// </remarks>
    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Warning,
        Message = "TCGdex is configured with a non-HTTPS endpoint ({Uri}). Requests and responses " +
                  "travel in plaintext and can be read or altered in transit; use https unless this " +
                  "is a local test server.")]
    internal static partial void InsecureEndpoint(this ILogger logger, Uri uri);

    // ----- 1300: GraphQL -----

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Debug,
        Message = "TCGdex GraphQL search returned {Count} card(s)")]
    internal static partial void GraphQlSearchCompleted(this ILogger logger, int count);

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Error,
        Message = "TCGdex GraphQL reported errors: {Errors}")]
    internal static partial void GraphQlErrors(this ILogger logger, string errors);

    /// <remarks>
    /// The server nulls an entry it cannot fully resolve. Dropping it silently
    /// would leave a caller wondering why a card is missing.
    /// </remarks>
    [LoggerMessage(
        EventId = 1302,
        Level = LogLevel.Warning,
        Message = "TCGdex GraphQL returned {Count} unresolvable card entr(ies), which were dropped")]
    internal static partial void GraphQlDroppedEntries(this ILogger logger, int count);

    // ----- 1400: data quality -----

    /// <remarks>
    /// The SDK reads what the API sends rather than rejecting it, so a malformed
    /// record deserializes to a card with a hole in it — a nameless attack, for
    /// example, which <c>2017sm-5</c> ships. The hole is honest data; this is how
    /// the SDK says the API, not the caller, produced it, without inventing a
    /// value to paper over it.
    /// </remarks>
    [LoggerMessage(
        EventId = 1400,
        Level = LogLevel.Warning,
        Message = "TCGdex card {CardId} has malformed data: {Detail}")]
    internal static partial void MalformedCardData(this ILogger logger, string cardId, string detail);
}

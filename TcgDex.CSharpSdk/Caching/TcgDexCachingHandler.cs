namespace TcgDex.Caching;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

/// <summary>
/// Serves repeated reads from a cache, and revalidates with the API rather than
/// re-downloading when an entry goes stale.
/// </summary>
/// <remarks>
/// <para>
/// A request takes one of three paths:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Fresh hit</b> — within the freshness window, served from memory with
///     no network at all.
///   </description></item>
///   <item><description>
///     <b>Stale hit</b> — the entry is past its window but has an <c>ETag</c>,
///     so the request goes out with <c>If-None-Match</c>. The API answers
///     <c>304</c> with an empty body, the entry's clock is reset, and the cached
///     body is served. A 22 KB set response costs 0 bytes here.
///   </description></item>
///   <item><description>
///     <b>Miss</b> — a normal request, stored on the way back.
///   </description></item>
/// </list>
/// <para>
/// Only <c>GET</c> is cached, which is the whole API — it is read-only.
/// </para>
/// <para>
/// This sits in the <c>HttpClient</c> pipeline rather than inside the SDK's
/// transport, so it is transparent to every resource client and composes with
/// any other handler a caller adds.
/// </para>
/// </remarks>
public sealed class TcgDexCachingHandler : DelegatingHandler
{
    private readonly ITcgDexResponseCache _cache;
    private readonly TcgDexCacheOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Tracks requests currently in flight so concurrent callers asking for the
    /// same URL share one response instead of each issuing their own.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task<CachedResponse?>> _inFlight =
        new(StringComparer.Ordinal);

    /// <summary>Creates the handler.</summary>
    /// <param name="cache">Where responses are stored.</param>
    /// <param name="options">Freshness policy. Defaults are used when omitted.</param>
    /// <param name="timeProvider">Clock used for freshness; defaults to the system clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cache"/> is null.</exception>
    public TcgDexCachingHandler(
        ITcgDexResponseCache cache,
        TcgDexCacheOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        Guard.NotNull(cache);

        _cache = cache;
        _options = options ?? new TcgDexCacheOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Number of responses served without any network request.</summary>
    /// <remarks>Useful for confirming the cache is doing what you expect.</remarks>
    public long FreshHits => _freshHits;

    /// <summary>Number of entries refreshed by a <c>304</c> instead of a re-download.</summary>
    public long Revalidations => _revalidations;

    /// <summary>Number of requests that went to the API in full.</summary>
    public long Misses => _misses;

    private long _freshHits;
    private long _revalidations;
    private long _misses;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(request);

        if (request.Method != HttpMethod.Get || request.RequestUri is null)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var key = request.RequestUri.AbsoluteUri;
        var timeToLive = _options.GetTimeToLive(request.RequestUri);
        var cached = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);

        if (cached is not null && cached.IsFresh(_timeProvider.GetUtcNow(), timeToLive))
        {
            Interlocked.Increment(ref _freshHits);
            return BuildResponse(request, cached, HttpStatusCode.OK);
        }

        var result = _options.CoalesceConcurrentRequests
            ? await FetchCoalescedAsync(key, request, cached, timeToLive, cancellationToken).ConfigureAwait(false)
            : await FetchAsync(key, request, cached, timeToLive, cancellationToken).ConfigureAwait(false);

        // A non-cacheable response — an error, typically — is handed straight
        // back. Re-sending here would double the load on exactly the requests
        // that are already failing, and would hide the real status code.
        if (result.Passthrough is not null)
        {
            return result.Passthrough;
        }

        if (result.Cached is not null)
        {
            return BuildResponse(request, result.Cached, HttpStatusCode.OK);
        }

        // Only reached by a waiter whose leader produced a non-shareable
        // response: an HttpResponseMessage has a single content stream and
        // cannot be handed to several callers.
        //
        // This goes back through the normal fetch path rather than calling the
        // inner handler directly, so a waiter that succeeds still populates the
        // cache. Sending raw here would mean a leader's failure quietly cost
        // every waiter a cache entry.
        var own = await FetchAsync(key, request, cached, timeToLive, cancellationToken).ConfigureAwait(false);

        return own.Passthrough
            ?? BuildResponse(request, own.Cached!, HttpStatusCode.OK);
    }

    /// <summary>
    /// The outcome of a fetch: either something worth caching, or a response to
    /// return untouched.
    /// </summary>
    private readonly record struct FetchResult(CachedResponse? Cached, HttpResponseMessage? Passthrough)
    {
        internal static FetchResult FromCache(CachedResponse cached) => new(cached, null);

        internal static FetchResult FromResponse(HttpResponseMessage response) => new(null, response);

        internal static FetchResult None => default;
    }

    /// <summary>
    /// Ensures only one caller fetches a given URL at a time; the rest await that
    /// result. Without this a cold cache under load issues one request per
    /// caller for the same resource.
    /// </summary>
    private async Task<FetchResult> FetchCoalescedAsync(
        string key,
        HttpRequestMessage request,
        CachedResponse? cached,
        TimeSpan timeToLive,
        CancellationToken cancellationToken)
    {
        // Only the cacheable part of the outcome is shared. An
        // HttpResponseMessage has a single content stream, so it cannot be
        // handed to several waiters.
        var tcs = new TaskCompletionSource<CachedResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = _inFlight.GetOrAdd(key, tcs.Task);

        if (!ReferenceEquals(pending, tcs.Task))
        {
            // Someone else is already fetching this URL.
            var shared = await pending.ConfigureAwait(false);

            return shared is null ? FetchResult.None : FetchResult.FromCache(shared);
        }

        try
        {
            var result = await FetchAsync(key, request, cached, timeToLive, cancellationToken).ConfigureAwait(false);

            tcs.SetResult(result.Cached);
            return result;
        }
        catch (Exception exception)
        {
            // Faulting the shared task propagates the failure to every waiter
            // rather than leaving them hanging on a task nobody completes.
            tcs.SetException(exception);
            throw;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private async Task<FetchResult> FetchAsync(
        string key,
        HttpRequestMessage request,
        CachedResponse? cached,
        TimeSpan timeToLive,
        CancellationToken cancellationToken)
    {
        // A stale entry with a validator is worth revalidating: an unchanged
        // resource comes back as 304 with no body.
        if (cached?.ETag is { Length: > 0 } etag)
        {
            request.Headers.IfNoneMatch.TryParseAdd(etag);
        }

        // Not disposed with `using`: a non-cacheable response is returned to the
        // caller, who owns it from then on.
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            response.Dispose();
            Interlocked.Increment(ref _revalidations);

            var refreshed = cached with { StoredAt = _timeProvider.GetUtcNow() };
            await _cache.SetAsync(key, refreshed, timeToLive, cancellationToken).ConfigureAwait(false);

            return FetchResult.FromCache(refreshed);
        }

        if (!response.IsSuccessStatusCode)
        {
            // Errors are never cached: a 404 now must not suppress a card that
            // appears later, and a 5xx blip must not become a persistent outage
            // for the caller. The real response goes back untouched.
            await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);

            return FetchResult.FromResponse(response);
        }

        Interlocked.Increment(ref _misses);

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var stored = new CachedResponse
        {
            Body = body,
            ETag = response.Headers.ETag?.ToString(),
            ContentType = response.Content.Headers.ContentType?.ToString(),
            StoredAt = _timeProvider.GetUtcNow(),
        };

        response.Dispose();

        await _cache.SetAsync(key, stored, timeToLive, cancellationToken).ConfigureAwait(false);

        return FetchResult.FromCache(stored);
    }

    /// <summary>
    /// Rebuilds a response from a cached entry. A fresh <see cref="ByteArrayContent"/>
    /// is created each time so concurrent callers never share a stream.
    /// </summary>
    private static HttpResponseMessage BuildResponse(
        HttpRequestMessage request,
        CachedResponse cached,
        HttpStatusCode statusCode)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(cached.Body),
        };

        if (cached.ContentType is { Length: > 0 } contentType
            && MediaTypeHeaderValue.TryParse(contentType, out var parsed))
        {
            response.Content.Headers.ContentType = parsed;
        }

        if (cached.ETag is { Length: > 0 } etag
            && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            response.Headers.ETag = tag;
        }

        return response;
    }
}

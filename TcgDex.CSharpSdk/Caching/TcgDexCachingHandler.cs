namespace TcgDex.Caching;

using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TcgDex.Diagnostics;

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
/// <para>
/// <b>Exceptions.</b> A body larger than the configured limit, or one whose
/// connection fails mid-read, throws <see cref="TcgDexApiException"/> out of
/// this handler rather than <see cref="HttpRequestException"/>. This is
/// deliberate: retry policies such as <c>AddStandardResilienceHandler</c> retry
/// <see cref="HttpRequestException"/>, and re-downloading a body that is too
/// large will only fail the same way again. Callers going through
/// <see cref="TcgDexClient"/> see <see cref="TcgDexApiException"/> either way.
/// </para>
/// </remarks>
public sealed class TcgDexCachingHandler : DelegatingHandler
{
    private readonly ITcgDexResponseCache _cache;
    private readonly TcgDexCacheOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly long _maxResponseBytes;
    private readonly ILogger _logger;

    /// <summary>
    /// The counters and in-flight requests, which outlive this instance under
    /// <c>IHttpClientFactory</c>. See <see cref="TcgDexCachingState"/>.
    /// </summary>
    private readonly TcgDexCachingState _state;

    /// <summary>Creates the handler.</summary>
    /// <param name="cache">Where responses are stored.</param>
    /// <param name="options">Freshness policy. Defaults are used when omitted.</param>
    /// <param name="timeProvider">Clock used for freshness; defaults to the system clock.</param>
    /// <param name="maxResponseBytes">
    /// The largest body this handler will buffer, matching
    /// <see cref="TcgDexOptions.MaxResponseBytes"/>. Zero removes the limit.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="cache"/> is null.</exception>
    /// <remarks>
    /// <paramref name="maxResponseBytes"/> defaults to the same 32 MiB as
    /// <see cref="TcgDexOptions"/> rather than to unbounded, so a handler
    /// constructed by hand is bounded too. Both of the SDK's own construction
    /// paths pass the configured value through.
    /// </remarks>
    public TcgDexCachingHandler(
        ITcgDexResponseCache cache,
        TcgDexCacheOptions? options = null,
        TimeProvider? timeProvider = null,
        long maxResponseBytes = 32L * 1024 * 1024)
        : this(cache, options, timeProvider, maxResponseBytes, new TcgDexCachingState(), logger: null)
    {
    }

    /// <summary>
    /// Creates a handler over state shared with every other handler built for
    /// the same client, which is how <c>AddTcgDexWithCaching</c> and
    /// <see cref="TcgDexClient.Create"/> construct it.
    /// </summary>
    internal TcgDexCachingHandler(
        ITcgDexResponseCache cache,
        TcgDexCacheOptions? options,
        TimeProvider? timeProvider,
        long maxResponseBytes,
        TcgDexCachingState state,
        ILogger? logger)
    {
        Guard.NotNull(cache);

        _cache = cache;
        _options = options ?? new TcgDexCacheOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxResponseBytes = maxResponseBytes;
        _state = state;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Number of responses served without any network request.</summary>
    /// <remarks>
    /// Useful for confirming the cache is doing what you expect. Under
    /// <c>AddTcgDexWithCaching</c> this counts across every handler the factory
    /// has built; inject <see cref="TcgDexCacheStatistics"/> to read it without
    /// a handler.
    /// </remarks>
    public long FreshHits => _state.Statistics.FreshHits;

    /// <summary>Number of entries refreshed by a <c>304</c> instead of a re-download.</summary>
    public long Revalidations => _state.Statistics.Revalidations;

    /// <summary>Number of requests that went to the API in full.</summary>
    public long Misses => _state.Statistics.Misses;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(request);

        // A re-sent message still carries the tag an earlier send added, which
        // may no longer be our ETag. It comes off on every path — a fresh hit
        // or a non-GET would otherwise leave it on the message — and before any
        // check, so it is never mistaken for the caller's.
        RemoveAddedETag(request);

        if (request.Method != HttpMethod.Get || request.RequestUri is null)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        string key = request.RequestUri.AbsoluteUri;
        TimeSpan timeToLive = _options.GetTimeToLive(request.RequestUri);
        CachedResponse? cached = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);

        if (cached is not null && cached.IsFresh(_timeProvider.GetUtcNow(), timeToLive))
        {
            _state.Statistics.RecordFreshHit();
            _logger.CacheHit(key);
            return BuildResponse(request, cached, HttpStatusCode.OK);
        }

        // A caller's own If-None-Match (or `*`) makes the outcome specific to
        // that request, so it neither leads nor joins a shared fetch: whichever
        // validator went out would otherwise decide the answer for both.
        // Contains, not IfNoneMatch.Count, so an unparsable caller value counts.
        FetchResult result = _options.CoalesceConcurrentRequests && !request.Headers.Contains("If-None-Match")
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
        FetchResult own = await FetchAsync(key, request, cached, timeToLive, cancellationToken).ConfigureAwait(false);

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
        TaskCompletionSource<CachedResponse?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CachedResponse?> pending = _state.InFlight.GetOrAdd(key, tcs.Task);

        if (!ReferenceEquals(pending, tcs.Task))
        {
            _logger.RequestCoalesced(key);

            // Someone else is already fetching this URL.
            //
            // Awaited through THIS caller's token, not bare. Waiting on the
            // shared task alone made a waiter's own cancellation and its own
            // Timeout unenforceable: it blocked for as long as the leader's
            // fetch took, which with Timeout.InfiniteTimeSpan is forever.
            CachedResponse? shared = await WaitForLeaderAsync(pending, cancellationToken)
                .ConfigureAwait(false);

            return shared is null ? FetchResult.None : FetchResult.FromCache(shared);
        }

        try
        {
            FetchResult result = await FetchAsync(key, request, cached, timeToLive, cancellationToken).ConfigureAwait(false);

            tcs.SetResult(result.Cached);
            return result;
        }
        catch
        {
            // The leader's failure is NOT propagated to the waiters, and that is
            // deliberate. Faulting the shared task handed every waiter the
            // leader's exception, which unwound through the transport's filter —
            // a filter that tests the WAITER's token. So a caller who had waited
            // five milliseconds and cancelled nothing was told "the request
            // timed out after 00:00:30", because a different caller on another
            // thread had cancelled or expired. One caller's instruction to stop
            // became a service fault reported to strangers.
            //
            // Completing with null instead lets each waiter fall through to its
            // own fetch, where any failure is genuinely its own and its own token
            // decides how it is reported.
            tcs.SetResult(null);
            throw;
        }
        finally
        {
            _state.InFlight.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Waits for the leader's fetch, but no longer than this caller is willing
    /// to wait.
    /// </summary>
    /// <remarks>
    /// Awaiting the shared task directly ties a waiter's fate to a request it
    /// did not make: its own <see cref="TcgDexOptions.Timeout"/> and its own
    /// cancellation both stop applying, because neither is attached to the task
    /// it is blocked on.
    /// </remarks>
    private static async Task<CachedResponse?> WaitForLeaderAsync(
        Task<CachedResponse?> pending,
        CancellationToken cancellationToken)
    {
#if NET6_0_OR_GREATER
        return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
#else
        // Task.WaitAsync is net6.0+. The same guarantee here is a race between
        // the leader's task and one that completes on cancellation — the leader
        // is left running either way, since another waiter may still want it.
        TaskCompletionSource<bool> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using (cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state).TrySetResult(true),
            cancelled))
        {
            if (await Task.WhenAny(pending, cancelled.Task).ConfigureAwait(false) != pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return await pending.ConfigureAwait(false);
#endif
    }

    private const string AddedETagKey = "TcgDex.CachingHandler.AddedETag";

    /// <summary>
    /// Weak comparison (RFC 9110 §13.1.2 / §8.8.3.2), which If-None-Match uses: <c>W/"a"</c>
    /// and <c>"a"</c> match. An absent tag never matches anything, so a 304
    /// without an ETag is not tied to an entry stored without one.
    /// </summary>
    private static bool SameTag(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        // An unparsable tag is not an entity-tag, so it matches nothing — not
        // even an identical string.
        return EntityTagHeaderValue.TryParse(a, out EntityTagHeaderValue? x)
            && EntityTagHeaderValue.TryParse(b, out EntityTagHeaderValue? y)
            && string.Equals(x.Tag, y.Tag, StringComparison.Ordinal);
    }

    /// <summary>Removes, and forgets, the tag an earlier send of this message added.</summary>
    /// <remarks>
    /// Ours is the exact instance we added — the collection keeps the instances
    /// it is given — so a caller's textually identical tag is never taken for
    /// it. <see cref="HttpHeaderValueCollection{T}.Remove"/> matches by
    /// <c>Equals</c> and could take the caller's, so every equal value comes off
    /// and the caller's are put back.
    /// </remarks>
    private static void RemoveAddedETag(HttpRequestMessage request)
    {
#if NET5_0_OR_GREATER
        HttpRequestOptionsKey<EntityTagHeaderValue> key = new(AddedETagKey);
        _ = request.Options.TryGetValue(key, out EntityTagHeaderValue? added);
        ((IDictionary<string, object?>)request.Options).Remove(AddedETagKey);
#else
        _ = request.Properties.TryGetValue(AddedETagKey, out object? value);
        _ = request.Properties.Remove(AddedETagKey);
        EntityTagHeaderValue? added = value as EntityTagHeaderValue;
#endif
        HttpHeaderValueCollection<EntityTagHeaderValue> tags = request.Headers.IfNoneMatch;
        if (added is null || !tags.Any(t => ReferenceEquals(t, added)))
        {
            return;
        }

        List<EntityTagHeaderValue> callers = [.. tags.Where(t => !ReferenceEquals(t, added) && t.Equals(added))];
        for (int i = 0; i <= callers.Count; i++)
        {
            _ = tags.Remove(added);
        }

        foreach (EntityTagHeaderValue tag in callers)
        {
            tags.Add(tag);
        }
    }

    private static void SetAddedETag(HttpRequestMessage request, EntityTagHeaderValue etag)
    {
#if NET5_0_OR_GREATER
        request.Options.Set(new HttpRequestOptionsKey<EntityTagHeaderValue>(AddedETagKey), etag);
#else
        request.Properties[AddedETagKey] = etag;
#endif
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
        //
        // Added only when absent: a handler above this one may send the same
        // message again, and adding each time sent the validator twice. Any
        // validator the caller set is theirs and stays (owner: "only remove our
        // own value").
        //
        // A caller's `*` is never combined with ours: the grammar is
        // "*" / #entity-tag (RFC 9110 §13.1.2), and a 304 answering `*` says
        // nothing about our copy.
        bool callerWildcard = request.Headers.IfNoneMatch.Any(
            t => string.Equals(t.Tag, "*", StringComparison.Ordinal));
        string? ourETag = cached?.ETag is { Length: > 0 } e ? e : null;
        bool callerValidator = request.Headers.IfNoneMatch.Any(t => !SameTag(t.ToString(), ourETag));

        // The instance itself is remembered, so a later send removes exactly the
        // value that went into the collection and never the caller's twin.
        if (!callerWildcard
            && EntityTagHeaderValue.TryParse(ourETag, out EntityTagHeaderValue? ours)
            && !request.Headers.IfNoneMatch.Any(t => SameTag(t.ToString(), ourETag)))
        {
            request.Headers.IfNoneMatch.Add(ours);
            SetAddedETag(request, ours);
        }

        // Not disposed with `using`: a non-cacheable response is returned to the
        // caller, who owns it from then on.
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string? renewedETag = response.Headers.ETag?.ToString();

        // A 304 is ours only if it cannot be validating the caller's copy: with
        // no caller validator every 304 answers our ETag, so a new ETag on it
        // renews ours (RFC 9110 §15.4.5); with one, only a 304 naming our cached
        // ETag is ours, and a different or absent ETag goes back untouched.
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null && !callerWildcard
            && (!callerValidator || SameTag(renewedETag, cached.ETag)))
        {
            // Taking a renewed ETag also moves the transport's parsed-model
            // cache, which is keyed by this same ETag, onto the new value.
            response.Dispose();
            _state.Statistics.RecordRevalidation();
            _logger.CacheRevalidated(key);

            CachedResponse refreshed = cached with
            {
                StoredAt = _timeProvider.GetUtcNow(),
                ETag = renewedETag ?? cached.ETag,
            };
            await _cache.SetAsync(key, refreshed, timeToLive, cancellationToken).ConfigureAwait(false);

            return FetchResult.FromCache(refreshed);
        }

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            // The caller's 304: neither served from nor evicted from our entry.
            return FetchResult.FromResponse(response);
        }

        if (!response.IsSuccessStatusCode)
        {
            return await EvictAfterFailureAsync(key, response, cached, cancellationToken).ConfigureAwait(false);
        }

        _state.Statistics.RecordMiss();

        // Ownership passes here. Every path that hands `response` back to the
        // caller has already returned above, so from this point it is ours — and
        // a using declaration releases it on the exception paths too, which the
        // straight-line Dispose() this replaced did not: an oversized body, a
        // dropped connection or a cancelled token used to leave the connection
        // out of the pool until finalisation.
        using HttpResponseMessage owned = response;

        // Bounded, like every other body read in the SDK. This handler sits
        // ABOVE the transport and drains the body itself, so BoundedContent
        // never saw this stream and MaxResponseBytes did not apply here —
        // the ceiling was HttpContent's own 2 GB. Decompression happens in
        // the handler BELOW this one, so with AutomaticDecompression enabled
        // a megabyte of hostile gzip expanded to roughly a gigabyte inside
        // this call, and was then stored, since the cache bounds entries
        // rather than bytes.
        ArraySegment<byte> body = await BoundedContent
            .ReadAsBytesAsync(
                response.Content,
                _maxResponseBytes,
                // Non-null: SendAsync returns early for a null RequestUri
                // before any fetch path is entered, and `key` above is built
                // from this same property.
                request.RequestUri!,
                cancellationToken)
            .ConfigureAwait(false);

        CachedResponse stored = new()
        {
            // Copied: the segment may sit in a larger pooled buffer, and this
            // is retained in the cache long after the read returns.
            Body = body.Count == body.Array!.Length
                ? body.Array
                : [.. body],
            ETag = response.Headers.ETag?.ToString(),
            ContentType = response.Content.Headers.ContentType?.ToString(),
            StoredAt = _timeProvider.GetUtcNow(),
        };

        await _cache.SetAsync(key, stored, timeToLive, cancellationToken).ConfigureAwait(false);
        _logger.CacheMiss(key, stored.Body.Length);

        return FetchResult.FromCache(stored);
    }

    /// <summary>
    /// Drops the entry for a URL that just failed, and hands the failure back.
    /// </summary>
    /// <remarks>
    /// Errors are never cached: a 404 now must not suppress a card that
    /// appears later, and a 5xx blip must not become a persistent outage for
    /// the caller. The real response goes back untouched.
    /// </remarks>
    private async Task<FetchResult> EvictAfterFailureAsync(
        string key,
        HttpResponseMessage response,
        CachedResponse? cached,
        CancellationToken cancellationToken)
    {
        // The response is not the caller's until it is returned, so a custom
        // cache whose eviction throws must not leak it.
        try
        {
            await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        // Logged only when there was an entry to evict; a failure on a URL
        // never cached evicts nothing.
        if (cached is not null)
        {
            _logger.CacheEvicted(key);
        }

        return FetchResult.FromResponse(response);
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
        HttpResponseMessage response = new(statusCode)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(cached.Body),
        };

        if (cached.ContentType is { Length: > 0 } contentType
            && MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? parsed))
        {
            response.Content.Headers.ContentType = parsed;
        }

        if (cached.ETag is { Length: > 0 } etag
            && EntityTagHeaderValue.TryParse(etag, out EntityTagHeaderValue? tag))
        {
            response.Headers.ETag = tag;
        }

        return response;
    }
}

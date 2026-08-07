namespace TcgDex.Tests.Caching;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using TcgDex.Caching;

/// <summary>
/// A controllable clock, so freshness and expiry are tested by advancing time
/// rather than by sleeping.
/// </summary>
/// <remarks>
/// .NET ships <c>Microsoft.Extensions.TimeProvider.Testing</c> for this, but a
/// dozen lines here avoids a test-only package reference for a type this small.
/// </remarks>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    internal void Advance(TimeSpan amount) => _now += amount;
}

/// <summary>
/// The innermost handler: records every request that actually reached the
/// network and replays queued responses.
/// </summary>
/// <remarks>
/// Counting requests is the point — every claim the cache makes is really a
/// claim about how many calls got through.
/// </remarks>
/// <summary>
/// An <see cref="HttpResponseMessage"/> that records whether it was disposed.
/// </summary>
/// <remarks>
/// The caching handler disposes the responses it consumes — the 304 it
/// revalidates with, and the 200 whose body it has already copied into the
/// cache. Both leak a connection if the call goes missing, and a leak is
/// invisible to every assertion about status codes and bodies, which is why
/// mutation testing found those two Dispose() calls unprotected.
/// </remarks>
internal sealed class TrackedResponse : HttpResponseMessage
{
    internal TrackedResponse(HttpStatusCode status)
        : base(status)
    {
    }

    internal bool WasDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>
/// Wraps a cache and records which keys were removed.
/// </summary>
/// <remarks>
/// Eviction on a failed response has no effect observable through the client:
/// the entry being evicted is already stale, so the next request revalidates
/// either way. It still matters — <see cref="ITcgDexResponseCache"/> is a
/// public extension point, and an implementation backed by Redis or disk is
/// entitled to be told the entry is gone rather than keeping it forever.
/// Asserting the interaction is the only way to pin that down.
/// </remarks>
internal sealed class RecordingCache(ITcgDexResponseCache inner) : ITcgDexResponseCache
{
    internal List<string> Removed { get; } = [];

    public ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default)
        => inner.GetAsync(key, cancellationToken);

    public ValueTask SetAsync(
        string key,
        CachedResponse response,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
        => inner.SetAsync(key, response, timeToLive, cancellationToken);

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        Removed.Add(key);
        return inner.RemoveAsync(key, cancellationToken);
    }
}

internal sealed class CountingHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _responses = new();
    // object, not System.Threading.Lock: Lock is .NET 9+, and this project now
    // also builds for net8.0. The lock is uncontended bookkeeping in a test
    // double, so Lock's faster path buys nothing here.
    private readonly object _gate = new();

    /// <summary>Requests that reached this handler, in order.</summary>
    internal List<HttpRequestMessage> Requests { get; } = [];

    internal CountingHandler Respond(HttpStatusCode status, string body, string? etag)
    {
        _responses.Enqueue(_ => Task.FromResult(Build(status, body, etag)));
        return this;
    }

    internal CountingHandler RespondSlowly(
        HttpStatusCode status,
        string body,
        string? etag,
        TimeSpan delay,
        int repeat = 1)
    {
        for (var i = 0; i < repeat; i++)
        {
            _responses.Enqueue(async _ =>
            {
                // A real delay is required here: coalescing can only be observed
                // while a request is genuinely in flight.
                await Task.Delay(delay);
                return Build(status, body, etag);
            });
        }

        return this;
    }

    /// <summary>Queues a response whose disposal the caller can observe.</summary>
    internal CountingHandler RespondTracked(TrackedResponse response, string body, string? etag)
    {
        response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        if (etag is not null)
        {
            response.Headers.TryAddWithoutValidation("ETag", etag);
        }

        _responses.Enqueue(_ => Task.FromResult<HttpResponseMessage>(response));
        return this;
    }

    internal CountingHandler Throw(Exception exception, int repeat = 1)
    {
        for (var i = 0; i < repeat; i++)
        {
            _responses.Enqueue(_ => Task.FromException<HttpResponseMessage>(exception));
        }

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Requests.Add(CloneForInspection(request));
        }

        if (!_responses.TryDequeue(out var responder))
        {
            throw new InvalidOperationException(
                $"The client made {Requests.Count} request(s) but fewer responses were queued. " +
                $"Last: {request.Method} {request.RequestUri}");
        }

        return await responder(request);
    }

    /// <summary>
    /// Captures the request as it was sent. The live message is disposed once the
    /// call completes, so assertions afterwards need a copy.
    /// </summary>
    private static HttpRequestMessage CloneForInspection(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private static HttpResponseMessage Build(HttpStatusCode status, string body, string? etag)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

        if (etag is { Length: > 0 } && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            response.Headers.ETag = tag;
        }

        return response;
    }
}

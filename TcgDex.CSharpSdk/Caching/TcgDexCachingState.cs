namespace TcgDex.Caching;

using System.Collections.Concurrent;

/// <summary>
/// What a <see cref="TcgDexCachingHandler"/> keeps that must outlive it.
/// </summary>
/// <remarks>
/// <c>IHttpClientFactory</c> rebuilds the handler chain every handler lifetime
/// (two minutes by default). Held in handler fields, the counters reset at each
/// rebuild, and the in-flight table did too — so a request on the old chain and
/// the same request on the new one were both sent, which is the duplicate
/// coalescing exists to prevent. <c>AddTcgDexWithCaching</c> registers one of
/// these as a singleton and hands it to every handler it builds.
/// </remarks>
internal sealed class TcgDexCachingState
{
    /// <summary>The counters, also registered on their own for injection.</summary>
    internal TcgDexCacheStatistics Statistics { get; } = new();

    /// <summary>
    /// Requests currently in flight, so concurrent callers asking for the same
    /// URL share one response instead of each issuing their own.
    /// </summary>
    internal ConcurrentDictionary<string, Task<CachedResponse?>> InFlight { get; } = new(StringComparer.Ordinal);
}

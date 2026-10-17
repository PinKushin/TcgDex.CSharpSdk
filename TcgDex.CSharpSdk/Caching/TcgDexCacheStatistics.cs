namespace TcgDex.Caching;

/// <summary>
/// How the response cache has been answering requests.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddTcgDexWithCaching</c> registers one instance as a singleton, so inject
/// it to watch the cache. It belongs to the container rather than to a handler
/// because <c>IHttpClientFactory</c> rebuilds handlers every two minutes; counts
/// kept on the handler started again from zero at every rebuild.
/// </para>
/// <para>
/// Counts are read and written atomically, so a 32-bit process never sees a
/// torn value.
/// </para>
/// </remarks>
public sealed class TcgDexCacheStatistics
{
    private long _freshHits;
    private long _revalidations;
    private long _misses;

    internal TcgDexCacheStatistics()
    {
    }

    /// <summary>Number of responses served without any network request.</summary>
    public long FreshHits => Interlocked.Read(ref _freshHits);

    /// <summary>Number of entries refreshed by a <c>304</c> instead of a re-download.</summary>
    public long Revalidations => Interlocked.Read(ref _revalidations);

    /// <summary>Number of requests that went to the API in full.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    internal void RecordFreshHit() => Interlocked.Increment(ref _freshHits);

    internal void RecordRevalidation() => Interlocked.Increment(ref _revalidations);

    internal void RecordMiss() => Interlocked.Increment(ref _misses);
}

namespace TcgDex.Caching;

/// <summary>
/// A cached HTTP response body together with the validator needed to refresh it.
/// </summary>
/// <remarks>
/// The body is stored as bytes rather than as a deserialized model, so one
/// cached entry serves every caller regardless of the type they deserialize
/// into, and nothing mutable is ever shared between them.
/// </remarks>
public sealed record CachedResponse
{
    /// <summary>The raw response body.</summary>
    public required byte[] Body { get; init; }

    /// <summary>
    /// The <c>ETag</c> the API returned, used to revalidate once the entry is no
    /// longer fresh.
    /// </summary>
    /// <remarks>
    /// Without this a stale entry costs a full re-download. With it, an unchanged
    /// resource costs a <c>304</c> and zero bytes of body.
    /// </remarks>
    public string? ETag { get; init; }

    /// <summary>The <c>Content-Type</c> to replay, so cached responses parse identically.</summary>
    public string? ContentType { get; init; }

    /// <summary>When the entry was stored or last revalidated.</summary>
    public required DateTimeOffset StoredAt { get; init; }

    /// <summary>Whether the entry is still within <paramref name="timeToLive"/> of <paramref name="now"/>.</summary>
    /// <param name="now">The current time.</param>
    /// <param name="timeToLive">How long an entry may be served without revalidation.</param>
    /// <returns><see langword="true"/> when the entry can be served without a request.</returns>
    public bool IsFresh(DateTimeOffset now, TimeSpan timeToLive) => now - StoredAt < timeToLive;
}

/// <summary>
/// Stores API responses so repeated reads avoid the network.
/// </summary>
/// <remarks>
/// Implement this to back the cache with something shared or persistent — Redis,
/// a distributed cache, or disk. The default is in-memory and per-process.
/// Implementations must be safe for concurrent use.
/// </remarks>
public interface ITcgDexResponseCache
{
    /// <summary>Retrieves an entry, or <see langword="null"/> if absent.</summary>
    /// <param name="key">The cache key, derived from the request URI.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The cached response, or <see langword="null"/>.</returns>
    ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Stores an entry.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="response">The response to store.</param>
    /// <param name="timeToLive">How long it may be served before revalidation.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the entry is stored.</returns>
    ValueTask SetAsync(
        string key,
        CachedResponse response,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default);

    /// <summary>Removes an entry, if present.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    /// <returns>A task that completes when the entry is gone.</returns>
    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);
}

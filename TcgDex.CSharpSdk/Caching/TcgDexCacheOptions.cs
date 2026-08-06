namespace TcgDex.Caching;

/// <summary>
/// Controls response caching.
/// </summary>
/// <remarks>
/// <para>
/// The API sends <c>Cache-Control: no-store</c>, so nothing caches by default and
/// enabling this is an explicit choice about your own data freshness. What makes
/// it safe is that the API also honours <c>If-None-Match</c>: once an entry is no
/// longer fresh it is <em>revalidated</em> rather than re-fetched, so a resource
/// that has not changed costs a <c>304</c> and zero bytes of body.
/// </para>
/// <para>
/// Time-to-live therefore controls how long you are willing to serve data
/// without asking, not how long before you pay for it again.
/// </para>
/// </remarks>
/// <remarks>
/// Not sealed: <see cref="GetTimeToLive"/> is the extension point for a caller
/// whose freshness policy differs from the path-based default.
/// </remarks>
public class TcgDexCacheOptions
{
    /// <summary>Default freshness window for card, set and series responses.</summary>
    public TimeSpan DefaultTimeToLive { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Freshness window for the enumeration endpoints.
    /// </summary>
    /// <remarks>
    /// Much longer than <see cref="DefaultTimeToLive"/> because the set of
    /// rarities, types and trainer types changes when a new expansion ships, not
    /// minute to minute. These are also the endpoints an application calls
    /// repeatedly to build filters and pickers.
    /// </remarks>
    public TimeSpan CatalogTimeToLive { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Freshness window for responses that carry market pricing.
    /// </summary>
    /// <remarks>
    /// Short, because prices are the one part of a card that moves daily and
    /// serving a stale price is worse than serving a stale card name.
    /// </remarks>
    public TimeSpan PricingTimeToLive { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Maximum number of entries the default in-memory cache holds before
    /// evicting the least recently used.
    /// </summary>
    /// <remarks>
    /// A bound matters here: a full set response is around 22 KB and there are
    /// hundreds of sets, so an unbounded cache in a long-running process is a
    /// slow memory leak.
    /// </remarks>
    public int MaxEntries { get; set; } = 512;

    /// <summary>
    /// Whether to collapse concurrent identical requests into one.
    /// </summary>
    /// <remarks>
    /// On by default. Without it, a cold cache under concurrent load sends one
    /// request per caller for the same URL — the classic cache stampede. With it,
    /// the first caller fetches and the rest await that same result.
    /// </remarks>
    public bool CoalesceConcurrentRequests { get; set; } = true;

    /// <summary>
    /// Chooses the freshness window for a request.
    /// </summary>
    /// <param name="requestUri">The request being cached.</param>
    /// <returns>How long the response may be served without revalidation.</returns>
    /// <remarks>
    /// Override this to apply your own policy. The default classifies by path:
    /// enumeration endpoints get <see cref="CatalogTimeToLive"/>, single cards
    /// get <see cref="PricingTimeToLive"/> because they embed pricing, and
    /// everything else gets <see cref="DefaultTimeToLive"/>.
    /// </remarks>
    public virtual TimeSpan GetTimeToLive(Uri requestUri)
    {
        Guard.NotNull(requestUri);

        var path = requestUri.AbsolutePath;

        if (IsCatalogPath(path))
        {
            return CatalogTimeToLive;
        }

        // A single card carries `pricing`; a card list does not.
        return IsSingleCardPath(path) ? PricingTimeToLive : DefaultTimeToLive;
    }

    private static bool IsCatalogPath(string path)
    {
        foreach (var endpoint in CatalogEndpoints)
        {
            if (path.EndsWith(endpoint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the path addresses one card rather than the card collection.
    /// </summary>
    private static bool IsSingleCardPath(string path)
    {
        var cards = path.IndexOf("/cards/", StringComparison.OrdinalIgnoreCase);

        return cards >= 0 && path.Length > cards + "/cards/".Length;
    }

    private static readonly string[] CatalogEndpoints =
    [
        "/categories", "/rarities", "/types", "/illustrators", "/stages",
        "/suffixes", "/variants", "/energy-types", "/regulation-marks",
        "/trainer-types", "/hp", "/retreats", "/dex-ids",
    ];
}

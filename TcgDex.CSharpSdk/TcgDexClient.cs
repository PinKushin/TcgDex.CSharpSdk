namespace TcgDex;

using TcgDex.Resources;

/// <summary>
/// Entry point to the TCGdex API.
/// </summary>
/// <remarks>
/// Resources are grouped the way the official SDKs group them, so
/// <c>client.Cards.GetAsync(id)</c> here corresponds to <c>tcgdex.card.get(id)</c>
/// there and the TCGdex documentation reads across without translation.
/// </remarks>
public interface ITcgDexClient
{
    /// <summary>Card lookups.</summary>
    ICardResource Cards { get; }

    /// <summary>Set lookups.</summary>
    ISetResource Sets { get; }

    /// <summary>Series lookups.</summary>
    ISerieResource Series { get; }

    /// <summary>Random card, set and series.</summary>
    IRandomResource Random { get; }

    /// <summary>The enumeration endpoints, for building filters and pickers.</summary>
    ICatalogResource Catalog { get; }
}

/// <inheritdoc cref="ITcgDexClient" />
public sealed class TcgDexClient : ITcgDexClient
{
    /// <summary>
    /// Creates a client over an existing <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="httpClient">The client used for requests.</param>
    /// <param name="options">Language and endpoint configuration. Defaults are used when omitted.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are not valid.</exception>
    /// <remarks>
    /// Prefer <c>AddTcgDex</c> in applications that use dependency injection —
    /// it wires this up through <c>IHttpClientFactory</c>, which handles handler
    /// lifetime and connection reuse.
    /// </remarks>
    public TcgDexClient(HttpClient httpClient, TcgDexOptions? options = null)
    {
        var transport = new TcgDexTransport(httpClient, options ?? new TcgDexOptions());

        Cards = new CardResource(transport);
        Sets = new SetResource(transport);
        Series = new SerieResource(transport);
        Random = new RandomResource(transport);
        Catalog = new CatalogResource(transport);
    }

    // Deliberately the only constructor. A second overload also taking
    // HttpClient makes IHttpClientFactory's typed-client activator ambiguous:
    // "Multiple constructors accepting all given argument types have been
    // found." AddTcgDex supplies the options through the factory instead.

    /// <inheritdoc />
    public ICardResource Cards { get; }

    /// <inheritdoc />
    public ISetResource Sets { get; }

    /// <inheritdoc />
    public ISerieResource Series { get; }

    /// <inheritdoc />
    public IRandomResource Random { get; }

    /// <inheritdoc />
    public ICatalogResource Catalog { get; }
}

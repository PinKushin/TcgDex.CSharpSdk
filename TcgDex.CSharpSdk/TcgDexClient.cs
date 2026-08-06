namespace TcgDex;

using TcgDex.Caching;
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
/// <remarks>
/// <para>
/// <b>On <see cref="HttpClient"/> lifetime.</b> When you pass one in, you own it
/// and this class never disposes it. When you use <see cref="Create"/>, the
/// returned client owns the one it made and disposes it with you.
/// </para>
/// <para>
/// Either way, hold a <em>single</em> instance for the life of your application.
/// Constructing one per request exhausts sockets, because a disposed
/// <see cref="HttpClient"/> leaves its connections in <c>TIME_WAIT</c>. In an
/// application with dependency injection, prefer <c>AddTcgDex</c> — it routes
/// through <c>IHttpClientFactory</c>, which handles this for you.
/// </para>
/// </remarks>
public sealed class TcgDexClient : ITcgDexClient, IDisposable
{
    /// <summary>
    /// The client to dispose on <see cref="Dispose"/>, or <see langword="null"/>
    /// when the caller supplied their own and therefore still owns it.
    /// </summary>
    private readonly HttpClient? _ownedHttpClient;

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
        : this(httpClient, options, ownsHttpClient: false)
    {
    }

    private TcgDexClient(HttpClient httpClient, TcgDexOptions? options, bool ownsHttpClient)
    {
        _ownedHttpClient = ownsHttpClient ? httpClient : null;

        var resolved = options ?? new TcgDexOptions();
        var transport = new TcgDexTransport(httpClient, resolved);
        var graphQl = new GraphQlTransport(httpClient, resolved);

        Cards = new CardResource(transport, graphQl);
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

    /// <summary>
    /// Creates a client with a correctly configured <see cref="HttpClient"/>,
    /// for applications without a dependency-injection container.
    /// </summary>
    /// <param name="options">Language and endpoint configuration.</param>
    /// <param name="configureCache">
    /// When supplied, enables response caching and applies this policy. Pass an
    /// empty delegate to enable it with defaults.
    /// </param>
    /// <returns>A client that owns and disposes its own <see cref="HttpClient"/>.</returns>
    /// <exception cref="ArgumentException">The options are not valid.</exception>
    /// <remarks>
    /// <para>
    /// <b>Create one and keep it.</b> This exists so that callers outside a
    /// container do not have to know how to configure
    /// <see cref="HttpClient"/> correctly — but it cannot stop the one mistake
    /// that matters, which is creating a client per request.
    /// </para>
    /// <para>
    /// The handler sets <c>PooledConnectionLifetime</c>, which a plain
    /// long-lived <see cref="HttpClient"/> does not: without it, connections are
    /// held indefinitely and never observe DNS changes. That is the failure a
    /// naive singleton runs into, and it is invisible until a host moves.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Once, for the life of the application.
    /// using var tcgdex = TcgDexClient.Create();
    ///
    /// var card = await tcgdex.Cards.GetAsync("swsh3-136", cancellationToken);
    /// </code>
    /// </example>
    public static TcgDexClient Create(
        TcgDexOptions? options = null,
        Action<TcgDexCacheOptions>? configureCache = null)
    {
        var resolved = options ?? new TcgDexOptions();
        resolved.Validate();

        // Connections are recycled on this interval so DNS changes are picked
        // up. A long-lived HttpClient over the default handler never does this.
        HttpMessageHandler handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        if (configureCache is not null)
        {
            var cacheOptions = new TcgDexCacheOptions();
            configureCache(cacheOptions);

            handler = new TcgDexCachingHandler(
                new MemoryTcgDexResponseCache(cacheOptions.MaxEntries),
                cacheOptions)
            {
                InnerHandler = handler,
            };
        }

        return new TcgDexClient(new HttpClient(handler), resolved, ownsHttpClient: true);
    }

    /// <summary>
    /// Disposes the <see cref="HttpClient"/> this instance created.
    /// </summary>
    /// <remarks>
    /// A client passed to the constructor belongs to the caller and is left
    /// untouched — disposing it here would break every other consumer sharing
    /// it, which is exactly the bug this ownership split prevents.
    /// </remarks>
    public void Dispose() => _ownedHttpClient?.Dispose();
}

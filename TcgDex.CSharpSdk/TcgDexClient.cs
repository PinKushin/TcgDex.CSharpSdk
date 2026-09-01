namespace TcgDex;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TcgDex.Caching;
using TcgDex.Diagnostics;
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
    /// How long a pooled connection may be reused before it is recycled so the
    /// next request re-resolves DNS.
    /// </summary>
    /// <remarks>
    /// Enforced through <c>SocketsHttpHandler.PooledConnectionLifetime</c> on
    /// modern targets and through <c>ServicePoint.ConnectionLeaseTimeout</c> on
    /// netstandard2.0. Different mechanism, same guarantee, one interval.
    /// </remarks>
    private static readonly TimeSpan ConnectionRecycleInterval = TimeSpan.FromMinutes(2);

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
        : this(httpClient, options, ownsHttpClient: false, loggerFactory: null)
    {
    }

    /// <summary>
    /// Creates a client that logs through the supplied factory.
    /// </summary>
    /// <param name="httpClient">The client used for requests.</param>
    /// <param name="options">Language and endpoint configuration.</param>
    /// <param name="loggerFactory">Where SDK log messages are written.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are not valid.</exception>
    /// <remarks>
    /// Registering through <c>AddTcgDex</c> supplies this automatically from the
    /// container, so this overload is for callers building the client by hand.
    /// </remarks>
    public TcgDexClient(HttpClient httpClient, TcgDexOptions? options, ILoggerFactory? loggerFactory)
        : this(httpClient, options, ownsHttpClient: false, loggerFactory)
    {
    }

    private TcgDexClient(
        HttpClient httpClient,
        TcgDexOptions? options,
        bool ownsHttpClient,
        ILoggerFactory? loggerFactory)
    {
        _ownedHttpClient = ownsHttpClient ? httpClient : null;

        TcgDexOptions resolved = options ?? new TcgDexOptions();

        // One category for the whole SDK, so a consumer can filter everything it
        // emits with a single rule on "TcgDex".
        ILogger logger = loggerFactory?.CreateLogger("TcgDex") ?? NullLogger.Instance;

        TcgDexTransport transport = new(httpClient, resolved, logger);
        GraphQlTransport graphQl = new(httpClient, resolved, logger);

        logger.ClientConfigured(resolved.Language, resolved.BaseAddress);

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
    /// <param name="loggerFactory">Optional destination for SDK log messages.</param>
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
    /// using TcgDexClient tcgdex = TcgDexClient.Create();
    ///
    /// Card? card = await tcgdex.Cards.GetAsync("swsh3-136", cancellationToken);
    /// </code>
    /// </example>
    public static TcgDexClient Create(
        TcgDexOptions? options = null,
        Action<TcgDexCacheOptions>? configureCache = null,
        ILoggerFactory? loggerFactory = null)
    {
        TcgDexOptions resolved = options ?? new TcgDexOptions();
        resolved.Validate();

        // Run the caller's cache configuration before any handler exists, so no
        // user code runs in the window between constructing the transport handler
        // and handing ownership to HttpClient below — the only path on which a
        // handler could leak (what CA2000 flags on the construction that follows).
        TcgDexCacheOptions? cacheOptions = null;
        if (configureCache is not null)
        {
            cacheOptions = new();
            configureCache(cacheOptions);
        }

        // CA2000: every handler constructed below is owned by the HttpClient at
        // the end of this method (ownsHttpClient: true), whose Dispose disposes
        // the whole chain. The cache callback above is the only user code, and it
        // has already run, so nothing executes between construction and that
        // ownership transfer — there is no path on which a handler leaks.
#pragma warning disable CA2000

        // Connections are recycled on this interval so DNS changes are picked
        // up. A long-lived HttpClient over the default handler never does this.
#if NETSTANDARD2_0
        // SocketsHttpHandler is .NET Core 2.1+, so on .NET Framework the same
        // guarantee comes from the mechanism that platform actually has:
        // ConnectionLeaseTimeout closes a connection after the interval and
        // forces the next request to re-resolve DNS. Set on the ServicePoint
        // for the base address, which is the only host this client talks to.
        // On other runtimes that reach this code path the call is a harmless
        // no-op.
        System.Net.ServicePointManager
            .FindServicePoint(resolved.BaseAddress)
            .ConnectionLeaseTimeout = (int)ConnectionRecycleInterval.TotalMilliseconds;

        // DecompressionMethods.All is .NET 5+, and Brotli is not available
        // here — these two are what netstandard2.0 can offer.
        HttpMessageHandler handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        };
#else
        HttpMessageHandler handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = ConnectionRecycleInterval,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
#endif

        // Wrapped before the cache below, which leaves failover INNERMOST. The
        // cache keys on the request URI, so a host rewritten above it would key
        // the same resource separately for every endpoint; down here the cache
        // only ever sees the canonical address.
        if (resolved.FailoverEndpoints.Count > 0)
        {
            IReadOnlyList<Uri> failoverEndpoints = TcgDexFailoverHandler.Deduplicate(
                resolved.FailoverEndpoints, resolved.BaseAddress);

            handler = new TcgDexFailoverHandler(
                resolved.BaseAddress,
                resolved.GraphQlEndpoint,
                failoverEndpoints,
                resolved.FailoverAttemptTimeout,
                resolved.FailoverCooldown,
                new FailoverCooldowns(failoverEndpoints.Count + 1))
            {
                InnerHandler = handler,
            };
        }

        if (cacheOptions is not null)
        {
            handler = new TcgDexCachingHandler(
                new MemoryTcgDexResponseCache(cacheOptions.MaxEntries),
                cacheOptions,
                timeProvider: null,
                maxResponseBytes: resolved.MaxResponseBytes)
            {
                InnerHandler = handler,
            };
        }

        return new TcgDexClient(new HttpClient(handler), resolved, ownsHttpClient: true, loggerFactory);
#pragma warning restore CA2000
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

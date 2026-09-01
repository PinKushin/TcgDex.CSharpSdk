namespace Microsoft.Extensions.DependencyInjection;

using System;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TcgDex;
using TcgDex.Caching;

/// <summary>
/// Registers the TCGdex client with a dependency-injection container.
/// </summary>
public static class TcgDexServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ITcgDexClient"/> and its backing
    /// <see cref="System.Net.Http.HttpClient"/>.
    /// </summary>
    /// <param name="services">The container to add to.</param>
    /// <param name="configure">Optional configuration; defaults target the official host in English.</param>
    /// <returns>
    /// The <see cref="IHttpClientBuilder"/> for the underlying client, so callers
    /// can attach their own handlers or resilience policies.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <remarks>
    /// Registered through <c>IHttpClientFactory</c> so handler lifetime and
    /// connection pooling are managed correctly. Constructing
    /// <see cref="System.Net.Http.HttpClient"/> by hand per call is the usual
    /// cause of socket exhaustion in SDK consumers.
    /// </remarks>
    public static IHttpClientBuilder AddTcgDex(
        this IServiceCollection services,
        Action<TcgDexOptions>? configure = null)
    {
        Guard.NotNull(services);

        TcgDexOptions options = RegisterOptions(services, configure);

        return AttachFailover(RegisterClient(services), options);
    }

    /// <summary>
    /// Builds, validates and registers the options.
    /// </summary>
    private static TcgDexOptions RegisterOptions(
        IServiceCollection services,
        Action<TcgDexOptions>? configure)
    {
        TcgDexOptions options = new();
        configure?.Invoke(options);

        // Fail here, at registration, rather than on the first request — a
        // typo'd language should not surface later as a 404 that reads like a
        // missing card.
        options.Validate();

        services.TryAddSingleton(options);
        services.Configure<TcgDexOptions>(configured =>
        {
            configured.BaseAddress = options.BaseAddress;
            configured.Language = options.Language;
            configured.GraphQlEndpoint = options.GraphQlEndpoint;
        });

        return options;
    }

    /// <summary>
    /// Registers the typed client itself, with no handlers attached.
    /// </summary>
    private static IHttpClientBuilder RegisterClient(IServiceCollection services)
        // Constructed explicitly rather than by the typed-client activator, so
        // TcgDexClient can keep a single constructor and stay ergonomic to
        // `new` up outside a container.
        => services.AddHttpClient<ITcgDexClient, TcgDexClient>(
            (httpClient, provider) => new TcgDexClient(
                httpClient,
                provider.GetRequiredService<TcgDexOptions>(),
                // Resolved rather than required: logging is a convenience, and a
                // container without it should still produce a working client.
                provider.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()));

    /// <summary>
    /// Adds the failover handler, when endpoints are configured.
    /// </summary>
    /// <remarks>
    /// <b>Added last on purpose.</b> Handlers run in the order they are added,
    /// so this leaves failover innermost — below the response cache, which keys
    /// on the request URI. Above the cache, a rewritten host would key the same
    /// resource separately per endpoint and cache it once for each.
    /// </remarks>
    private static IHttpClientBuilder AttachFailover(
        IHttpClientBuilder builder,
        TcgDexOptions options)
    {
        if (options.FailoverEndpoints.Count == 0)
        {
            return builder;
        }

        // Created ONCE and captured, not built inside the factory.
        // IHttpClientFactory rebuilds the handler chain every HandlerLifetime —
        // two minutes by default — so a handler that made its own cooldown state
        // would forget every failure on that schedule, quietly capping a
        // five-minute cooldown at two and re-probing dead endpoints far more
        // often than configured.
        IReadOnlyList<Uri> endpoints =
            TcgDexFailoverHandler.Deduplicate(options.FailoverEndpoints, options.BaseAddress);

        FailoverCooldowns cooldowns = new(endpoints.Count + 1);

        return builder.AddHttpMessageHandler(() => new TcgDexFailoverHandler(
            options.BaseAddress,
            options.GraphQlEndpoint,
            endpoints,
            options.FailoverAttemptTimeout,
            options.FailoverCooldown,
            cooldowns));
    }

    /// <summary>
    /// Registers <see cref="ITcgDexClient"/> with response caching enabled.
    /// </summary>
    /// <param name="services">The container to add to.</param>
    /// <param name="configure">Optional client configuration.</param>
    /// <param name="configureCache">Optional cache policy.</param>
    /// <returns>
    /// The <see cref="IHttpClientBuilder"/> for the underlying client, so callers
    /// can attach further handlers.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// Caching is opt-in because the API sends <c>Cache-Control: no-store</c>, so
    /// enabling it is a decision about your own tolerance for stale data rather
    /// than something the service asks for.
    /// </para>
    /// <para>
    /// It is worth enabling: the API honours <c>If-None-Match</c>, so once an
    /// entry falls out of its freshness window it is revalidated rather than
    /// re-fetched. An unchanged 22 KB set response then costs a <c>304</c> and
    /// zero bytes of body.
    /// </para>
    /// <para>
    /// Register your own <see cref="ITcgDexResponseCache"/> before calling this
    /// to back the cache with something shared or persistent; otherwise a bounded
    /// in-process cache is used.
    /// </para>
    /// </remarks>
    public static IHttpClientBuilder AddTcgDexWithCaching(
        this IServiceCollection services,
        Action<TcgDexOptions>? configure = null,
        Action<TcgDexCacheOptions>? configureCache = null)
    {
        Guard.NotNull(services);

        TcgDexCacheOptions cacheOptions = new();
        configureCache?.Invoke(cacheOptions);

        services.TryAddSingleton(cacheOptions);
        services.TryAddSingleton<ITcgDexResponseCache>(
            _ => new MemoryTcgDexResponseCache(cacheOptions.MaxEntries));

        TcgDexOptions options = RegisterOptions(services, configure);

        // Caching is added before failover so it ends up OUTSIDE it: the cache
        // then only ever sees the canonical address, and a failover swaps the
        // host below it without fragmenting the keys.
        return AttachFailover(
            RegisterClient(services)
                .AddHttpMessageHandler(provider => new TcgDexCachingHandler(
                    provider.GetRequiredService<ITcgDexResponseCache>(),
                    provider.GetRequiredService<TcgDexCacheOptions>(),
                    timeProvider: null,
                    maxResponseBytes: options.MaxResponseBytes)),
            options);
    }
}

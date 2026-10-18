namespace Microsoft.Extensions.DependencyInjection;

using System;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TcgDex;
using TcgDex.Caching;
using TcgDex.Diagnostics;

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
    /// <exception cref="InvalidOperationException">
    /// <c>AddTcgDex</c> or <c>AddTcgDexWithCaching</c> was already called on
    /// <paramref name="services"/>.
    /// </exception>
    public static IHttpClientBuilder AddTcgDex(
        this IServiceCollection services,
        Action<TcgDexOptions>? configure = null)
    {
        Guard.NotNull(services);
        EnsureFirstRegistration(services);

        RegisterOptions(services, configure);

        return RegisterClient(services);
    }

    /// <summary>
    /// Refuses a second registration rather than ignoring it.
    /// </summary>
    /// <remarks>
    /// The second call's options used to be dropped silently (the first
    /// <c>TryAdd</c> won), so a caller configuring the client in two places got
    /// whichever ran first with nothing to say so.
    /// </remarks>
    private static void EnsureFirstRegistration(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ITcgDexClient)))
        {
            throw new InvalidOperationException(
                "AddTcgDex or AddTcgDexWithCaching has already been called on this service " +
                "collection. Call one of them once and configure everything in that call; a " +
                "second call's options would otherwise be ignored.");
        }
    }

    /// <summary>
    /// Registers the options through the options system, validated on start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller's delegate is registered with <c>Configure</c>, the standard
    /// options pattern, so <c>IOptions</c>, <c>IOptionsSnapshot</c> and
    /// <c>IOptionsMonitor</c> all see every property.
    /// </para>
    /// <para>
    /// <see cref="TcgDexOptionsValidator"/> checks the final options, merged from
    /// every source (appsettings, other <c>Configure</c> calls), and
    /// <c>ValidateOnStart</c> makes a host fail at start with an
    /// <see cref="OptionsValidationException"/> rather than on the first request
    /// — a typo'd language should not surface later as a 404 that reads like a
    /// missing card. Without a host, the same exception surfaces on first
    /// resolution.
    /// </para>
    /// </remarks>
    private static void RegisterOptions(
        IServiceCollection services,
        Action<TcgDexOptions>? configure)
    {
        services.AddOptions<TcgDexOptions>()
            .Configure(configure ?? (static _ => { }))
            .ValidateOnStart();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TcgDexOptions>, TcgDexOptionsValidator>());
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<TcgDexOptions>>().Value);
    }

    /// <summary>
    /// Adapts <see cref="TcgDexOptions.Validate"/> to the options pipeline,
    /// keeping its messages.
    /// </summary>
    private sealed class TcgDexOptionsValidator : IValidateOptions<TcgDexOptions>
    {
        public ValidateOptionsResult Validate(string? name, TcgDexOptions options)
        {
            try
            {
                options.Validate();
                return ValidateOptionsResult.Success;
            }
            catch (ArgumentException exception)
            {
                // Handled, not swallowed: the message becomes the failure the
                // options system raises as OptionsValidationException.
                return ValidateOptionsResult.Fail(exception.Message);
            }
        }
    }

    /// <summary>
    /// Registers the typed client itself, with no handlers attached.
    /// </summary>
    private static IHttpClientBuilder RegisterClient(IServiceCollection services)
    {
        // The typed client is transient, as IHttpClientFactory's typed clients
        // are, so a parsed-model cache it built for itself started empty on
        // every resolution and never hit. It lives in the container instead,
        // one per application, and each client borrows it. Keyed by URL, type
        // and ETag, so sharing it changes nothing but the hit rate.
        services.TryAddSingleton(provider => new DeserializedResponseCache(
            provider.GetRequiredService<TcgDexOptions>().MaxDeserializedCacheEntries));

        // Constructed explicitly rather than by the typed-client activator, so
        // TcgDexClient's public constructors stay unambiguous and ergonomic to
        // `new` up outside a container.
        return services.AddHttpClient<ITcgDexClient, TcgDexClient>(
            (httpClient, provider) =>
            {
                TcgDexOptions options = provider.GetRequiredService<TcgDexOptions>();

                return new TcgDexClient(
                    httpClient,
                    options,
                    // Resolved rather than required: logging is a convenience, and a
                    // container without it should still produce a working client.
                    provider.GetService<ILoggerFactory>(),
                    // Zero disables the cache, and the cache cannot be built at zero.
                    options.MaxDeserializedCacheEntries > 0
                        ? provider.GetRequiredService<DeserializedResponseCache>()
                        : null);
            });
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
    /// <exception cref="InvalidOperationException">
    /// <c>AddTcgDex</c> or <c>AddTcgDexWithCaching</c> was already called on
    /// <paramref name="services"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Inject <see cref="TcgDexCacheStatistics"/> to see how the cache is
    /// answering.
    /// </para>
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
        EnsureFirstRegistration(services);

        // The options pattern, as for TcgDexOptions: configureCache is one
        // Configure among any others (appsettings, Configure<TcgDexCacheOptions>),
        // merged when first resolved. A TcgDexCacheOptions registered before
        // this call, such as a subclass overriding GetTimeToLive, still wins.
        services.AddOptions<TcgDexCacheOptions>().Configure(configureCache ?? (static _ => { }));
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<TcgDexCacheOptions>>().Value);
        services.TryAddSingleton<ITcgDexResponseCache>(
            provider => new MemoryTcgDexResponseCache(provider.GetRequiredService<TcgDexCacheOptions>().MaxEntries));

        // IHttpClientFactory rebuilds handlers every handler lifetime, so the
        // counters and the in-flight table live in a singleton the handlers
        // share, not in the handler (TcgDexCachingState).
        services.TryAddSingleton(_ => new TcgDexCachingState());
        services.TryAddSingleton(provider => provider.GetRequiredService<TcgDexCachingState>().Statistics);

        RegisterOptions(services, configure);

        return RegisterClient(services)
            .AddHttpMessageHandler(provider => new TcgDexCachingHandler(
                provider.GetRequiredService<ITcgDexResponseCache>(),
                provider.GetRequiredService<TcgDexCacheOptions>(),
                timeProvider: null,
                provider.GetRequiredService<TcgDexOptions>().MaxResponseBytes,
                provider.GetRequiredService<TcgDexCachingState>(),
                provider.GetService<ILoggerFactory>()?.CreateLogger(TcgDexLog.Category)));
    }
}

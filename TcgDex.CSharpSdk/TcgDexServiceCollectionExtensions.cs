namespace Microsoft.Extensions.DependencyInjection;

using System;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TcgDex;

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
        ArgumentNullException.ThrowIfNull(services);

        var options = new TcgDexOptions();
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

        // Constructed explicitly rather than by the typed-client activator, so
        // TcgDexClient can keep a single constructor and stay ergonomic to
        // `new` up outside a container.
        return services.AddHttpClient<ITcgDexClient, TcgDexClient>(
            (httpClient, provider) => new TcgDexClient(
                httpClient,
                provider.GetRequiredService<TcgDexOptions>()));
    }
}

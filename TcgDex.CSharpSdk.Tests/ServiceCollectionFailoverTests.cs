namespace TcgDex.Tests;

using System.Net.Http;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using TcgDex;

/// <summary>
/// Failover as it is wired through a dependency-injection container.
/// </summary>
/// <remarks>
/// The DI path builds its pipeline differently from <see cref="TcgDexClient.Create"/>
/// — handlers are appended to an <c>IHttpClientBuilder</c> and assembled by
/// <c>IHttpClientFactory</c> — so nothing proven about one proves anything about
/// the other. This is the path most consumers use.
/// </remarks>
[TestFixture]
public sealed class ServiceCollectionFailoverTests
{
    /// <summary>
    /// The assembled outermost handler for the typed client, as
    /// <c>IHttpClientFactory</c> builds it.
    /// </summary>
    private static HttpMessageHandler Pipeline(IServiceCollection services)
    {
        ServiceProvider provider = services.BuildServiceProvider();

        return provider
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(nameof(ITcgDexClient));
    }

    /// <summary>Walks the chain, outermost first.</summary>
    private static List<Type> Chain(HttpMessageHandler outermost)
    {
        List<Type> chain = [];

        for (HttpMessageHandler? current = outermost;
            current is not null;
            current = (current as DelegatingHandler)?.InnerHandler)
        {
            chain.Add(current.GetType());
        }

        return chain;
    }

    [Test]
    public void AddTcgDex_WithoutFailover_AddsNoFailoverHandler()
    {
        // The DI twin of the most important test in the options suite: a build
        // that attached the handler unconditionally would put every consumer who
        // never asked for failover behind it.
        ServiceCollection services = new();
        services.AddTcgDex();

        Chain(Pipeline(services)).ShouldNotContain(typeof(TcgDexFailoverHandler));
    }

    [Test]
    public void AddTcgDex_WithFailover_PassesTheConfigurationThrough()
    {
        // Asserting the handler's TYPE is the proxy the Create-path test was
        // strengthened to stop relying on, and this is the path most consumers
        // use. AttachFailover passes two adjacent, interchangeable TimeSpans;
        // swapping them compiles and turns a five-minute cooldown into ten
        // seconds — a dead endpoint re-probed thirty times more often, on the
        // day the API is down.
        ServiceCollection services = new();
        services.AddTcgDex(options =>
        {
            options.FailoverAttemptTimeout = TimeSpan.FromSeconds(4);
            options.FailoverCooldown = TimeSpan.FromMinutes(9);
            options.UseFailover(TcgDexMirror.Eu2);
        });

        ServiceProvider provider = services.BuildServiceProvider();

        TcgDexFailoverHandler handler = BuildHandler(
            provider,
            provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
                .Get(nameof(ITcgDexClient)));

        Read<TimeSpan>(handler, "_attemptTimeout").ShouldBe(TimeSpan.FromSeconds(4));
        Read<TimeSpan>(handler, "_cooldown").ShouldBe(TimeSpan.FromMinutes(9));
        Read<IReadOnlyList<Uri>>(handler, "_endpoints")
            .Select(endpoint => endpoint.ToString())
            .ShouldBe(["https://api.eu2.tcgdex.net/v2/"]);
    }

    [Test]
    public void AddTcgDex_WithAMirrorAlsoListedAsFallback_DropsTheDuplicate()
    {
        // `UseMirror(Eu2).UseFailover()` is the natural way to write this, since
        // the two are documented side by side. Deduplication lives in the wiring,
        // so proving the static helper works does not prove either call site
        // uses it.
        ServiceCollection services = new();
        services.AddTcgDex(options =>
        {
            options.UseMirror(TcgDexMirror.Eu2);
            options.UseFailover();
        });

        ServiceProvider provider = services.BuildServiceProvider();

        TcgDexFailoverHandler handler = BuildHandler(
            provider,
            provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
                .Get(nameof(ITcgDexClient)));

        Read<IReadOnlyList<Uri>>(handler, "_endpoints")
            .ShouldNotContain(new Uri("https://api.eu2.tcgdex.net/v2/"));
    }

    private static T Read<T>(object target, string field)
        => (T)target.GetType()
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)
            .ShouldNotBeNull()
            .GetValue(target)
            .ShouldNotBeNull();

    [Test]
    public void AddTcgDexWithCaching_PutsTheCacheOutsideFailover()
    {
        // The ordering comment on AttachFailover is load-bearing and was
        // previously unverified. Reversing the two AddHttpMessageHandler calls
        // compiles, serves correct responses, and silently keys the cache per
        // endpoint — so every failover throws away the cache for that resource.
        ServiceCollection services = new();
        services.AddTcgDexWithCaching(
            options => options.UseFailover(TcgDexMirror.Eu2),
            _ => { });

        List<Type> chain = Chain(Pipeline(services));

        int cache = chain.IndexOf(typeof(TcgDex.Caching.TcgDexCachingHandler));
        int failover = chain.IndexOf(typeof(TcgDexFailoverHandler));

        cache.ShouldBeGreaterThanOrEqualTo(0);
        failover.ShouldBeGreaterThanOrEqualTo(0);
        cache.ShouldBeLessThan(failover, "the cache must sit outside failover");
    }

    [Test]
    public void TheCooldownSurvivesTheHandlerChainBeingRebuilt()
    {
        // IHttpClientFactory rebuilds the chain every HandlerLifetime — two
        // minutes by default. A handler that owned its cooldown state would
        // forget every failure on that schedule, capping a five-minute cooldown
        // at two and re-probing dead endpoints far more often than configured.
        //
        // The registered builder actions are run twice against fresh builders
        // rather than calling CreateHandler twice. CreateHandler CACHES the
        // assembled chain for the handler lifetime, so two calls return the same
        // object and comparing their state proves nothing — an earlier version of
        // this test did exactly that and passed against a build that created the
        // state per handler.
        ServiceCollection services = new();
        services.AddTcgDex(options => options.UseFailover(TcgDexMirror.Eu2));

        ServiceProvider provider = services.BuildServiceProvider();

        HttpClientFactoryOptions factoryOptions = provider
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(nameof(ITcgDexClient));

        TcgDexFailoverHandler first = BuildHandler(provider, factoryOptions);
        TcgDexFailoverHandler second = BuildHandler(provider, factoryOptions);

        first.ShouldNotBeSameAs(second, "the two chains must be genuinely separate");

        FieldInfo state = typeof(TcgDexFailoverHandler)
            .GetField("_cooldowns", BindingFlags.NonPublic | BindingFlags.Instance)
            .ShouldNotBeNull();

        state.GetValue(second).ShouldBeSameAs(state.GetValue(first));
    }

    /// <summary>
    /// Runs the registered handler-builder actions against a fresh builder,
    /// producing a genuinely new chain the way a lifetime rotation does.
    /// </summary>
    private static TcgDexFailoverHandler BuildHandler(
        IServiceProvider provider,
        HttpClientFactoryOptions factoryOptions)
    {
        FreshBuilder builder = new(provider) { Name = nameof(ITcgDexClient) };

        foreach (Action<HttpMessageHandlerBuilder> action in
            factoryOptions.HttpMessageHandlerBuilderActions)
        {
            action(builder);
        }

        return builder.AdditionalHandlers.OfType<TcgDexFailoverHandler>().Single();
    }

    private sealed class FreshBuilder(IServiceProvider services) : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }

        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();

        public override IList<DelegatingHandler> AdditionalHandlers { get; } = [];

        public override IServiceProvider Services { get; } = services;

        public override HttpMessageHandler Build()
            => CreateHandlerPipeline(PrimaryHandler, AdditionalHandlers);
    }
}

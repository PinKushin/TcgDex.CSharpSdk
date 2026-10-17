namespace TcgDex.Tests;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TcgDex;
using TcgDex.Caching;
using TcgDex.Models;
using TcgDex.Tests.Diagnostics;

/// <summary>
/// What <c>AddTcgDex</c> and <c>AddTcgDexWithCaching</c> put in the container,
/// and what survives <c>IHttpClientFactory</c>'s handler rotation.
/// </summary>
/// <remarks>
/// <para>
/// <c>IHttpClientFactory</c> rebuilds a client's handler chain every handler
/// lifetime (two minutes by default) and resolves the typed client as
/// transient. Anything a handler or the client holds in an instance field is
/// therefore per-rotation or per-resolution, which is how the caching handler's
/// counters and request coalescing, and the transport's parsed-model cache,
/// silently reset.
/// </para>
/// <para>
/// A rotation is reproduced here without a clock: the factory builds a chain by
/// running the registered <see cref="HttpClientFactoryOptions.HttpMessageHandlerBuilderActions"/>
/// on a fresh <see cref="HttpMessageHandlerBuilder"/>, and so does
/// <see cref="BuildChain"/>. Two calls are two rotations.
/// </para>
/// </remarks>
[TestFixture]
public sealed class DependencyInjectionTests
{
    private const string SetUri = "https://api.tcgdex.net/v2/en/sets/swsh3";

    /// <summary>A primary handler that answers every request through a delegate.</summary>
    private sealed class Primary(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _requests;

        internal int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string? etag = "\"v1\"")
    {
        HttpResponseMessage response = new(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

        if (etag is not null)
        {
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        }

        return response;
    }

    private static Primary AlwaysOk(string body = "{}")
        => new(_ => Task.FromResult(Json(HttpStatusCode.OK, body)));

    /// <summary>Builds the client's handler chain exactly as one rotation of the factory does.</summary>
    private static (HttpMessageInvoker Invoker, TcgDexCachingHandler Handler) BuildChain(
        IServiceProvider provider,
        HttpMessageHandler primary)
    {
        HttpClientFactoryOptions factoryOptions = provider
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(nameof(ITcgDexClient));

        HttpMessageHandlerBuilder builder = provider.GetRequiredService<HttpMessageHandlerBuilder>();
        builder.Name = nameof(ITcgDexClient);

        foreach (Action<HttpMessageHandlerBuilder> action in factoryOptions.HttpMessageHandlerBuilderActions)
        {
            action(builder);
        }

        builder.PrimaryHandler = primary;

        return (new HttpMessageInvoker(builder.Build()), builder.AdditionalHandlers.OfType<TcgDexCachingHandler>().Single());
    }

    private static Task<HttpResponseMessage> Get(HttpMessageInvoker invoker, string uri = SetUri)
        => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);

    // ----- options -----

    [Test]
    public void AddTcgDex_EveryPropertyReachesEveryOptionsInterface()
    {
        // Configure used to copy three of the seven properties by hand, so an
        // IOptions consumer saw the default for the other four while the
        // client itself used the configured values.
        ServiceCollection services = new();

        services.AddTcgDex(o =>
        {
            o.BaseAddress = new Uri("https://mirror.example/v2/");
            o.Language = TcgDexLanguages.German;
            o.GraphQlEndpoint = new Uri("https://mirror.example/v2/graphql");
            o.MaxResponseBytes = 1234;
            o.Timeout = TimeSpan.FromSeconds(7);
            o.DeserializePricing = false;
            o.MaxDeserializedCacheEntries = 3;
        });

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        TcgDexOptions[] seen =
        [
            provider.GetRequiredService<TcgDexOptions>(),
            provider.GetRequiredService<IOptions<TcgDexOptions>>().Value,
            provider.GetRequiredService<IOptionsMonitor<TcgDexOptions>>().CurrentValue,
            scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<TcgDexOptions>>().Value,
        ];

        foreach (TcgDexOptions options in seen)
        {
            options.BaseAddress.ShouldBe(new Uri("https://mirror.example/v2/"));
            options.Language.ShouldBe("de");
            options.GraphQlEndpoint.ShouldBe(new Uri("https://mirror.example/v2/graphql"));
            options.MaxResponseBytes.ShouldBe(1234);
            options.Timeout.ShouldBe(TimeSpan.FromSeconds(7));
            options.DeserializePricing.ShouldBeFalse();
            options.MaxDeserializedCacheEntries.ShouldBe(3);
        }
    }

    [Test]
    public void AddTcgDex_CalledTwice_Throws()
    {
        // A second call used to be silently ignored: TryAdd kept the first
        // options, so the second call's settings never applied.
        ServiceCollection services = new();
        services.AddTcgDex();

        Should.Throw<InvalidOperationException>(() => services.AddTcgDex(o => o.Language = TcgDexLanguages.French))
            .Message.ShouldContain("AddTcgDex");
    }

    [Test]
    public void AddTcgDexWithCaching_AfterAddTcgDex_Throws()
    {
        ServiceCollection services = new();
        services.AddTcgDex();

        Should.Throw<InvalidOperationException>(() => services.AddTcgDexWithCaching());
    }

    // ----- the parsed-model cache -----

    [Test]
    public async Task ParsedModels_AreSharedAcrossClientResolutions()
    {
        // The typed client is transient, and each instance built its own
        // parsed-model cache, so under DI it could never hit: every resolution
        // started empty. Shared, the second client gets the first one's object.
        Primary primary = AlwaysOk(Fixture.ReadText("set-full.json"));
        ServiceCollection services = new();
        services.AddTcgDex().ConfigurePrimaryHttpMessageHandler(() => primary);

        using ServiceProvider provider = services.BuildServiceProvider();

        Set? first = await provider.GetRequiredService<ITcgDexClient>().Sets.GetAsync("swsh3");
        Set? second = await provider.GetRequiredService<ITcgDexClient>().Sets.GetAsync("swsh3");

        primary.Requests.ShouldBe(2);
        first.ShouldNotBeNull();
        second.ShouldBeSameAs(first);
    }

    // ----- the caching handler across rotations -----

    [Test]
    public async Task CachingHandler_CountersSurviveARotation()
    {
        ServiceCollection services = new();
        services.AddTcgDexWithCaching();

        using ServiceProvider provider = services.BuildServiceProvider();

        (HttpMessageInvoker before, _) = BuildChain(provider, AlwaysOk());
        (await Get(before)).Dispose();
        (await Get(before)).Dispose();

        (_, TcgDexCachingHandler after) = BuildChain(provider, AlwaysOk());

        after.Misses.ShouldBe(1);
        after.FreshHits.ShouldBe(1);
    }

    [Test]
    public async Task CachingHandler_CountersAreReachableFromTheContainer()
    {
        ServiceCollection services = new();
        services.AddTcgDexWithCaching();

        using ServiceProvider provider = services.BuildServiceProvider();

        (HttpMessageInvoker invoker, _) = BuildChain(provider, AlwaysOk());
        (await Get(invoker)).Dispose();
        (await Get(invoker)).Dispose();

        TcgDexCacheStatistics statistics = provider.GetRequiredService<TcgDexCacheStatistics>();

        statistics.Misses.ShouldBe(1);
        statistics.FreshHits.ShouldBe(1);
        statistics.Revalidations.ShouldBe(0);
    }

    [Test]
    public async Task CachingHandler_CoalescesAcrossARotation()
    {
        // A request in flight on the old chain is joined by the same request on
        // the new one, rather than each chain sending its own. Deterministic
        // without a clock: everything up to the first incomplete await runs
        // synchronously inside SendAsync, so when the second SendAsync returns
        // its task, that request is already waiting on the leader.
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Primary primary = new(async _ =>
        {
            await release.Task;
            return Json(HttpStatusCode.OK, "{}");
        });

        ServiceCollection services = new();
        services.AddTcgDexWithCaching();

        using ServiceProvider provider = services.BuildServiceProvider();

        (HttpMessageInvoker before, _) = BuildChain(provider, primary);
        (HttpMessageInvoker after, _) = BuildChain(provider, primary);

        Task<HttpResponseMessage> leader = Get(before);
        Task<HttpResponseMessage> waiter = Get(after);

        release.SetResult(true);

        (await leader).Dispose();
        (await waiter).Dispose();

        primary.Requests.ShouldBe(1);
    }

    // ----- cache log events 1100-1104 -----

    /// <summary>Sends two requests for the same URL and returns the cache event ids logged.</summary>
    private static async Task<List<int>> CacheEventsAsync(
        Action<TcgDexCacheOptions>? configureCache,
        HttpStatusCode secondStatus)
    {
        RecordingLogger logger = new(LogLevel.Trace);
        Queue<HttpResponseMessage> queue = new([Json(HttpStatusCode.OK, "{}"), Json(secondStatus, string.Empty)]);

        ServiceCollection services = new();
        services.AddSingleton(logger.Factory);
        services.AddTcgDexWithCaching(configureCache: configureCache);

        using ServiceProvider provider = services.BuildServiceProvider();
        (HttpMessageInvoker invoker, _) = BuildChain(provider, new Primary(_ => Task.FromResult(queue.Dequeue())));

        (await Get(invoker)).Dispose();
        (await Get(invoker)).Dispose();

        return logger.Entries.Select(e => e.EventId).Where(id => id is >= 1100 and < 1200).ToList();
    }

    [Test]
    public async Task CachingHandler_LogsMissThenHit()
        => (await CacheEventsAsync(configureCache: null, HttpStatusCode.OK)).ShouldBe([1102, 1100]);

    [Test]
    public async Task CachingHandler_LogsRevalidation()
        => (await CacheEventsAsync(c => c.DefaultTimeToLive = TimeSpan.Zero, HttpStatusCode.NotModified))
            .ShouldBe([1102, 1101]);

    [Test]
    public async Task CachingHandler_LogsEvictionOnAFailedResponse()
        => (await CacheEventsAsync(c => c.DefaultTimeToLive = TimeSpan.Zero, HttpStatusCode.InternalServerError))
            .ShouldBe([1102, 1104]);

    [Test]
    public async Task CachingHandler_LogsACoalescedRequest()
    {
        RecordingLogger logger = new(LogLevel.Trace);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Primary primary = new(async _ =>
        {
            await release.Task;
            return Json(HttpStatusCode.OK, "{}");
        });

        ServiceCollection services = new();
        services.AddSingleton(logger.Factory);
        services.AddTcgDexWithCaching();

        using ServiceProvider provider = services.BuildServiceProvider();
        (HttpMessageInvoker invoker, _) = BuildChain(provider, primary);

        Task<HttpResponseMessage> leader = Get(invoker);
        Task<HttpResponseMessage> waiter = Get(invoker);

        logger.Entries.Select(e => e.EventId).ShouldContain(1103);

        release.SetResult(true);
        (await leader).Dispose();
        (await waiter).Dispose();
    }
}

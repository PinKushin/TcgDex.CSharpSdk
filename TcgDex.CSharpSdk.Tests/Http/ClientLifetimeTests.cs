namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TcgDex;
using TcgDex.Models;
using TcgDex.Tests.Diagnostics;

/// <summary>
/// Ownership of the underlying <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// Getting this wrong is one of the classic .NET faults: disposing a shared
/// client breaks every other consumer of it, and never disposing one you created
/// leaks. The rule is that whoever created it disposes it.
/// </remarks>
[TestFixture]
public sealed class ClientLifetimeTests
{
    [Test]
    public async Task DisposingTheSdkClient_DoesNotDisposeACallerSuppliedHttpClient()
    {
        // The caller may be sharing that HttpClient with the rest of their
        // application; disposing it here would break them.
        RecordingHandler handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        using HttpClient httpClient = new(handler);
        TcgDexClient client = new(httpClient, new TcgDexOptions());

        client.Dispose();

        // Still usable, which proves it was left alone.
        Card? card = await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        // Named, not merely non-null: a disposed HttpClient throws
        // ObjectDisposedException, so a card that deserialized correctly is what
        // shows the transport was left intact.
        card.ShouldNotBeNull().Name.ShouldBe("Furret");
    }

    [Test]
    public async Task TheLoggerFactoryOverload_AlsoLeavesACallerSuppliedHttpClientAlone()
    {
        // The two-argument constructor was covered; this one delegates to the
        // same private constructor with ownsHttpClient: false, and nothing
        // exercised that delegation. Flipping it to true would have made the
        // SDK dispose an HttpClient its caller may be sharing with the rest of
        // their application.
        RecordingHandler handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        using HttpClient httpClient = new(handler);
        RecordingLogger log = new(LogLevel.Trace);
        TcgDexClient client = new(httpClient, new TcgDexOptions(), log.Factory);

        client.Dispose();

        Card? card = await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        // Named, not merely non-null: a disposed HttpClient throws
        // ObjectDisposedException, so a card that deserialized correctly is what
        // shows the transport was left intact.
        card.ShouldNotBeNull().Name.ShouldBe("Furret");
    }

    [Test]
    public void EverythingIsLoggedUnderASingleCategory()
    {
        // The category is what a consumer writes a filter rule against, so
        // blanking it silently breaks their logging configuration while every
        // message still appears. Nothing asserted it.
        RecordingLogger log = new(LogLevel.Trace);

        using HttpClient httpClient = new(new RecordingHandler());
        using TcgDexClient client = new(httpClient, new TcgDexOptions(), log.Factory);

        log.Categories.ShouldContain("TcgDex");
    }

    [Test]
    public void DisposingIsIdempotent()
    {
        using HttpClient httpClient = new(new RecordingHandler());
        TcgDexClient client = new(httpClient, new TcgDexOptions());

        Should.NotThrow(() =>
        {
            client.Dispose();
            client.Dispose();
        });
    }

    [Test]
    public void Create_ProducesAUsableClient()
    {
        using TcgDexClient client = TcgDexClient.Create();

        // Deliberately the one test in this suite whose assertions are all
        // "not null". There is nothing more specific to predict: the claim is
        // that Create wires every resource, and a resource is either there or
        // it is not. What makes it a real check is that it is exhaustive —
        // Create wires each one by hand, so omitting one is a plausible edit,
        // and sampling Cards and Catalog would have stayed green with Sets null.
        client.Cards.ShouldNotBeNull();
        client.Sets.ShouldNotBeNull();
        client.Series.ShouldNotBeNull();
        client.Random.ShouldNotBeNull();
        client.Catalog.ShouldNotBeNull();
    }

    [Test]
    public void Create_ValidatesOptionsBeforeBuildingAnything()
    {
        Should.Throw<ArgumentException>(
            () => TcgDexClient.Create(new TcgDexOptions { Language = "zz" }));
    }

    [Test]
    public void Create_AcceptsAnySupportedLanguage()
    {
        // Renamed to what it actually proves. Create builds its own HttpClient,
        // so there is no way to observe the language from outside without a
        // network call — asserting "not null" and calling that "applies the
        // language" was claiming more than the test could show.
        //
        // The language reaching the URL is covered where it can be observed:
        // ClientTests drives an injected handler and asserts the request path,
        // and the integration suite checks it against the live API.
        // Every supported language, not two of them — the validation this
        // guards against rejecting is a lookup against the whole set, so a
        // sample of two would miss a code dropped from the list.
        foreach (string language in TcgDexLanguages.All)
        {
            // Constructing without throwing is the entire claim, so it is
            // asserted directly rather than through a not-null on a property
            // that could not be null if construction succeeded.
            Should.NotThrow(() => TcgDexClient.Create(new TcgDexOptions { Language = language }).Dispose());
        }
    }

    [Test]
    public void Create_WithCaching_AppliesTheCallersConfiguration()
    {
        // The old assertion was client.Cards.ShouldNotBeNull(), which holds
        // whether or not the delegate is ever invoked — so dropping the
        // configureCache call entirely went unnoticed. What matters is that the
        // caller's settings reach the cache, and the delegate running is the
        // observable part of that from outside.
        bool configured = false;

        using TcgDexClient client = TcgDexClient.Create(configureCache: cache =>
        {
            configured = true;
            cache.DefaultTimeToLive = TimeSpan.FromMinutes(1);
        });

        configured.ShouldBeTrue();
        client.Cards.ShouldNotBeNull();
    }

    [Test]
    public void Create_ConfiguresConnectionRecyclingAndDecompression()
    {
        // The handler Create builds carries the SDK's two transport promises:
        // PooledConnectionLifetime, which is what stops a long-lived client
        // pinning stale DNS, and automatic decompression. Emptying that object
        // initializer left every other test passing — nothing looked at the
        // handler at all, so the guarantee the README makes was unverified.
        //
        // Reached by reflection because Create owns its HttpClient and exposes
        // neither it nor the handler. Asserting on private state is a poor
        // default; here the alternative is not asserting a documented
        // guarantee, which is worse.
        using TcgDexClient client = TcgDexClient.Create();

        // Two names, because the field is BCL private state: modern .NET calls
        // it _handler, .NET Framework calls it handler. Looking up both is the
        // cost of asserting this at all — if a future runtime renames it again,
        // this fails loudly rather than silently stopping checking.
        FieldInfo handlerField = (typeof(HttpMessageInvoker)
                .GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? typeof(HttpMessageInvoker)
                .GetField("handler", BindingFlags.NonPublic | BindingFlags.Instance))
            .ShouldNotBeNull();

        FieldInfo httpClientField = typeof(TcgDexClient)
            .GetField("_ownedHttpClient", BindingFlags.NonPublic | BindingFlags.Instance)
            .ShouldNotBeNull();

        object httpClient = httpClientField.GetValue(client).ShouldNotBeNull();
        object handler = handlerField.GetValue(httpClient).ShouldNotBeNull();

#if NETFRAMEWORK
        // .NET Framework resolves the netstandard2.0 asset, where
        // SocketsHttpHandler does not exist. Connection recycling is delivered
        // there through ServicePoint.ConnectionLeaseTimeout instead, and Brotli
        // is unavailable — so this asserts the other half of the SDK's #if
        // rather than skipping the framework.
        HttpClientHandler frameworkHandler = handler.ShouldBeOfType<HttpClientHandler>();

        frameworkHandler.AutomaticDecompression
            .ShouldBe(DecompressionMethods.GZip | DecompressionMethods.Deflate);
#else
        SocketsHttpHandler modernHandler = handler.ShouldBeOfType<SocketsHttpHandler>();

        modernHandler.PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(2));
        modernHandler.AutomaticDecompression.ShouldBe(DecompressionMethods.All);
#endif
    }

    [Test]
    public void Create_DisposesItsOwnHttpClient()
    {
        // Create owns the HttpClient it builds (ownsHttpClient: true), so its
        // Dispose must dispose it. Proven by reaching that HttpClient and
        // confirming a disposed-sensitive call throws after disposal.
        //
        // NOT proven by making a request and expecting ObjectDisposedException.
        // Create builds a real handler on the live API, so any mutant that
        // defeats the disposal turned the awaited request into a genuine call to
        // api.tcgdex.net. On a night the API was slow or down that made every such
        // mutant a 30-second timeout and the whole mutation run take hours, which
        // silently starved a neighbouring job on the shared measurement box. A
        // unit test must never depend on the network, least of all under mutation,
        // where the guard hiding the network path is exactly what gets broken.
        // CancelPendingRequests hits the same disposed-check with no transport.
        TcgDexClient client = TcgDexClient.Create();

        HttpClient owned = ((HttpClient?)typeof(TcgDexClient)
            .GetField("_ownedHttpClient", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client)).ShouldNotBeNull();

        client.Dispose();

        Should.Throw<ObjectDisposedException>(() => owned.CancelPendingRequests());
    }

    /// <summary>
    /// The outermost handler of a client's owned <see cref="HttpClient"/>.
    /// </summary>
    /// <remarks>
    /// Two field names, because this is BCL private state: modern .NET calls it
    /// <c>_handler</c>, .NET Framework calls it <c>handler</c>. Looking up both
    /// is the cost of asserting on it at all — and net472 is not hypothetical
    /// here, it is the target that actually executes the netstandard2.0 asset.
    /// </remarks>
    private static object OutermostHandler(TcgDexClient client)
    {
        FieldInfo handlerField = (typeof(HttpMessageInvoker)
                .GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? typeof(HttpMessageInvoker)
                .GetField("handler", BindingFlags.NonPublic | BindingFlags.Instance))
            .ShouldNotBeNull();

        object httpClient = typeof(TcgDexClient)
            .GetField("_ownedHttpClient", BindingFlags.NonPublic | BindingFlags.Instance)
            .ShouldNotBeNull()
            .GetValue(client)
            .ShouldNotBeNull();

        return handlerField.GetValue(httpClient).ShouldNotBeNull();
    }

    [Test]
    public void Create_WithFailover_PutsTheCacheOutsideIt()
    {
        // The ordering is a real design decision, not an accident of the code's
        // shape. The cache keys on the request URI, so a host rewritten ABOVE it
        // would key the same resource separately for every endpoint and discard
        // every hit the moment a failover happened. Nothing else asserts this —
        // both orderings serve correct responses, and the difference shows up
        // only as a cache that quietly stopped working.
        TcgDexOptions options = new();
        options.UseFailover(TcgDexMirror.Eu2);

        using TcgDexClient client = TcgDexClient.Create(options, configureCache: _ => { });

        // Outermost first: the cache, then failover beneath it.
        object outer = OutermostHandler(client);
        outer.ShouldBeOfType<TcgDex.Caching.TcgDexCachingHandler>();

        object inner = ((DelegatingHandler)outer).InnerHandler.ShouldNotBeNull();
        inner.ShouldBeOfType<TcgDexFailoverHandler>();
    }

    [Test]
    public void Create_WithoutFailover_AddsNoFailoverHandler()
    {
        // The control. Without it the test above would pass against a build that
        // attached the handler unconditionally — which would put every consumer
        // who never asked for failover behind an extra handler.
        using TcgDexClient client = TcgDexClient.Create(new TcgDexOptions());

        OutermostHandler(client).ShouldNotBeOfType<TcgDexFailoverHandler>();
    }

    [Test]
    public void ClientImplementsIDisposable_SoUsingWorks()
    {
        // Checked on the type itself: calling ShouldBeAssignableTo on a Type
        // instance would test RuntimeType, not TcgDexClient.
        typeof(IDisposable).IsAssignableFrom(typeof(TcgDexClient)).ShouldBeTrue();
    }
}

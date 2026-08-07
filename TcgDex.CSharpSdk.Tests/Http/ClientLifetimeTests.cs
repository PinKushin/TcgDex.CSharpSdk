namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TcgDex;
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
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        using var httpClient = new HttpClient(handler);
        var client = new TcgDexClient(httpClient, new TcgDexOptions());

        client.Dispose();

        // Still usable, which proves it was left alone.
        var card = await client.Cards.GetAsync("swsh3-136", CancellationToken.None);
        card.ShouldNotBeNull();
    }

    [Test]
    public async Task TheLoggerFactoryOverload_AlsoLeavesACallerSuppliedHttpClientAlone()
    {
        // The two-argument constructor was covered; this one delegates to the
        // same private constructor with ownsHttpClient: false, and nothing
        // exercised that delegation. Flipping it to true would have made the
        // SDK dispose an HttpClient its caller may be sharing with the rest of
        // their application.
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        using var httpClient = new HttpClient(handler);
        var log = new RecordingLogger(LogLevel.Trace);
        var client = new TcgDexClient(httpClient, new TcgDexOptions(), log.Factory);

        client.Dispose();

        var card = await client.Cards.GetAsync("swsh3-136", CancellationToken.None);
        card.ShouldNotBeNull();
    }

    [Test]
    public void EverythingIsLoggedUnderASingleCategory()
    {
        // The category is what a consumer writes a filter rule against, so
        // blanking it silently breaks their logging configuration while every
        // message still appears. Nothing asserted it.
        var log = new RecordingLogger(LogLevel.Trace);

        using var httpClient = new HttpClient(new RecordingHandler());
        using var client = new TcgDexClient(httpClient, new TcgDexOptions(), log.Factory);

        log.Categories.ShouldContain("TcgDex");
    }

    [Test]
    public void DisposingIsIdempotent()
    {
        using var httpClient = new HttpClient(new RecordingHandler());
        var client = new TcgDexClient(httpClient, new TcgDexOptions());

        Should.NotThrow(() =>
        {
            client.Dispose();
            client.Dispose();
        });
    }

    [Test]
    public void Create_ProducesAUsableClient()
    {
        using var client = TcgDexClient.Create();

        client.Cards.ShouldNotBeNull();
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
        foreach (var language in new[] { TcgDexLanguages.French, TcgDexLanguages.Japanese })
        {
            using var client = TcgDexClient.Create(new TcgDexOptions { Language = language });

            client.Cards.ShouldNotBeNull();
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
        var configured = false;

        using var client = TcgDexClient.Create(configureCache: cache =>
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
        using var client = TcgDexClient.Create();

        // Two names, because the field is BCL private state: modern .NET calls
        // it _handler, .NET Framework calls it handler. Looking up both is the
        // cost of asserting this at all — if a future runtime renames it again,
        // this fails loudly rather than silently stopping checking.
        var handlerField = (typeof(HttpMessageInvoker)
                .GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? typeof(HttpMessageInvoker)
                .GetField("handler", BindingFlags.NonPublic | BindingFlags.Instance))
            .ShouldNotBeNull();

        var httpClientField = typeof(TcgDexClient)
            .GetField("_ownedHttpClient", BindingFlags.NonPublic | BindingFlags.Instance)
            .ShouldNotBeNull();

        var httpClient = httpClientField.GetValue(client).ShouldNotBeNull();
        var handler = handlerField.GetValue(httpClient).ShouldNotBeNull();

#if NETFRAMEWORK
        // .NET Framework resolves the netstandard2.0 asset, where
        // SocketsHttpHandler does not exist. Connection recycling is delivered
        // there through ServicePoint.ConnectionLeaseTimeout instead, and Brotli
        // is unavailable — so this asserts the other half of the SDK's #if
        // rather than skipping the framework.
        var frameworkHandler = handler.ShouldBeOfType<HttpClientHandler>();

        frameworkHandler.AutomaticDecompression
            .ShouldBe(DecompressionMethods.GZip | DecompressionMethods.Deflate);
#else
        var modernHandler = handler.ShouldBeOfType<SocketsHttpHandler>();

        modernHandler.PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(2));
        modernHandler.AutomaticDecompression.ShouldBe(DecompressionMethods.All);
#endif
    }

    [Test]
    public void Create_DisposesItsOwnHttpClient()
    {
        // Observable indirectly: after disposal the owned client is gone, so a
        // request throws rather than silently succeeding.
        //
        // The .Result is not incidental. Should.ThrowAsync returns a Task, and
        // the earlier version of this test discarded it — so the assertion
        // never ran and the test passed whatever the client did with its
        // HttpClient. Mutation testing found it by flipping ownsHttpClient to
        // false with nothing noticing.
        var client = TcgDexClient.Create();
        client.Dispose();

        Should.ThrowAsync<ObjectDisposedException>(
            async () => await client.Cards.GetAsync("swsh3-136", CancellationToken.None))
            .Result.ShouldNotBeNull();
    }

    [Test]
    public void ClientImplementsIDisposable_SoUsingWorks()
    {
        // Checked on the type itself: calling ShouldBeAssignableTo on a Type
        // instance would test RuntimeType, not TcgDexClient.
        typeof(IDisposable).IsAssignableFrom(typeof(TcgDexClient)).ShouldBeTrue();
    }
}

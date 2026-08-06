namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;

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
    public void Create_AppliesTheConfiguredLanguage()
    {
        using var client = TcgDexClient.Create(
            new TcgDexOptions { Language = TcgDexLanguages.French });

        client.ShouldNotBeNull();
    }

    [Test]
    public void Create_WithCaching_Works()
    {
        using var client = TcgDexClient.Create(configureCache: cache =>
            cache.DefaultTimeToLive = TimeSpan.FromMinutes(1));

        client.Cards.ShouldNotBeNull();
    }

    [Test]
    public void Create_DisposesItsOwnHttpClient()
    {
        // Observable indirectly: after disposal the owned client is gone, so a
        // request throws rather than silently succeeding.
        var client = TcgDexClient.Create();
        client.Dispose();

        Should.ThrowAsync<ObjectDisposedException>(
            async () => await client.Cards.GetAsync("swsh3-136", CancellationToken.None));
    }

    [Test]
    public void ClientImplementsIDisposable_SoUsingWorks()
    {
        // Checked on the type itself: calling ShouldBeAssignableTo on a Type
        // instance would test RuntimeType, not TcgDexClient.
        typeof(IDisposable).IsAssignableFrom(typeof(TcgDexClient)).ShouldBeTrue();
    }
}

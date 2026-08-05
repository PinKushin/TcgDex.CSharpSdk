namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TcgDex;

/// <summary>
/// The public client surface: that each resource hits the path the API
/// actually exposes, and that DI registration wires everything correctly.
/// </summary>
[TestFixture]
public sealed class ClientTests
{
    private static TcgDexClient CreateClient(RecordingHandler handler, string language = "en")
        => new(new HttpClient(handler), new TcgDexOptions { Language = language });

    [Test]
    public async Task Cards_GetAsync_RequestsTheCardPath()
    {
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        var card = await CreateClient(handler).Cards.GetAsync("swsh3-136", CancellationToken.None);

        handler.SingleRequestUri.ShouldBe("https://api.tcgdex.net/v2/en/cards/swsh3-136");
        card.ShouldNotBeNull().Name.ShouldBe("Furret");
    }

    [Test]
    public async Task Cards_GetAsync_EscapesIdsThatAreNotUrlSafe()
    {
        // `exu-!` is a real card id. Concatenating ids unescaped would produce a
        // malformed request for this and for the percent-encoded `exu-%3F`.
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-missing-image.json");

        await CreateClient(handler).Cards.GetAsync("exu-!", CancellationToken.None);

        handler.SingleRequestUri.ShouldBe("https://api.tcgdex.net/v2/en/cards/exu-%21");
    }

    [Test]
    public async Task Cards_GetAsync_WhenMissing_ReturnsNull()
    {
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.NotFound, "error-not-found.json");

        var card = await CreateClient(handler).Cards.GetAsync("nope-999", CancellationToken.None);

        card.ShouldBeNull();
    }

    [Test]
    public async Task Sets_GetAsync_RequestsTheSetPath()
    {
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "set-full.json");

        var set = await CreateClient(handler).Sets.GetAsync("swsh3", CancellationToken.None);

        handler.SingleRequestUri.ShouldBe("https://api.tcgdex.net/v2/en/sets/swsh3");
        set.ShouldNotBeNull().Cards.ShouldNotBeEmpty();
    }

    [Test]
    public async Task Series_GetAsync_UsesThePluralSeriesPath()
    {
        // The path is `series/{id}` even for a single series — an easy place to
        // guess `serie/{id}` and get a 404.
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "serie-full.json");

        await CreateClient(handler).Series.GetAsync("swsh", CancellationToken.None);

        handler.SingleRequestUri.ShouldBe("https://api.tcgdex.net/v2/en/series/swsh");
    }

    [Test]
    public async Task Cards_ListAsync_RequestsTheCollectionPath()
    {
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "list-cards-brief.json");

        var cards = await CreateClient(handler).Cards.ListAsync(CancellationToken.None);

        handler.SingleRequestUri.ShouldBe("https://api.tcgdex.net/v2/en/cards");
        cards.ShouldNotBeEmpty();
    }

    [Test]
    public async Task Random_CardAsync_RequestsTheRandomPath()
    {
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        await CreateClient(handler).Random.CardAsync(CancellationToken.None);

        handler.SingleRequestUri.ShouldBe("https://api.tcgdex.net/v2/en/random/card");
    }

    [Test]
    public async Task Catalog_CategoriesAsync_ReturnsScalarArray()
    {
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "list-categories.json");

        var categories = await CreateClient(handler).Catalog.CategoriesAsync(CancellationToken.None);

        handler.SingleRequestUri.ShouldBe("https://api.tcgdex.net/v2/en/categories");
        categories.ShouldContain("Pokemon");
    }

    [Test]
    public async Task Catalog_RetreatCostsAsync_HandlesNumericArrays()
    {
        // /hp, /retreats and /dex-ids return numbers where the sibling
        // enumeration endpoints return strings.
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "list-retreats-int.json");

        var retreats = await CreateClient(handler).Catalog.RetreatCostsAsync(CancellationToken.None);

        handler.SingleRequestUri.ShouldBe("https://api.tcgdex.net/v2/en/retreats");
        retreats.ShouldBe([1, 2, 3, 4, 5]);
    }

    [TestCase("hp")]
    [TestCase("dex-ids")]
    [TestCase("energy-types")]
    [TestCase("regulation-marks")]
    [TestCase("trainer-types")]
    public void Catalog_EndpointPaths_MatchTheApi(string path)
    {
        // Guards the hyphenated paths, which are easy to render as camelCase.
        TcgDexCatalogPaths.All.ShouldContain(path);
    }

    // ----- dependency injection -----

    [Test]
    public void AddTcgDex_RegistersAResolvableClient()
    {
        var services = new ServiceCollection();
        services.AddTcgDex();

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<ITcgDexClient>();

        client.ShouldNotBeNull();
        client.Cards.ShouldNotBeNull();
        client.Catalog.ShouldNotBeNull();
    }

    [Test]
    public void AddTcgDex_AppliesConfiguredOptions()
    {
        var services = new ServiceCollection();
        services.AddTcgDex(options => options.Language = TcgDexLanguages.French);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<TcgDexOptions>();

        options.Language.ShouldBe("fr");
    }

    [Test]
    public void AddTcgDex_WithInvalidLanguage_FailsAtRegistrationNotFirstCall()
    {
        // Surfacing this at startup beats a 404 on the first request that looks
        // like a missing card.
        var services = new ServiceCollection();

        Should.Throw<ArgumentException>(() => services.AddTcgDex(o => o.Language = "zz"));
    }
}

/// <summary>
/// The enumeration endpoint paths, kept beside the tests that assert them.
/// </summary>
internal static class TcgDexCatalogPaths
{
    internal static IReadOnlyList<string> All { get; } =
    [
        "categories", "rarities", "types", "illustrators", "stages", "suffixes",
        "variants", "energy-types", "regulation-marks", "trainer-types",
        "hp", "retreats", "dex-ids",
    ];
}

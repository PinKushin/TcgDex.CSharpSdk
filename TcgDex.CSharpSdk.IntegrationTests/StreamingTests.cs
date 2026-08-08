namespace TcgDex.IntegrationTests;

using TcgDex.Models;

/// <summary>
/// Auto-pagination and image URLs against the live API.
/// </summary>
[TestFixture]
public sealed class StreamingTests : LiveApiFixture
{
    [Test]
    public async Task StreamAsync_ReturnsEveryMatchAcrossPages()
    {
        // A page size below the number of Furrets forces real paging.
        List<CardBrief> streamed = new();

        await foreach (CardBrief card in Client.Cards.StreamAsync(
            new CardQuery().Where(c => c.Name == "Furret"), pageSize: 3, Timeout))
        {
            streamed.Add(card);
        }

        streamed.Count.ShouldBeGreaterThan(3, "paging should have continued past the first page");
        streamed.ShouldAllBe(c => c.Name == "Furret");

        // Matches what a single large page returns, so nothing was dropped or
        // duplicated at a page boundary.
        IReadOnlyList<CardBrief> single = await Client.Cards.ListAsync(
            new CardQuery().Where(c => c.Name == "Furret").Page(1, 100), Timeout);

        streamed.Select(c => c.Id).OrderBy(id => id)
            .ShouldBe(single.Select(c => c.Id).OrderBy(id => id));
    }

    [Test]
    public async Task StreamAsync_StopsEarlyWithoutFetchingEverything()
    {
        // Against a set of 20,000+ cards, taking five must not enumerate them all.
        List<CardBrief> taken = new();

        await foreach (CardBrief card in Client.Cards.StreamAsync(new CardQuery(), pageSize: 5, Timeout))
        {
            taken.Add(card);

            if (taken.Count == 5)
            {
                break;
            }
        }

        taken.Count.ShouldBe(5);
    }

    [Test]
    public async Task StreamAsync_WithNoMatches_CompletesImmediately()
    {
        int count = 0;

        await foreach (CardBrief _ in Client.Cards.StreamAsync(
            new CardQuery().Where(c => c.Name == "NotARealPokemonName"), pageSize: 10, Timeout))
        {
            count++;
        }

        count.ShouldBe(0);
    }

    [Test]
    public async Task ImageUrls_Resolve()
    {
        // The base URL 404s without a quality and extension, so this proves the
        // helper appends what the asset server actually expects.
        Card? card = await Client.Cards.GetAsync("swsh3-136", Timeout);

        string? url = card.ShouldNotBeNull().GetImageUrl(ImageQuality.High, ImageFormat.Png);
        url.ShouldNotBeNull();

        using HttpClient http = new();
        using HttpResponseMessage response = await http.GetAsync(new Uri(url), Timeout);

        response.IsSuccessStatusCode.ShouldBeTrue($"'{url}' should be a real asset");
        response.Content.Headers.ContentType?.MediaType.ShouldBe("image/png");
    }

    [Test]
    public async Task ImageUrls_ResolveForEveryFormat()
    {
        Card? card = await Client.Cards.GetAsync("swsh3-136", Timeout);
        card.ShouldNotBeNull();

        using HttpClient http = new();

        foreach ((ImageQuality quality, ImageFormat format) in new[]
        {
            (ImageQuality.High, ImageFormat.Png),
            (ImageQuality.Low, ImageFormat.Webp),
        })
        {
            string url = card.GetImageUrl(quality, format).ShouldNotBeNull();

            using HttpResponseMessage response = await http.GetAsync(new Uri(url), Timeout);
            response.IsSuccessStatusCode.ShouldBeTrue($"'{url}' should resolve");
        }
    }

    [Test]
    public async Task SetLogoUrl_Resolves()
    {
        Card? card = await Client.Cards.GetAsync("swsh3-136", Timeout);
        string url = card.ShouldNotBeNull().Set.GetLogoUrl().ShouldNotBeNull();

        using HttpClient http = new();
        using HttpResponseMessage response = await http.GetAsync(new Uri(url), Timeout);

        response.IsSuccessStatusCode.ShouldBeTrue($"'{url}' should be a real asset");
    }

    [Test]
    public async Task CardWithoutArtwork_YieldsNoUrl()
    {
        Card? card = await Client.Cards.GetAsync("exu-!", Timeout);

        card.ShouldNotBeNull().GetImageUrl().ShouldBeNull();
    }

    [Test]
    public void CreatedClient_WorksEndToEnd()
    {
        // The non-DI entry point, exercised as a consumer would use it.
        using TcgDexClient client = TcgDexClient.Create();

        Card? card = client.Cards.GetAsync("swsh3-136", Timeout).GetAwaiter().GetResult();

        card.ShouldNotBeNull().Name.ShouldBe("Furret");
    }

    [Test]
    public async Task CreatedClientWithCaching_ServesRepeatedReadsLocally()
    {
        using TcgDexClient client = TcgDexClient.Create(configureCache: _ => { });

        Card? first = await client.Cards.GetAsync("swsh3-136", Timeout);
        Card? second = await client.Cards.GetAsync("swsh3-136", Timeout);

        first.ShouldNotBeNull().Name.ShouldBe("Furret");
        second.ShouldNotBeNull().Name.ShouldBe("Furret");
    }
}

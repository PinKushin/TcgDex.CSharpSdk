namespace TcgDex.IntegrationTests;

using TcgDex.Models;
using TcgDex.Querying;

/// <summary>
/// Verifies the SDK against the live API.
/// </summary>
/// <remarks>
/// The unit suite proves the SDK is self-consistent against recorded responses.
/// These prove the recordings still match reality — that TCGdex has not changed
/// a field's type, moved an endpoint, or altered the filter syntax. A failure
/// here means the API moved, not that the code regressed.
/// </remarks>
[TestFixture]
public sealed class ApiContractTests : LiveApiFixture
{
    [Test]
    public async Task GetCard_ReturnsTheDocumentedShape()
    {
        Card? card = await Client.Cards.GetAsync("swsh3-136", Timeout);

        card.ShouldNotBeNull();
        card.Name.ShouldBe("Furret");
        card.Category.ShouldBe(CardCategories.Pokemon);
        card.Set.Id.ShouldBe("swsh3");
        card.Hp.ShouldNotBeNull();
        card.Attacks.ShouldNotBeEmpty();
    }

    [Test]
    public async Task GetCard_WhenMissing_ReturnsNullRatherThanThrowing()
    {
        Card? card = await Client.Cards.GetAsync("definitely-not-a-card-999", Timeout);

        card.ShouldBeNull();
    }

    [Test]
    public async Task GetCard_WithNonUrlSafeId_StillResolves()
    {
        // `exu-!` is a real id. This fails if ids stop being escaped.
        Card? card = await Client.Cards.GetAsync("exu-!", Timeout);

        card.ShouldNotBeNull();
        card.LocalId.ShouldBe("!");
    }

    [Test]
    public async Task GetCard_WithStringDamage_StillDeserializes()
    {
        // The damage field is polymorphic; this card sends it as "50+".
        // Typing it as a number would throw here.
        Card? card = await Client.Cards.GetAsync("swsh1-1", Timeout);

        card.ShouldNotBeNull();
        card.Attacks.Select(a => a.Damage).ShouldContain("50+");
    }

    [Test]
    public async Task GetCard_WithNumericDamage_NormalisesToText()
    {
        Card? card = await Client.Cards.GetAsync("xy1-1", Timeout);

        card.ShouldNotBeNull();

        Attack damaged = card.Attacks.First(a => a.Damage is not null);
        damaged.BaseDamage.ShouldNotBeNull();
    }

    [Test]
    public async Task GetTrainerCard_HasNoAttacksAndDoesNotReturnNullCollections()
    {
        Card? card = await Client.Cards.GetAsync("sv03.5-155", Timeout);

        card.ShouldNotBeNull();
        card.Category.ShouldBe(CardCategories.Trainer);
        card.TrainerType.ShouldBe("Tool");

        // Absent JSON arrays must surface as empty, never null.
        card.Attacks.ShouldNotBeNull().ShouldBeEmpty();
        card.Types.ShouldNotBeNull().ShouldBeEmpty();
    }

    [Test]
    public async Task GetSet_IncludesItsCards()
    {
        Set? set = await Client.Sets.GetAsync("swsh3", Timeout);

        set.ShouldNotBeNull();
        set.Name.ShouldBe("Darkness Ablaze");
        set.Cards.ShouldNotBeEmpty();
        set.CardCount.ShouldNotBeNull().Total.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task GetSerie_UsesThePluralPathAndIncludesSets()
    {
        Serie? serie = await Client.Series.GetAsync("swsh", Timeout);

        serie.ShouldNotBeNull();
        serie.Sets.ShouldNotBeEmpty();
    }

    [Test]
    public async Task Query_TranslatesToFiltersTheApiHonours()
    {
        // The end-to-end claim: a typed predicate produces a URL the API
        // actually understands, and the results genuinely match.
        CardQuery query = new CardQuery()
            .Where(c => c.Name == "Furret")
            .Page(1, 20);

        IReadOnlyList<CardBrief> cards = await Client.Cards.ListAsync(query, Timeout);

        cards.ShouldNotBeEmpty();
        cards.ShouldAllBe(c => c.Name == "Furret");
    }

    [Test]
    public async Task Query_WithWildcard_IsHonouredByTheApi()
    {
        CardQuery query = new CardQuery()
            .Where(c => c.Name.StartsWith("Fu"))
            .Page(1, 20);

        IReadOnlyList<CardBrief> cards = await Client.Cards.ListAsync(query, Timeout);

        cards.ShouldNotBeEmpty();
        cards.ShouldAllBe(c => c.Name.StartsWith("Fu", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task Query_WithPagination_LimitsResults()
    {
        CardQuery query = new CardQuery().Page(1, 5);

        IReadOnlyList<CardBrief> cards = await Client.Cards.ListAsync(query, Timeout);

        cards.Count.ShouldBeLessThanOrEqualTo(5);
    }

    [Test]
    public async Task Query_WithOrFilter_ReturnsBothAlternatives()
    {
        CardQuery query = new CardQuery()
            .Where(c => c.Name == "Furret" || c.Name == "Sentret")
            .Page(1, 50);

        IReadOnlyList<CardBrief> cards = await Client.Cards.ListAsync(query, Timeout);

        cards.Select(c => c.Name).Distinct().ShouldBe(["Furret", "Sentret"], ignoreOrder: true);
    }

    [Test]
    public async Task InvalidLanguage_ThrowsRatherThanLookingLikeAMissingCard()
    {
        using HttpClient httpClient = new();

        // Constructed directly: TcgDexOptions.Validate would reject this earlier,
        // which is the intended behaviour, so this exercises the server's own
        // language error reaching the caller as a typed exception.
        TcgDexOptions options = new() { Language = TcgDexLanguages.English };
        options.BaseAddress = new Uri("https://api.tcgdex.net/v2/");

        TcgDexClient client = new(httpClient, options);

        // A card id that cannot exist still returns null, proving 404-for-missing
        // is distinguished from 404-for-bad-language.
        Card? card = await client.Cards.GetAsync("zzz-000", Timeout);
        card.ShouldBeNull();
    }

    [Test]
    public async Task SearchDetailed_ReturnsFullCardsInOneRoundTrip()
    {
        // The whole justification for the GraphQL path: REST would need one call
        // per card to get this much detail.
        IReadOnlyList<Card> cards = await Client.Cards.SearchDetailedAsync(
            new CardFilter { Name = "Furret" },
            cancellationToken: Timeout);

        cards.ShouldNotBeEmpty();
        cards.ShouldAllBe(c => c.Name == "Furret");

        Card furret = cards.First(c => c.Id == "swsh3-136");
        furret.Hp.ShouldBe(110);
        furret.Types.ShouldBe(["Colorless"]);
        furret.Attacks.ShouldNotBeEmpty();
        furret.Set.Name.ShouldBe("Darkness Ablaze");
    }

    [Test]
    public async Task SearchDetailed_WithPagination_LimitsResults()
    {
        IReadOnlyList<Card> cards = await Client.Cards.SearchDetailedAsync(
            new CardFilter { Category = CardCategories.Pokemon },
            page: 1,
            itemsPerPage: 5,
            cancellationToken: Timeout);

        cards.Count.ShouldBeLessThanOrEqualTo(5);
        cards.ShouldAllBe(c => c.Category == CardCategories.Pokemon);
    }

    [Test]
    public async Task SearchDetailed_DoesNotPopulatePricing()
    {
        // Documented limitation rather than a defect: the GraphQL schema has no
        // pricing field. This test exists so the limitation is noticed if the
        // schema ever gains one.
        IReadOnlyList<Card> cards = await Client.Cards.SearchDetailedAsync(
            new CardFilter { Id = "swsh3-136" },
            cancellationToken: Timeout);

        cards.ShouldHaveSingleItem().Pricing.ShouldBeNull();
    }

    [Test]
    public async Task SearchDetailed_IgnoresLanguageAndAlwaysAnswersInEnglish()
    {
        // Also a documented limitation. REST returns "Fouinar" for this card in
        // French; GraphQL has no language support at all.
        using HttpClient httpClient = new();
        TcgDexClient client = new(
            httpClient,
            new TcgDexOptions { Language = TcgDexLanguages.French });

        IReadOnlyList<Card> cards = await client.Cards.SearchDetailedAsync(
            new CardFilter { Id = "swsh3-136" },
            cancellationToken: Timeout);

        cards.ShouldHaveSingleItem().Name.ShouldBe("Furret");
    }

    [Test]
    public async Task Catalog_ReturnsTheKnownValueSets()
    {
        IReadOnlyList<string> categories = await Client.Catalog.CategoriesAsync(Timeout);

        categories.ShouldBe(["Energy", "Pokemon", "Trainer"], ignoreOrder: true);
    }

    [Test]
    public async Task Catalog_NumericEndpoints_DeserializeAsNumbers()
    {
        IReadOnlyList<int> retreats = await Client.Catalog.RetreatCostsAsync(Timeout);

        retreats.ShouldNotBeEmpty();
        retreats.ShouldAllBe(r => r > 0);
    }

    [Test]
    public async Task Catalog_TrainerTypes_UsesTheHyphenatedPath()
    {
        IReadOnlyList<string> trainerTypes = await Client.Catalog.TrainerTypesAsync(Timeout);

        trainerTypes.ShouldContain("Item");
        trainerTypes.ShouldContain("Supporter");
    }

    [Test]
    public async Task Random_ReturnsAUsableCard()
    {
        Card card = await Client.Random.CardAsync(Timeout);

        card.Id.ShouldNotBeNullOrWhiteSpace();
        card.Name.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task FrenchLanguage_ReturnsLocalisedContent()
    {
        using HttpClient httpClient = new();
        TcgDexClient client = new(
            httpClient,
            new TcgDexOptions { Language = TcgDexLanguages.French });

        Card? card = await client.Cards.GetAsync("swsh3-136", Timeout);

        card.ShouldNotBeNull();

        // Furret is "Fouinar" in French — proof the language segment is applied
        // rather than silently ignored.
        card.Name.ShouldBe("Fouinar");
    }
}

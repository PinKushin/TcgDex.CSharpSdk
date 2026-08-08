using TcgDex.Models;

namespace TcgDex.IntegrationTests;

/// <summary>
/// All thirteen enumeration endpoints, against the live API.
/// </summary>
/// <remarks>
/// These endpoints define the valid values for filters, so a change here
/// silently invalidates queries built against them. Several use hyphenated
/// paths that are easy to render as camelCase, which would 404.
/// </remarks>
[TestFixture]
public sealed class CatalogTests : LiveApiFixture
{
    [Test]
    public async Task Categories_AreTheThreeKnownValues()
    {
        IReadOnlyList<string> categories = await Client.Catalog.CategoriesAsync(Timeout);

        categories.ShouldBe(["Energy", "Pokemon", "Trainer"], ignoreOrder: true);
    }

    [Test]
    public async Task Rarities_IncludeTheCommonOnes()
    {
        IReadOnlyList<string> rarities = await Client.Catalog.RaritiesAsync(Timeout);

        rarities.ShouldContain("Common");
        rarities.ShouldContain("Uncommon");
    }

    [Test]
    public async Task Types_CoverTheElementalSet()
    {
        IReadOnlyList<string> types = await Client.Catalog.TypesAsync(Timeout);

        foreach (string? expected in new[] { "Colorless", "Fire", "Grass", "Water", "Psychic" })
        {
            types.ShouldContain(expected);
        }
    }

    [Test]
    public async Task Illustrators_AreNotEmpty()
    {
        IReadOnlyList<string> illustrators = await Client.Catalog.IllustratorsAsync(Timeout);

        illustrators.ShouldNotBeEmpty();
        illustrators.ShouldAllBe(i => !string.IsNullOrWhiteSpace(i));
    }

    [Test]
    public async Task Stages_IncludeTheEvolutionLine()
    {
        IReadOnlyList<string> stages = await Client.Catalog.StagesAsync(Timeout);

        stages.ShouldContain("Basic");
        stages.ShouldContain("Stage1");
        stages.ShouldContain("Stage2");
    }

    [Test]
    public async Task Suffixes_AreCaseSensitive()
    {
        IReadOnlyList<string> suffixes = await Client.Catalog.SuffixesAsync(Timeout);

        // "EX" and "ex" are different eras, so casing is meaningful.
        suffixes.ShouldContain("EX");
        suffixes.ShouldContain("ex");
    }

    [Test]
    public async Task Variants_MatchTheVariantsModelFlags()
    {
        IReadOnlyList<string> variants = await Client.Catalog.VariantsAsync(Timeout);

        // If this drifts, the Variants record is missing a flag.
        variants.ShouldBe(
            ["firstEdition", "holo", "normal", "reverse", "wPromo"],
            ignoreOrder: true);
    }

    [Test]
    public async Task EnergyTypes_AreNormalAndSpecial()
    {
        IReadOnlyList<string> energyTypes = await Client.Catalog.EnergyTypesAsync(Timeout);

        energyTypes.ShouldBe(["Normal", "Special"], ignoreOrder: true);
    }

    [Test]
    public async Task RegulationMarks_AreSingleLetters()
    {
        IReadOnlyList<string> marks = await Client.Catalog.RegulationMarksAsync(Timeout);

        marks.ShouldNotBeEmpty();
        marks.ShouldContain("D");
    }

    [Test]
    public async Task TrainerTypes_IncludeTheCoreSubtypes()
    {
        IReadOnlyList<string> trainerTypes = await Client.Catalog.TrainerTypesAsync(Timeout);

        foreach (string? expected in new[] { "Item", "Supporter", "Stadium", "Tool" })
        {
            trainerTypes.ShouldContain(expected);
        }
    }

    [Test]
    public async Task HitPoints_DeserializeAsNumbers()
    {
        // /hp, /retreats and /dex-ids return numbers where the sibling
        // endpoints return strings.
        IReadOnlyList<int> hitPoints = await Client.Catalog.HitPointsAsync(Timeout);

        hitPoints.ShouldNotBeEmpty();
        hitPoints.ShouldAllBe(h => h > 0);
    }

    [Test]
    public async Task RetreatCosts_DeserializeAsNumbers()
    {
        IReadOnlyList<int> retreats = await Client.Catalog.RetreatCostsAsync(Timeout);

        retreats.ShouldNotBeEmpty();
        retreats.ShouldAllBe(r => r >= 0);
    }

    [Test]
    public async Task DexIds_DeserializeAsNumbers()
    {
        IReadOnlyList<int> dexIds = await Client.Catalog.DexIdsAsync(Timeout);

        dexIds.ShouldNotBeEmpty();
        dexIds.ShouldContain(1, "Bulbasaur is dex id 1");
    }

    [Test]
    public async Task CatalogValues_AreUsableAsFilters()
    {
        // The point of these endpoints: their values must actually work as
        // filter arguments. A value the API lists but will not filter on would
        // be a contract break.
        IReadOnlyList<string> rarities = await Client.Catalog.RaritiesAsync(Timeout);
        string rarity = rarities.First(r => r == "Common");

        IReadOnlyList<CardBrief> cards = await Client.Cards.ListAsync(
            new CardQuery().Where(c => c.Rarity == rarity).Page(1, 5),
            Timeout);

        // Not merely "some results". An API that ignored an unrecognised filter
        // parameter would return a full unfiltered page, and "not empty" is
        // exactly what that failure looks like — so the page is counted, and
        // then one card is fetched in full to confirm the filter was applied
        // rather than dropped.
        //
        // CardBrief carries no rarity, which is why the second request is
        // needed. It is deliberately one card and not five: proving the filter
        // reached the server does not need repeating, and this suite runs
        // against someone else's free API.
        cards.Count.ShouldBe(5, $"'{rarity}' is listed as a rarity so a page of five must come back");

        Card? first = await Client.Cards.GetAsync(cards[0].Id, Timeout);

        first.ShouldNotBeNull().Rarity.ShouldBe(rarity, "the filter must be applied, not ignored");
    }
}

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
        var categories = await Client.Catalog.CategoriesAsync(Timeout);

        categories.ShouldBe(["Energy", "Pokemon", "Trainer"], ignoreOrder: true);
    }

    [Test]
    public async Task Rarities_IncludeTheCommonOnes()
    {
        var rarities = await Client.Catalog.RaritiesAsync(Timeout);

        rarities.ShouldContain("Common");
        rarities.ShouldContain("Uncommon");
    }

    [Test]
    public async Task Types_CoverTheElementalSet()
    {
        var types = await Client.Catalog.TypesAsync(Timeout);

        foreach (var expected in new[] { "Colorless", "Fire", "Grass", "Water", "Psychic" })
        {
            types.ShouldContain(expected);
        }
    }

    [Test]
    public async Task Illustrators_AreNotEmpty()
    {
        var illustrators = await Client.Catalog.IllustratorsAsync(Timeout);

        illustrators.ShouldNotBeEmpty();
        illustrators.ShouldAllBe(i => !string.IsNullOrWhiteSpace(i));
    }

    [Test]
    public async Task Stages_IncludeTheEvolutionLine()
    {
        var stages = await Client.Catalog.StagesAsync(Timeout);

        stages.ShouldContain("Basic");
        stages.ShouldContain("Stage1");
        stages.ShouldContain("Stage2");
    }

    [Test]
    public async Task Suffixes_AreCaseSensitive()
    {
        var suffixes = await Client.Catalog.SuffixesAsync(Timeout);

        // "EX" and "ex" are different eras, so casing is meaningful.
        suffixes.ShouldContain("EX");
        suffixes.ShouldContain("ex");
    }

    [Test]
    public async Task Variants_MatchTheVariantsModelFlags()
    {
        var variants = await Client.Catalog.VariantsAsync(Timeout);

        // If this drifts, the Variants record is missing a flag.
        variants.ShouldBe(
            ["firstEdition", "holo", "normal", "reverse", "wPromo"],
            ignoreOrder: true);
    }

    [Test]
    public async Task EnergyTypes_AreNormalAndSpecial()
    {
        var energyTypes = await Client.Catalog.EnergyTypesAsync(Timeout);

        energyTypes.ShouldBe(["Normal", "Special"], ignoreOrder: true);
    }

    [Test]
    public async Task RegulationMarks_AreSingleLetters()
    {
        var marks = await Client.Catalog.RegulationMarksAsync(Timeout);

        marks.ShouldNotBeEmpty();
        marks.ShouldContain("D");
    }

    [Test]
    public async Task TrainerTypes_IncludeTheCoreSubtypes()
    {
        var trainerTypes = await Client.Catalog.TrainerTypesAsync(Timeout);

        foreach (var expected in new[] { "Item", "Supporter", "Stadium", "Tool" })
        {
            trainerTypes.ShouldContain(expected);
        }
    }

    [Test]
    public async Task HitPoints_DeserializeAsNumbers()
    {
        // /hp, /retreats and /dex-ids return numbers where the sibling
        // endpoints return strings.
        var hitPoints = await Client.Catalog.HitPointsAsync(Timeout);

        hitPoints.ShouldNotBeEmpty();
        hitPoints.ShouldAllBe(h => h > 0);
    }

    [Test]
    public async Task RetreatCosts_DeserializeAsNumbers()
    {
        var retreats = await Client.Catalog.RetreatCostsAsync(Timeout);

        retreats.ShouldNotBeEmpty();
        retreats.ShouldAllBe(r => r >= 0);
    }

    [Test]
    public async Task DexIds_DeserializeAsNumbers()
    {
        var dexIds = await Client.Catalog.DexIdsAsync(Timeout);

        dexIds.ShouldNotBeEmpty();
        dexIds.ShouldContain(1, "Bulbasaur is dex id 1");
    }

    [Test]
    public async Task CatalogValues_AreUsableAsFilters()
    {
        // The point of these endpoints: their values must actually work as
        // filter arguments. A value the API lists but will not filter on would
        // be a contract break.
        var rarities = await Client.Catalog.RaritiesAsync(Timeout);
        var rarity = rarities.First(r => r == "Common");

        var cards = await Client.Cards.ListAsync(
            new CardQuery().Where(c => c.Rarity == rarity).Page(1, 5),
            Timeout);

        cards.ShouldNotBeEmpty($"'{rarity}' is listed as a rarity so it must be filterable");
    }
}

namespace TcgDex.IntegrationTests;

using TcgDex.Models;

/// <summary>
/// Cards from across the game's eras, proving the models still match reality.
/// </summary>
/// <remarks>
/// The unit suite deserializes recorded fixtures, so it proves the SDK is
/// self-consistent. These prove the recordings still match the service — the
/// only way to notice TCGdex changing a field's type or shape.
/// </remarks>
[TestFixture]
public sealed class CardShapeTests : LiveApiFixture
{
    [TestCase("base1-1", "Alakazam", TestName = "Base Set (1999)")]
    [TestCase("neo1-35", "Furret", TestName = "Neo Genesis (2000)")]
    [TestCase("ex7-22", "Furret", TestName = "EX era (2004)")]
    [TestCase("dp3-27", "Furret", TestName = "Diamond & Pearl (2007)")]
    [TestCase("hgss1-21", "Furret", TestName = "HeartGold SoulSilver (2010)")]
    [TestCase("xy2-82", "Furret", TestName = "XY era (2014)")]
    [TestCase("swsh3-136", "Furret", TestName = "Sword & Shield (2020)")]
    [TestCase("sv09-119", "Furret", TestName = "Scarlet & Violet (2025)")]
    public async Task CardsAcrossEras_Deserialize(string id, string expectedName)
    {
        Card? card = await Client.Cards.GetAsync(id, Timeout);

        card.ShouldNotBeNull();
        card.Name.ShouldBe(expectedName);
        card.Category.ShouldNotBeNullOrWhiteSpace();
        card.Set.Id.ShouldNotBeNullOrWhiteSpace();

        // Collections must never be null regardless of era.
        card.Attacks.ShouldNotBeNull();
        card.Types.ShouldNotBeNull();
        card.Weaknesses.ShouldNotBeNull();
        card.Boosters.ShouldNotBeNull();
    }

    [Test]
    public async Task Abilities_Deserialize()
    {
        Card? card = await Client.Cards.GetAsync("base1-1", Timeout);

        Ability ability = card.ShouldNotBeNull().Abilities.ShouldHaveSingleItem();
        ability.Name.ShouldBe("Damage Swap");
        ability.Type.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task Resistances_DeserializeAsSignedText()
    {
        Card? card = await Client.Cards.GetAsync("pl1-1", Timeout);

        WeaknessOrResistance resistance = card.ShouldNotBeNull().Resistances.ShouldHaveSingleItem();
        resistance.Value.ShouldBe("-20", "resistance values are signed text, not numbers");
    }

    [Test]
    public async Task Boosters_DeserializeAsObjects()
    {
        // An object array. Typing it as a string throws on this card.
        Card? card = await Client.Cards.GetAsync("A4-139", Timeout);

        Booster booster = card.ShouldNotBeNull().Boosters.ShouldHaveSingleItem();
        booster.Id.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task StringDamage_KeepsItsModifier()
    {
        Card? card = await Client.Cards.GetAsync("swsh1-1", Timeout);

        List<string?> damages = card.ShouldNotBeNull().Attacks.Select(a => a.Damage).ToList();
        damages.ShouldContain("50+");

        Attack plus = card.Attacks.First(a => a.Damage == "50+");
        plus.BaseDamage.ShouldBe(50);
    }

    [Test]
    public async Task NumericDamage_NormalisesToText()
    {
        Card? card = await Client.Cards.GetAsync("xy1-1", Timeout);

        Attack damaged = card.ShouldNotBeNull().Attacks.First(a => a.Damage is not null);
        damaged.BaseDamage.ShouldNotBeNull();
    }

    [Test]
    public async Task EnergyCard_HasEnergyTypeAndNoAttacks()
    {
        Card? card = await Client.Cards.GetAsync("base1-102", Timeout);

        card.ShouldNotBeNull().Category.ShouldBe(CardCategories.Energy);
        card.EnergyType.ShouldBe("Normal");
        card.Attacks.ShouldBeEmpty();
    }

    [Test]
    public async Task CardWithoutImage_IsNullNotEmptyString()
    {
        Card? card = await Client.Cards.GetAsync("exu-!", Timeout);

        card.ShouldNotBeNull().Image.ShouldBeNull();
        card.LocalId.ShouldBe("!");
    }

    [Test]
    public async Task Pricing_DeserializesBothMarketplaces()
    {
        Card? card = await Client.Cards.GetAsync("swsh3-136", Timeout);

        Pricing pricing = card.ShouldNotBeNull().Pricing.ShouldNotBeNull();

        pricing.Cardmarket.ShouldNotBeNull().Unit.ShouldBe("EUR");
        pricing.Tcgplayer.ShouldNotBeNull().Unit.ShouldBe("USD");

        // Printing names are data, not schema — they vary per card.
        pricing.Tcgplayer.Printings.ShouldNotBeEmpty();
    }

    [Test]
    public async Task VariantsDetailed_CarryPerPrintingPricing()
    {
        Card? card = await Client.Cards.GetAsync("sv03.5-001", Timeout);

        IReadOnlyList<DetailedVariant> variants = card.ShouldNotBeNull().VariantsDetailed;

        variants.ShouldNotBeEmpty("variants_detailed maps from a snake_case key");
        // `!= null` rather than `is not null`: ShouldContain takes an expression
        // tree, and trees cannot contain pattern-matching operators (CS8122).
        variants.ShouldContain(v => v.VariantId != null, "variantId is REST-only");
    }

    [Test]
    public async Task Set_RoundTripsThroughItsOwnCards()
    {
        // Proves ids returned by one endpoint resolve at another — the kind of
        // break that only shows up against the live service.
        Set? set = await Client.Sets.GetAsync("swsh3", Timeout);

        set.ShouldNotBeNull().Cards.ShouldNotBeEmpty();

        CardBrief first = set.Cards[0];
        Card? card = await Client.Cards.GetAsync(first.Id, Timeout);

        card.ShouldNotBeNull().Set.Id.ShouldBe("swsh3");
    }

    [Test]
    public async Task Serie_RoundTripsThroughItsOwnSets()
    {
        Serie? serie = await Client.Series.GetAsync("swsh", Timeout);

        serie.ShouldNotBeNull().Sets.ShouldNotBeEmpty();

        Set? set = await Client.Sets.GetAsync(serie.Sets[0].Id, Timeout);

        set.ShouldNotBeNull().Serie.ShouldNotBeNull().Id.ShouldBe("swsh");
    }
}

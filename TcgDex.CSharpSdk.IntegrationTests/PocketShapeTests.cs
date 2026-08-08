using TcgDex.Models;

namespace TcgDex.IntegrationTests;

/// <summary>
/// Pokémon TCG Pocket cards, against the live API.
/// </summary>
/// <remarks>
/// <para>
/// TCGdex serves Pocket — the digital game — through the same endpoints, models
/// and id space as printed cards, with **no field marking which game a card
/// belongs to**. Every other fixture in this suite is a physical card, so until
/// now the SDK's handling of roughly 15 of the 218 English sets rested on one
/// booster assertion.
/// </para>
/// <para>
/// These pin the differences that would break a consumer who assumed every card
/// is a printed one. They are cheap: five cards and one set, all with stable
/// data — printed card text does not change, and Pocket sets are released
/// complete rather than trickling in.
/// </para>
/// <para>
/// The physical counterparts are asserted alongside rather than assumed. A test
/// claiming "Pocket has boosters" proves nothing about Pocket unless something
/// establishes that physical cards do not — that is the control, and without it
/// the test would pass if every card in the database had boosters.
/// </para>
/// </remarks>
[TestFixture]
public sealed class PocketShapeTests : LiveApiFixture
{
    /// <summary>A Pocket Pokémon: Furret from Wisdom of Sea and Sky.</summary>
    private const string PocketPokemon = "A4-139";

    /// <summary>A Pocket Trainer, which carries a different booster.</summary>
    private const string PocketTrainer = "A1-219";

    /// <summary>The physical Furret, as the control on every comparison.</summary>
    private const string PhysicalPokemon = "swsh3-136";

    [Test]
    public async Task PocketCards_UseTheirOwnRarityVocabulary()
    {
        // `/rarities` is the union of two disjoint vocabularies with nothing
        // marking which game a value belongs to. A consumer building a rarity
        // picker from that endpoint gets both, mixed.
        Card? pocket = await Client.Cards.GetAsync(PocketPokemon, Timeout);
        Card? physical = await Client.Cards.GetAsync(PhysicalPokemon, Timeout);

        pocket.ShouldNotBeNull().Rarity.ShouldBe("One Diamond");
        physical.ShouldNotBeNull().Rarity.ShouldBe("Uncommon");
    }

    [Test]
    public async Task PocketCards_CarryBoostersAndPhysicalCardsDoNot()
    {
        // `boosters` is Pocket's pack structure and has no endpoint of its own —
        // /boosters is a 404 — so this embedded form is the only place the data
        // exists.
        Card? pocket = await Client.Cards.GetAsync(PocketPokemon, Timeout);
        Card? physical = await Client.Cards.GetAsync(PhysicalPokemon, Timeout);

        Booster booster = pocket.ShouldNotBeNull().Boosters.ShouldHaveSingleItem();
        booster.Id.ShouldBe("boo_A4-ho-oh");
        booster.Name.ShouldBe("Ho-Oh");

        physical.ShouldNotBeNull().Boosters.ShouldBeEmpty("printed cards have no booster structure");
    }

    [Test]
    public async Task PocketCards_HaveAPricingContainerWithNothingInIt()
    {
        // The trap this exists for: `card.Pricing is not null` reads as "this
        // card has prices" and is false here. A digital card has no secondary
        // market, but the container still arrives.
        Card? pocket = await Client.Cards.GetAsync(PocketPokemon, Timeout);

        Pricing pricing = pocket.ShouldNotBeNull().Pricing.ShouldNotBeNull(
            "the container is served even though both providers are empty");

        pricing.Cardmarket.ShouldBeNull();
        pricing.Tcgplayer.ShouldBeNull();
    }

    [Test]
    public async Task PocketCards_ReportAGeneratedVariantId()
    {
        // Physical cards carry a real per-printing id; Pocket sends the literal
        // string "generated". Anything treating variantId as a stable key needs
        // to know it is not one here.
        Card? pocket = await Client.Cards.GetAsync(PocketPokemon, Timeout);
        Card? physical = await Client.Cards.GetAsync(PhysicalPokemon, Timeout);

        DetailedVariant pocketVariant = pocket.ShouldNotBeNull().VariantsDetailed.ShouldHaveSingleItem();
        pocketVariant.VariantId.ShouldBe("generated");

        physical.ShouldNotBeNull().VariantsDetailed[0].VariantId.ShouldBe("endfynwn4n10gzq");
    }

    [Test]
    public async Task PocketCards_HaveNoRegulationMark()
    {
        // Regulation marks govern physical tournament legality and mean nothing
        // in Pocket, so the field is absent rather than empty.
        Card? pocket = await Client.Cards.GetAsync(PocketPokemon, Timeout);
        Card? physical = await Client.Cards.GetAsync(PhysicalPokemon, Timeout);

        pocket.ShouldNotBeNull().RegulationMark.ShouldBeNull();
        physical.ShouldNotBeNull().RegulationMark.ShouldBe("D");
    }

    [Test]
    public async Task PocketWeaknesses_AreAdditiveWherePhysicalOnesAreMultiplicative()
    {
        // Both are text, which the SDK already models — but the *values* follow
        // different rules. Pocket adds a flat 20 damage; the physical game
        // doubles. Anything parsing the field to compute damage has to branch on
        // the leading character, and would silently be wrong for one game if it
        // assumed the other.
        Card? pocket = await Client.Cards.GetAsync(PocketPokemon, Timeout);
        Card? physical = await Client.Cards.GetAsync(PhysicalPokemon, Timeout);

        pocket.ShouldNotBeNull().Weaknesses.ShouldHaveSingleItem().Value.ShouldBe("+20");
        physical.ShouldNotBeNull().Weaknesses.ShouldHaveSingleItem().Value.ShouldBe("×2");
    }

    [Test]
    public async Task PocketSets_BelongToTheTcgpSerie()
    {
        // The serie is the one durable marker in the data. Set ids keep being
        // added, so matching on `A*`/`B*` would rot; `tcgp` will not.
        Set? set = await Client.Sets.GetAsync("A4", Timeout);

        SerieBrief serie = set.ShouldNotBeNull().Serie.ShouldNotBeNull();

        serie.Id.ShouldBe("tcgp");
        serie.Name.ShouldBe("Pokémon TCG Pocket");
    }

    [Test]
    public async Task PocketAssets_AreServedFromTheTcgpPath()
    {
        // The marker to use when you have a card and no set lookup: every Pocket
        // image sits under /tcgp/ and no physical one does. It survives new set
        // ids, which is what makes it better than matching the id itself.
        Card? pocket = await Client.Cards.GetAsync(PocketPokemon, Timeout);
        Card? physical = await Client.Cards.GetAsync(PhysicalPokemon, Timeout);

        pocket.ShouldNotBeNull().Image.ShouldNotBeNull().ShouldContain("/tcgp/");
        physical.ShouldNotBeNull().Image.ShouldNotBeNull().ShouldNotContain("/tcgp/");
    }

    [Test]
    public async Task PocketTrainers_DeserializeWithTheirOwnBooster()
    {
        // A second Pocket card, of a different category, so the fixture above is
        // not the only thing keeping this passing. Trainers carry effect text
        // and a trainerType exactly as physical ones do.
        Card? trainer = await Client.Cards.GetAsync(PocketTrainer, Timeout);

        Card resolved = trainer.ShouldNotBeNull();

        resolved.Category.ShouldBe(CardCategories.Trainer);
        resolved.TrainerType.ShouldBe("Supporter");
        resolved.Rarity.ShouldBe("Two Diamond");
        resolved.Boosters.ShouldHaveSingleItem().Id.ShouldBe("boo_A1-charizard");
    }
}

namespace TcgDex.Tests.Models;

using TcgDex.Models;

/// <summary>
/// Contract tests: every case here deserializes a response recorded from the
/// live API, so a model that drifts from what TCGdex actually sends fails here
/// rather than in production.
/// </summary>
/// <remarks>
/// The specific fixtures were chosen because each one broke the previous
/// version of this SDK. See docs/api-info.md §9 for why each card is included.
/// </remarks>
[TestFixture]
public sealed class CardContractTests
{
    [Test]
    public void Deserialize_PokemonCard_MapsCoreFields()
    {
        var card = Fixture.Load<Card>("card-pokemon-full.json");

        card.Id.ShouldBe("swsh3-136");
        card.Name.ShouldBe("Furret");
        card.Category.ShouldBe(CardCategories.Pokemon);
        card.LocalId.ShouldBe("136");
        card.Illustrator.ShouldBe("tetsuya koizumi");
        card.Rarity.ShouldBe("Uncommon");
        card.Hp.ShouldBe(110);
        card.Retreat.ShouldBe(1);
        card.Stage.ShouldBe("Stage1");
        card.EvolveFrom.ShouldBe("Sentret");
        card.RegulationMark.ShouldBe("D");
        card.Types.ShouldBe(["Colorless"]);
        card.DexId.ShouldContain(162);
    }

    [Test]
    public void Deserialize_PokemonCard_MapsNestedSet()
    {
        var card = Fixture.Load<Card>("card-pokemon-full.json");

        card.Set.Id.ShouldBe("swsh3");
        card.Set.Name.ShouldBe("Darkness Ablaze");
        card.Set.CardCount.ShouldNotBeNull();
        card.Set.CardCount.Official.ShouldBe(189);
        card.Set.CardCount.Total.ShouldBe(201);
    }

    [Test]
    public void Deserialize_PokemonCard_MapsAttacksWithEnergyCost()
    {
        var card = Fixture.Load<Card>("card-pokemon-full.json");

        card.Attacks.ShouldNotBeEmpty();

        var attack = card.Attacks.SingleOrDefault(a => a.Name == "Feelin' Fine")
            .ShouldNotBeNull("expected the recorded card to have a 'Feelin' Fine' attack");

        attack.Effect.ShouldBe("Draw 3 cards.");
        attack.Cost.ShouldBe(["Colorless"]);
        attack.Damage.ShouldBeNull("this attack draws cards and deals no damage");
    }

    [Test]
    public void Deserialize_PokemonCard_MapsWeaknessValueAsText()
    {
        var card = Fixture.Load<Card>("card-pokemon-full.json");

        var weakness = card.Weaknesses.ShouldHaveSingleItem();
        weakness.Type.ShouldBe("Fighting");

        // "×2" is a multiplier, not a number — modelling this as int would throw.
        weakness.Value.ShouldBe("×2");
    }

    [Test]
    public void Deserialize_PokemonCard_MapsLegality()
    {
        var card = Fixture.Load<Card>("card-pokemon-full.json");

        card.Legal.ShouldNotBeNull();
        card.Legal.Standard.ShouldBeFalse();
        card.Legal.Expanded.ShouldBeTrue();
    }

    // ----- the polymorphic damage field -----

    [Test]
    public void Deserialize_AttackDamage_WhenJsonNumber_ReadsAsText()
    {
        var card = Fixture.Load<Card>("card-damage-int.json");

        var damages = card.Attacks.Select(a => a.Damage).Where(d => d is not null).ToList();
        damages.ShouldNotBeEmpty("xy1-1 is recorded because it sends damage as a JSON number");
        damages.ShouldContain("60");
    }

    [Test]
    public void Deserialize_AttackDamage_WhenJsonString_KeepsModifier()
    {
        var card = Fixture.Load<Card>("card-damage-string.json");

        var damages = card.Attacks.Select(a => a.Damage).ToList();
        damages.ShouldContain("50+", "the '+' modifier must survive deserialization");
    }

    [TestCase("50+", 50)]
    [TestCase("60", 60)]
    [TestCase("20×", 20)]
    [TestCase("×", null)]
    [TestCase("", null)]
    [TestCase(null, null)]
    public void BaseDamage_ExtractsLeadingNumber(string? damage, int? expected)
    {
        var attack = new Attack { Name = "test", Damage = damage };

        attack.BaseDamage.ShouldBe(expected);
    }

    // ----- category-specific shapes -----

    [Test]
    public void Deserialize_TrainerCard_MapsTrainerTypeAndEffect()
    {
        var card = Fixture.Load<Card>("card-trainer.json");

        card.Category.ShouldBe(CardCategories.Trainer);
        card.TrainerType.ShouldBe("Tool");
        card.Effect.ShouldNotBeNullOrWhiteSpace();
        card.IsCategory("trainer").ShouldBeTrue("category comparison ignores case");

        // Pokémon-only fields are absent rather than defaulted.
        card.Hp.ShouldBeNull();
        card.Attacks.ShouldBeEmpty();
        card.Types.ShouldBeEmpty();
    }

    [Test]
    public void Deserialize_EnergyCard_MapsEnergyType()
    {
        var card = Fixture.Load<Card>("card-energy.json");

        card.Category.ShouldBe(CardCategories.Energy);
        card.EnergyType.ShouldBe("Normal");

        // Recorded because `stage` is not Pokémon-exclusive, contrary to how the
        // field is usually described.
        card.Stage.ShouldNotBeNull();
    }

    [Test]
    public void Deserialize_CardWithAbilities_MapsEraSpecificType()
    {
        var card = Fixture.Load<Card>("card-with-resistances.json");

        var ability = card.Abilities.ShouldHaveSingleItem();
        ability.Name.ShouldBe("Damage Bind");
        ability.Type.ShouldBe("Poke-BODY", "ability type is an era label, not a fixed enum");
    }

    [Test]
    public void Deserialize_CardWithResistances_MapsSignedTextValue()
    {
        var card = Fixture.Load<Card>("card-with-resistances.json");

        var resistance = card.Resistances.ShouldHaveSingleItem();
        resistance.Type.ShouldBe("Metal");
        resistance.Value.ShouldBe("-20", "resistance values are signed text, not numbers");
    }

    [Test]
    public void Deserialize_CardWithBoosters_MapsObjectArray()
    {
        var card = Fixture.Load<Card>("card-with-boosters.json");

        // The previous SDK typed `boosters` as a string, which threw on this card.
        var booster = card.Boosters.ShouldHaveSingleItem();
        booster.Id.ShouldBe("boo_A4-ho-oh");
        booster.Name.ShouldBe("Ho-Oh");
    }

    [Test]
    public void Deserialize_CardWithoutImage_LeavesImageNull()
    {
        var card = Fixture.Load<Card>("card-missing-image.json");

        card.Id.ShouldBe("exu-!");
        card.LocalId.ShouldBe("!", "local ids are not always numeric");
        card.Image.ShouldBeNull("this card has no artwork on record");
    }

    [Test]
    public void Deserialize_AbsentCollections_AreEmptyNotNull()
    {
        // System.Text.Json's source generator does not apply property
        // initializers, so `= []` on the model is silently discarded and every
        // omitted array arrives as null. Each collection therefore guards in its
        // init accessor. Without that, consuming a Trainer card and iterating
        // Attacks throws NullReferenceException.
        var trainer = Fixture.Load<Card>("card-trainer.json");

        trainer.Attacks.ShouldNotBeNull();
        trainer.Abilities.ShouldNotBeNull();
        trainer.Weaknesses.ShouldNotBeNull();
        trainer.Resistances.ShouldNotBeNull();
        trainer.Types.ShouldNotBeNull();
        trainer.DexId.ShouldNotBeNull();
        trainer.Boosters.ShouldNotBeNull();
    }

    [Test]
    public void Construct_WithExplicitNullCollection_CoercesToEmpty()
    {
        // The same guard protects callers building a Card by hand.
        var card = new Card
        {
            Id = "x-1",
            Name = "Test",
            Category = CardCategories.Trainer,
            LocalId = "1",
            Set = new SetBrief { Id = "x", Name = "Test Set" },
            Attacks = null!,
            Types = null!,
        };

        card.Attacks.ShouldBeEmpty();
        card.Types.ShouldBeEmpty();
    }

    [Test]
    public void Deserialize_CardWithSuffix_PreservesCase()
    {
        var card = Fixture.Load<Card>("card-damage-int.json");

        // "EX" and "ex" denote different eras, so casing must survive.
        card.Suffix.ShouldBe("EX");
    }
}

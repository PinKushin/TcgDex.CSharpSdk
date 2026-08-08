namespace TcgDex.Tests.Models;

using TcgDex.Models;

/// <summary>
/// Contract tests: every case here deserializes a response recorded from the
/// live API, so a model that drifts from what TCGdex actually sends fails here
/// rather than in production.
/// </summary>
/// <remarks>
/// Each fixture was chosen for a shape that is easy to model wrongly —
/// polymorphic damage, an object array, a missing image. See docs/api-info.md
/// §9 for what each card covers.
/// </remarks>
[TestFixture]
public sealed class CardContractTests
{
    [Test]
    public void Deserialize_PokemonCard_MapsCoreFields()
    {
        Card card = Fixture.Load<Card>("card-pokemon-full.json");

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
        Card card = Fixture.Load<Card>("card-pokemon-full.json");

        card.Set.Id.ShouldBe("swsh3");
        card.Set.Name.ShouldBe("Darkness Ablaze");
        card.Set.CardCount.ShouldNotBeNull();
        card.Set.CardCount.Official.ShouldBe(189);
        card.Set.CardCount.Total.ShouldBe(201);
    }

    [Test]
    public void Deserialize_PokemonCard_MapsAttacksWithEnergyCost()
    {
        Card card = Fixture.Load<Card>("card-pokemon-full.json");

        card.Attacks.ShouldNotBeEmpty();

        Attack attack = card.Attacks.SingleOrDefault(a => a.Name == "Feelin' Fine")
            .ShouldNotBeNull("expected the recorded card to have a 'Feelin' Fine' attack");

        attack.Effect.ShouldBe("Draw 3 cards.");
        attack.Cost.ShouldBe(["Colorless"]);
        attack.Damage.ShouldBeNull("this attack draws cards and deals no damage");
    }

    [Test]
    public void Deserialize_PokemonCard_MapsWeaknessValueAsText()
    {
        Card card = Fixture.Load<Card>("card-pokemon-full.json");

        WeaknessOrResistance weakness = card.Weaknesses.ShouldHaveSingleItem();
        weakness.Type.ShouldBe("Fighting");

        // "×2" is a multiplier, not a number — modelling this as int would throw.
        weakness.Value.ShouldBe("×2");
    }

    [Test]
    public void Deserialize_PokemonCard_MapsLegality()
    {
        Card card = Fixture.Load<Card>("card-pokemon-full.json");

        card.Legal.ShouldNotBeNull();
        card.Legal.Standard.ShouldBeFalse();
        card.Legal.Expanded.ShouldBeTrue();
    }

    // ----- the polymorphic damage field -----

    [Test]
    public void Deserialize_AttackDamage_WhenJsonNumber_ReadsAsText()
    {
        Card card = Fixture.Load<Card>("card-damage-int.json");

        List<string?> damages = card.Attacks.Select(a => a.Damage).Where(d => d is not null).ToList();
        damages.ShouldNotBeEmpty("xy1-1 is recorded because it sends damage as a JSON number");
        damages.ShouldContain("60");
    }

    [Test]
    public void Deserialize_AttackDamage_WhenJsonString_KeepsModifier()
    {
        Card card = Fixture.Load<Card>("card-damage-string.json");

        List<string?> damages = card.Attacks.Select(a => a.Damage).ToList();
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
        Attack attack = new() { Name = "test", Damage = damage };

        attack.BaseDamage.ShouldBe(expected);
    }

    // ----- category-specific shapes -----

    [Test]
    public void Deserialize_TrainerCard_MapsTrainerTypeAndEffect()
    {
        Card card = Fixture.Load<Card>("card-trainer.json");

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
        Card card = Fixture.Load<Card>("card-energy.json");

        card.Category.ShouldBe(CardCategories.Energy);
        card.EnergyType.ShouldBe("Normal");

        // Recorded because `stage` is not Pokémon-exclusive, contrary to how the
        // field is usually described.
        card.Stage.ShouldNotBeNull();
    }

    [Test]
    public void Deserialize_CardWithAbilities_MapsEraSpecificType()
    {
        Card card = Fixture.Load<Card>("card-with-resistances.json");

        Ability ability = card.Abilities.ShouldHaveSingleItem();
        ability.Name.ShouldBe("Damage Bind");
        ability.Type.ShouldBe("Poke-BODY", "ability type is an era label, not a fixed enum");
    }

    [Test]
    public void Deserialize_CardWithResistances_MapsSignedTextValue()
    {
        Card card = Fixture.Load<Card>("card-with-resistances.json");

        WeaknessOrResistance resistance = card.Resistances.ShouldHaveSingleItem();
        resistance.Type.ShouldBe("Metal");
        resistance.Value.ShouldBe("-20", "resistance values are signed text, not numbers");
    }

    [Test]
    public void Deserialize_CardWithBoosters_MapsObjectArray()
    {
        Card card = Fixture.Load<Card>("card-with-boosters.json");

        // `boosters` is an object array. Typing it as a string throws here.
        Booster booster = card.Boosters.ShouldHaveSingleItem();
        booster.Id.ShouldBe("boo_A4-ho-oh");
        booster.Name.ShouldBe("Ho-Oh");
    }

    [Test]
    public void Deserialize_CardWithoutImage_LeavesImageNull()
    {
        Card card = Fixture.Load<Card>("card-missing-image.json");

        card.Id.ShouldBe("exu-!");
        card.LocalId.ShouldBe("!", "local ids are not always numeric");
        card.Image.ShouldBeNull("this card has no artwork on record");
    }

    [Test]
    public void Deserialize_NumericLocalId_ReadsAsText()
    {
        // TCGdex documents localId as "String or Number"
        // (https://tcgdex.dev/reference/card). Every card the live API serves
        // today quotes it — 0 unquoted occurrences across the full ~2.3 MB card
        // list — and the GraphQL schema declares it `String!`. So this is not a
        // shape that was observed; it is the one the published contract permits.
        //
        // It is worth tolerating anyway because LocalId is `required`: a single
        // unquoted value would throw and lose the *whole* card rather than one
        // field, and the same assumption typed as a number is what broke the
        // previous SDK on `attacks[].damage`.
        // Two-argument Replace on purpose: the StringComparison overload does not
        // exist on net472, which this suite also runs against.
        string json = Fixture.ReadText("card-pokemon-full.json")
            .Replace("\"localId\":\"136\"", "\"localId\":136");

        json.ShouldContain("\"localId\":136", customMessage: "the fixture edit must actually apply");

        Card card = Fixture.Parse<Card>(json);

        card.LocalId.ShouldBe("136");
    }

    [Test]
    public void Deserialize_AbsentCollections_AreEmptyNotNull()
    {
        // System.Text.Json's source generator does not apply property
        // initializers, so `= []` on the model is silently discarded and every
        // omitted array arrives as null. Each collection therefore guards in its
        // init accessor. Without that, consuming a Trainer card and iterating
        // Attacks throws NullReferenceException.
        Card trainer = Fixture.Load<Card>("card-trainer.json");

        // Empty, which is what the test name claims and what it did not check.
        // Every assertion here was ShouldNotBeNull, so a guard that returned a
        // one-element list of nulls — or any non-empty collection — would have
        // passed a test called AreEmptyNotNull.
        trainer.Attacks.ShouldBeEmpty();
        trainer.Abilities.ShouldBeEmpty();
        trainer.Weaknesses.ShouldBeEmpty();
        trainer.Resistances.ShouldBeEmpty();
        trainer.Types.ShouldBeEmpty();
        trainer.DexId.ShouldNotBeNull();
        trainer.Boosters.ShouldNotBeNull();
    }

    [Test]
    public void Construct_WithExplicitNullCollection_CoercesToEmpty()
    {
        // The same guard protects callers building a Card by hand.
        Card card = new()
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
        Card card = Fixture.Load<Card>("card-damage-int.json");

        // "EX" and "ex" denote different eras, so casing must survive.
        card.Suffix.ShouldBe("EX");
    }
}

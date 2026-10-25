namespace TcgDex.Tests.Models;

using TcgDex.Models;
using TcgDex.Serialization;

/// <summary>
/// Models compare by value, including the collections they hold (DECISIONS #40).
/// </summary>
/// <remarks>
/// A record's synthesised equality compares a property with
/// <c>EqualityComparer&lt;T&gt;.Default</c>, and for an <c>IReadOnlyList&lt;T&gt;</c> that is reference
/// equality: two cards deserialized from the same JSON were never equal.
/// </remarks>
[TestFixture]
public sealed class ValueEqualityTests
{
    private const string CardJson = """
        {"id":"x-1","localId":"1","name":"X","category":"Pokemon","set":{"id":"s","name":"S"},
         "types":["Fire"],"dexId":[4],
         "attacks":[{"name":"Ember","cost":["Fire","Colorless"],"damage":30}],
         "abilities":[{"name":"Blaze","type":"Ability","effect":"Hot"}],
         "weaknesses":[{"type":"Water","value":"x2"}],
         "resistances":[{"type":"Fighting","value":"-30"}],
         "boosters":[{"id":"b1","name":"Booster"}],
         "variants_detailed":[{"type":"normal","stamp":["set-logo"]}]}
        """;

    private static Card ParseCard() => JsonSerializer.Deserialize(CardJson, TcgDexJsonContext.Default.Card)!;

    // ----- the headline: identical JSON, equal models -----

    [Test]
    public void TwoCards_FromIdenticalJson_AreEqual_WithEqualHashCodes()
    {
        Card first = ParseCard();
        Card second = ParseCard();

        first.ShouldBe(second);
        (first == second).ShouldBeTrue();
        first.GetHashCode().ShouldBe(second.GetHashCode());
    }

    /// <summary>One change per collection on Card, so a collection left out of the comparison fails here.</summary>
    private static IEnumerable<TestCaseData> ACardWithOneCollectionChanged()
    {
        yield return Case("Types", c => c with { Types = ["Water"] });
        yield return Case("DexIds", c => c with { DexIds = [5] });
        yield return Case("Attacks", c => c with { Attacks = [new Attack { Name = "Other" }] });
        yield return Case("Abilities", c => c with { Abilities = [new Ability { Name = "Other" }] });
        yield return Case("Weaknesses", c => c with { Weaknesses = [new WeaknessOrResistance { Type = "Grass" }] });
        yield return Case("Resistances", c => c with { Resistances = [new WeaknessOrResistance { Type = "Metal" }] });
        yield return Case("Boosters", c => c with { Boosters = [new Booster { Id = "b2" }] });
        yield return Case("DetailedVariants", c => c with { DetailedVariants = [new DetailedVariant { Type = "holo" }] });

        static TestCaseData Case(string name, Func<Card, Card> change) => new TestCaseData(change).SetName($"A_{name}_Difference_MakesTheCardsUnequal");
    }

    [TestCaseSource(nameof(ACardWithOneCollectionChanged))]
    public void ACardWithOneCollectionChanged_IsNotEqual(Func<Card, Card> change)
    {
        Card original = ParseCard();

        change(original).ShouldNotBe(original);
    }

    /// <summary>
    /// The mirror image: one collection per property rebuilt as a different list with the same items. This is the
    /// case that fails if a property's setter forgets the wrapper, which the "one item changed" cases above cannot
    /// see (a list compared by reference is also unequal after a change).
    /// </summary>
    private static IEnumerable<TestCaseData> ACardWithOneCollectionRebuilt()
    {
        yield return Case("Types", c => c with { Types = c.Types.ToList() });
        yield return Case("DexIds", c => c with { DexIds = c.DexIds.ToList() });
        yield return Case("Attacks", c => c with { Attacks = c.Attacks.ToList() });
        yield return Case("Abilities", c => c with { Abilities = c.Abilities.ToList() });
        yield return Case("Weaknesses", c => c with { Weaknesses = c.Weaknesses.ToList() });
        yield return Case("Resistances", c => c with { Resistances = c.Resistances.ToList() });
        yield return Case("Boosters", c => c with { Boosters = c.Boosters.ToList() });
        yield return Case("DetailedVariants", c => c with { DetailedVariants = c.DetailedVariants.ToList() });

        static TestCaseData Case(string name, Func<Card, Card> rebuild) => new TestCaseData(rebuild).SetName($"A_Rebuilt_{name}_KeepsTheCardsEqual");
    }

    [TestCaseSource(nameof(ACardWithOneCollectionRebuilt))]
    public void ACardWithOneCollectionRebuilt_IsStillEqual(Func<Card, Card> rebuild)
    {
        Card original = ParseCard();
        Card rebuilt = rebuild(original);

        // `with` builds a new Card, and each case supplies a freshly built list for one property, so equality here
        // has to come from comparing contents.
        rebuilt.ShouldNotBeSameAs(original);
        rebuilt.Equals(original).ShouldBeTrue();
        rebuilt.GetHashCode().ShouldBe(original.GetHashCode());
    }

    [Test]
    public void ACollectionSuppliedAsADifferentListInstance_WithTheSameItems_IsEqual()
    {
        // The setter wraps whatever the caller hands over, so a List, an array
        // and a deserialized list all compare by their contents.
        Card original = ParseCard();
        string[] asArray = ["Fire"];

        (original with { Types = new List<string> { "Fire" } }).ShouldBe(original);
        (original with { Types = asArray }).ShouldBe(original);
    }

    [Test]
    public void TheOrderOfACollection_Matters()
    {
        Card ab = ParseCard() with { Types = ["Fire", "Water"] };
        Card ba = ParseCard() with { Types = ["Water", "Fire"] };

        ab.ShouldNotBe(ba);
    }

    [Test]
    public void AShorterCollection_IsNotEqualToALongerOneWithTheSamePrefix()
    {
        Card one = ParseCard() with { Types = ["Fire"] };
        Card two = ParseCard() with { Types = ["Fire", "Water"] };

        one.ShouldNotBe(two);
    }

    [Test]
    public void AnAbsentCollection_AnExplicitEmptyOne_AndANullOne_AreAllEqual()
    {
        const string Base = """{"id":"x-1","localId":"1","name":"X","category":"Pokemon","set":{"id":"s","name":"S"}""";

        Card absent = JsonSerializer.Deserialize(Base + "}", TcgDexJsonContext.Default.Card)!;
        Card empty = JsonSerializer.Deserialize(Base + ""","types":[]}""", TcgDexJsonContext.Default.Card)!;
        Card explicitNull = JsonSerializer.Deserialize(Base + ""","types":null}""", TcgDexJsonContext.Default.Card)!;

        empty.ShouldBe(absent);
        explicitNull.ShouldBe(absent);
    }

    // ----- the other models that hold a collection -----

    [Test]
    public void Attacks_CompareTheirCostByContents()
    {
        Attack one = new() { Name = "A", Cost = ["Fire", "Fire"] };

        (one with { Cost = new List<string> { "Fire", "Fire" } }).ShouldBe(one);
        (one with { Cost = ["Fire"] }).ShouldNotBe(one);
    }

    [Test]
    public void DetailedVariants_CompareTheirStampsByContents()
    {
        DetailedVariant one = new() { Type = "normal", Stamps = ["set-logo"] };

        (one with { Stamps = new List<string> { "set-logo" } }).ShouldBe(one);
        (one with { Stamps = ["other"] }).ShouldNotBe(one);
    }

    [Test]
    public void Series_CompareTheirSetsByContents()
    {
        CardSetBrief setA = new() { Id = "a", Name = "A" };
        Series one = new() { Id = "s", Name = "S", Sets = [setA] };

        (one with { Sets = new List<CardSetBrief> { new() { Id = "a", Name = "A" } } }).ShouldBe(one);
        (one with { Sets = [new CardSetBrief { Id = "b", Name = "B" }] }).ShouldNotBe(one);
    }

    [Test]
    public void CardSets_CompareTheirCardsByContents()
    {
        CardBrief cardA = new() { Id = "a-1", LocalId = "1", Name = "A" };
        CardSet one = new() { Id = "s", Name = "S", Cards = [cardA] };

        (one with { Cards = new List<CardBrief> { new() { Id = "a-1", LocalId = "1", Name = "A" } } }).ShouldBe(one);
        (one with { Cards = [new CardBrief { Id = "b-1", LocalId = "1", Name = "B" }] }).ShouldNotBe(one);
    }

    [Test]
    public void Pricing_ComparesItsPrintingsByContents_WhateverTheirOrder()
    {
        TcgPlayerPricing one = new()
        {
            Unit = "USD",
            Printings = new Dictionary<string, TcgPlayerPrice>
            {
                ["normal"] = new() { LowPrice =1m },
                ["holofoil"] = new() { LowPrice =2m },
            },
        };

        TcgPlayerPricing reordered = one with
        {
            Printings = new Dictionary<string, TcgPlayerPrice>
            {
                ["holofoil"] = new() { LowPrice =2m },
                ["normal"] = new() { LowPrice =1m },
            },
        };

        reordered.ShouldBe(one);
        reordered.GetHashCode().ShouldBe(one.GetHashCode());
    }

    [Test]
    public void Pricing_NoticesAChangedPriceAKeyAndAMissingEntry()
    {
        TcgPlayerPricing one = new()
        {
            Printings = new Dictionary<string, TcgPlayerPrice> { ["normal"] = new() { LowPrice =1m } },
        };

        (one with { Printings = new Dictionary<string, TcgPlayerPrice> { ["normal"] = new() { LowPrice =9m } } }).ShouldNotBe(one);
        (one with { Printings = new Dictionary<string, TcgPlayerPrice> { ["holofoil"] = new() { LowPrice =1m } } }).ShouldNotBe(one);
        (one with { Printings = new Dictionary<string, TcgPlayerPrice>() }).ShouldNotBe(one);
    }

    [Test]
    public void Pricing_FromIdenticalJson_IsEqual()
    {
        const string Json = """{"unit":"USD","normal":{"lowPrice":1.5},"holofoil":{"lowPrice":2.5}}""";

        TcgPlayerPricing first = JsonSerializer.Deserialize(Json, TcgDexJsonContext.Default.TcgPlayerPricing)!;
        TcgPlayerPricing second = JsonSerializer.Deserialize(Json, TcgDexJsonContext.Default.TcgPlayerPricing)!;

        first.ShouldBe(second);
        first.Printings.Count.ShouldBe(2);
    }

    // ----- what the wrapper must not change -----

    [Test]
    public void ACollection_StillBehavesAsAReadOnlyList()
    {
        Card card = ParseCard() with { Types = ["Fire", "Water", "Grass"] };

        card.Types.Count.ShouldBe(3);
        card.Types[1].ShouldBe("Water");
        card.Types.ShouldBe(["Fire", "Water", "Grass"]);
    }

    [Test]
    public void ACardSurvivesASerializationRoundTrip_AndIsEqualToTheOriginal()
    {
        Card original = ParseCard();

        string json = JsonSerializer.Serialize(original, TcgDexJsonContext.Default.Card);
        Card back = JsonSerializer.Deserialize(json, TcgDexJsonContext.Default.Card)!;

        back.ShouldBe(original);
    }

    [Test]
    public void TheRecordText_ShowsACollectionsItems_NotItsTypeName()
    {
        // The default would print "System.Collections.Generic.List`1[System.String]".
        (ParseCard() with { Types = ["Fire", "Water"] }).ToString().ShouldContain("[Fire, Water]");
    }
}

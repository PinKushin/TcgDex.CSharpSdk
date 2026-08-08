namespace TcgDex.Tests.Models;

using TcgDex.Models;

/// <summary>
/// Contract tests for the pricing shapes, which are the most irregular part of
/// the API: hyphenated Cardmarket keys and TCGplayer printing names that vary
/// per card.
/// </summary>
[TestFixture]
public sealed class PricingContractTests
{
    [Test]
    public void Deserialize_Cardmarket_MapsHyphenatedKeys()
    {
        Card card = Fixture.Load<Card>("card-pokemon-full.json");

        CardmarketPricing cardmarket = card.Pricing.ShouldNotBeNull().Cardmarket.ShouldNotBeNull();

        cardmarket.Unit.ShouldBe("EUR");
        cardmarket.IdProduct.ShouldBe(483559);
        cardmarket.Avg.ShouldBe(0.11m);
        cardmarket.Low.ShouldBe(0.02m);

        // These map to `avg-holo` / `low-holo`, which do not round-trip without
        // explicit name mapping.
        cardmarket.AvgHolo.ShouldBe(0.29m);
        cardmarket.LowHolo.ShouldBe(0.04m);
        cardmarket.Avg30Holo.ShouldBe(0.32m);
    }

    [Test]
    public void Deserialize_TcgPlayer_KeysPrintingsByName()
    {
        Card card = Fixture.Load<Card>("card-pokemon-full.json");

        TcgPlayerPricing tcgplayer = card.Pricing.ShouldNotBeNull().Tcgplayer.ShouldNotBeNull();

        tcgplayer.Unit.ShouldBe("USD");
        tcgplayer.Printings.Keys.ShouldBe(["normal", "reverse-holofoil"], ignoreOrder: true);

        TcgPlayerPrice normal = tcgplayer["normal"].ShouldNotBeNull();
        normal.ProductId.ShouldBe(219333);
        normal.LowPrice.ShouldBe(0.02m);
        normal.MarketPrice.ShouldBe(0.09m);
        normal.DirectLowPrice.ShouldBeNull("this printing has no TCGplayer Direct price");
    }

    [Test]
    public void Deserialize_TcgPlayer_HandlesDifferentPrintingKeysPerCard()
    {
        // Recorded specifically because this card is keyed `holofoil` rather
        // than `normal` — fixed properties would silently drop it.
        Card card = Fixture.Load<Card>("card-pricing-holofoil.json");

        TcgPlayerPricing tcgplayer = card.Pricing.ShouldNotBeNull().Tcgplayer.ShouldNotBeNull();

        tcgplayer.Printings.Keys.ShouldContain("holofoil");
        tcgplayer["holofoil"].ShouldNotBeNull().MarketPrice.ShouldNotBeNull();
    }

    [Test]
    public void Indexer_ForUnknownPrinting_ReturnsNull()
    {
        Card card = Fixture.Load<Card>("card-pokemon-full.json");
        TcgPlayerPricing tcgplayer = card.Pricing.ShouldNotBeNull().Tcgplayer.ShouldNotBeNull();

        tcgplayer["a-printing-that-does-not-exist"].ShouldBeNull();
    }

    [Test]
    public void Deserialize_VariantsDetailed_CarriesPerPrintingPricing()
    {
        Card card = Fixture.Load<Card>("card-pokemon-full.json");

        // Counted and named. "Not empty" would have been satisfied by one
        // entry with every field null, which is exactly what a broken
        // snake_case mapping produces — the array binds, the contents do not.
        card.VariantsDetailed.Count.ShouldBe(
            2,
            "swsh3-136 is printed in a normal and a reverse variant");

        DetailedVariant first = card.VariantsDetailed[0];
        first.Type.ShouldBe("normal");
        first.Size.ShouldBe("standard");
        first.VariantId.ShouldBe("endfynwn4n10gzq", "variantId is REST-only and must not be dropped");

        // Reaching a leaf value proves the nested pricing really deserialized,
        // rather than an empty object having been constructed.
        first.Pricing.ShouldNotBeNull().Cardmarket.ShouldNotBeNull().Unit.ShouldBe("EUR");
    }

    [Test]
    public void Deserialize_Variants_MapsAllFlags()
    {
        Card card = Fixture.Load<Card>("card-pokemon-full.json");

        Variants variants = card.Variants.ShouldNotBeNull();
        variants.Normal.ShouldBeTrue();
        variants.Reverse.ShouldBeTrue();
        variants.Holo.ShouldBeFalse();
        variants.FirstEdition.ShouldBeFalse();
        variants.WPromo.ShouldBeFalse();
    }

}

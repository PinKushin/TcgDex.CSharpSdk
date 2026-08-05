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
        var card = Fixture.Load<Card>("card-pokemon-full.json");

        var cardmarket = card.Pricing.ShouldNotBeNull().Cardmarket.ShouldNotBeNull();

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
        var card = Fixture.Load<Card>("card-pokemon-full.json");

        var tcgplayer = card.Pricing.ShouldNotBeNull().Tcgplayer.ShouldNotBeNull();

        tcgplayer.Unit.ShouldBe("USD");
        tcgplayer.Printings.Keys.ShouldBe(["normal", "reverse-holofoil"], ignoreOrder: true);

        var normal = tcgplayer["normal"].ShouldNotBeNull();
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
        var card = Fixture.Load<Card>("card-pricing-holofoil.json");

        var tcgplayer = card.Pricing.ShouldNotBeNull().Tcgplayer.ShouldNotBeNull();

        tcgplayer.Printings.ShouldContainKey("holofoil");
        tcgplayer["holofoil"].ShouldNotBeNull().MarketPrice.ShouldNotBeNull();
    }

    [Test]
    public void Indexer_ForUnknownPrinting_ReturnsNull()
    {
        var card = Fixture.Load<Card>("card-pokemon-full.json");
        var tcgplayer = card.Pricing.ShouldNotBeNull().Tcgplayer.ShouldNotBeNull();

        tcgplayer["a-printing-that-does-not-exist"].ShouldBeNull();
    }

    [Test]
    public void Deserialize_VariantsDetailed_CarriesPerPrintingPricing()
    {
        var card = Fixture.Load<Card>("card-pokemon-full.json");

        card.VariantsDetailed.ShouldNotBeEmpty(
            "variants_detailed maps from a snake_case key and is easy to lose");

        var first = card.VariantsDetailed[0];
        first.Type.ShouldNotBeNullOrWhiteSpace();
        first.VariantId.ShouldNotBeNullOrWhiteSpace("variantId is REST-only and must not be dropped");
        first.Pricing.ShouldNotBeNull();
    }

    [Test]
    public void Deserialize_Variants_MapsAllFlags()
    {
        var card = Fixture.Load<Card>("card-pokemon-full.json");

        var variants = card.Variants.ShouldNotBeNull();
        variants.Normal.ShouldBeTrue();
        variants.Reverse.ShouldBeTrue();
        variants.Holo.ShouldBeFalse();
        variants.FirstEdition.ShouldBeFalse();
        variants.WPromo.ShouldBeFalse();
    }
}

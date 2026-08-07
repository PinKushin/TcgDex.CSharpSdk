namespace TcgDex.Tests.Models;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;
using TcgDex.Models;
using TcgDex.Serialization;
using TcgDex.Tests.Http;

/// <summary>
/// <see cref="TcgDexOptions.DeserializePricing"/>, in both directions.
/// </summary>
/// <remarks>
/// <para>
/// The option removes the <c>pricing</c> property from the deserialization
/// contract rather than nulling it afterwards, so the block is skipped as an
/// unknown field. Both directions are asserted because only one of them is the
/// interesting failure: an implementation that quietly did nothing would still
/// pass a test that only checked the default.
/// </para>
/// <para>
/// The rest of the card is asserted alongside, since removing a property from a
/// contract is exactly the kind of change that could take neighbouring
/// properties with it.
/// </para>
/// </remarks>
[TestFixture]
public sealed class PricingOptOutTests
{
    private static TcgDexClient CreateClient(bool deserializePricing)
        => new(
            new HttpClient(new RecordingHandler()
                .RespondWith(HttpStatusCode.OK, Fixture.ReadText("card-pokemon-full.json"))),
            new TcgDexOptions { DeserializePricing = deserializePricing });

    [Test]
    public async Task ByDefault_PricingIsPopulated()
    {
        using var client = CreateClient(deserializePricing: true);

        var card = await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        card.ShouldNotBeNull().Pricing.ShouldNotBeNull()
            .Cardmarket.ShouldNotBeNull().Avg.ShouldBe(0.11m);
    }

    [Test]
    public async Task WhenTurnedOff_PricingIsNull()
    {
        using var client = CreateClient(deserializePricing: false);

        var card = await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        card.ShouldNotBeNull().Pricing.ShouldBeNull(
            "the property is dropped from the contract, so the block is skipped");
    }

    [Test]
    public async Task WhenTurnedOff_EveryOtherFieldStillDeserializes()
    {
        using var client = CreateClient(deserializePricing: false);

        var card = (await client.Cards.GetAsync("swsh3-136", CancellationToken.None)).ShouldNotBeNull();

        card.Name.ShouldBe("Furret");
        card.Id.ShouldBe("swsh3-136");
        card.Hp.ShouldBe(110);
        card.Rarity.ShouldBe("Uncommon");
        card.Illustrator.ShouldBe("tetsuya koizumi");
        card.Set.ShouldNotBeNull().Id.ShouldBe("swsh3");
        card.Attacks.ShouldNotBeNull().ShouldNotBeEmpty();
        card.Variants.ShouldNotBeNull();
    }

    [Test]
    public void WhenTurnedOff_ACardStillSerializes()
    {
        // The SDK reads an API it never writes to, so nothing internal takes
        // this path — but a converter that threw on Write would turn any
        // caller's attempt to serialize a Card into a crash, and round-tripping
        // a model through System.Text.Json is an ordinary thing to do. Writing
        // null is honest: with pricing off there is none to emit.
        var card = Fixture.Load<Card>("card-pokemon-full.json");
        var contract = TcgDexJsonContracts.For(new TcgDexOptions { DeserializePricing = false });

        var json = Should.NotThrow(() => JsonSerializer.Serialize(card, contract));

        json.ShouldContain("\"pricing\":null");
        json.ShouldContain("Furret", Case.Sensitive, "the rest of the card must still be written");
    }

    [Test]
    public void TheDefault_IsOn()
        => new TcgDexOptions().DeserializePricing.ShouldBeTrue(
            "a null Pricing must mean the API sent none, not that it was switched off");

    [Test]
    public async Task BothSettings_CanCoexistInOneProcess()
    {
        // The reason this is a contract modifier rather than a static flag. Two
        // clients configured differently are an ordinary DI arrangement, and a
        // global switch would make whichever was constructed last win.
        using var on = CreateClient(deserializePricing: true);
        using var off = CreateClient(deserializePricing: false);

        var withPricing = await on.Cards.GetAsync("swsh3-136", CancellationToken.None);
        var withoutPricing = await off.Cards.GetAsync("swsh3-136", CancellationToken.None);

        withPricing.ShouldNotBeNull().Pricing.ShouldNotBeNull();
        withoutPricing.ShouldNotBeNull().Pricing.ShouldBeNull();
    }
}

namespace TcgDex.Tests.Models;

using System.Text.Json.Serialization.Metadata;
using TcgDex.Models;
using TcgDex.Serialization;

/// <summary>
/// Serializing models back to JSON.
/// </summary>
/// <remarks>
/// The API is read-only, so the SDK itself never serializes a card — but the
/// serializer context is public, and writing one out is a reasonable thing for
/// a consumer to do: caching to disk, logging a payload, snapshot testing.
/// A converter's <c>Write</c> method is abstract, so these code paths exist
/// whether or not the SDK uses them; testing them makes round-tripping a
/// supported feature rather than an accident.
/// </remarks>
[TestFixture]
public sealed class SerializationTests
{
    private static string Serialize<T>(T value)
        where T : notnull
    {
        var typeInfo = (JsonTypeInfo<T>)TcgDexJsonContext.Default.Options.GetTypeInfo(typeof(T));
        return JsonSerializer.Serialize(value, typeInfo);
    }

    private static T Deserialize<T>(string json)
        where T : notnull
    {
        var typeInfo = (JsonTypeInfo<T>)TcgDexJsonContext.Default.Options.GetTypeInfo(typeof(T));
        return JsonSerializer.Deserialize(json, typeInfo)!;
    }

    [Test]
    public void Card_RoundTrips()
    {
        var original = Fixture.Load<Card>("card-pokemon-full.json");

        var restored = Deserialize<Card>(Serialize(original));

        restored.Id.ShouldBe(original.Id);
        restored.Name.ShouldBe(original.Name);
        restored.Hp.ShouldBe(original.Hp);
        restored.Types.ShouldBe(original.Types);
        restored.Set.Id.ShouldBe(original.Set.Id);
        restored.Attacks.Count.ShouldBe(original.Attacks.Count);
    }

    [Test]
    public void Attack_WithTextDamage_RoundTripsThroughTheConverter()
    {
        // FlexibleStringConverter.Write: damage is read from either a number or
        // a string, and always written back as a string.
        var original = new Attack { Name = "Blasting Wind", Damage = "50+", Cost = ["Grass"] };

        // Asserted by round trip rather than by raw text: System.Text.Json's
        // default encoder escapes `+` as +, so the serialized form does not
        // contain a literal "50+" even though the value is preserved exactly.
        Deserialize<Attack>(Serialize(original)).Damage.ShouldBe("50+");
    }

    [Test]
    public void Attack_WithNullDamage_WritesNull()
    {
        var json = Serialize(new Attack { Name = "Feelin' Fine" });

        Deserialize<Attack>(json).Damage.ShouldBeNull();
    }

    [Test]
    public void AttackDamage_ReadFromNumber_IsWrittenAsString()
    {
        // Proves the normalisation survives a round trip: a numeric input
        // becomes text and stays text.
        var attack = Deserialize<Attack>("""{"name":"Tail Smash","damage":130}""");

        attack.Damage.ShouldBe("130");

        // Written back as a string, not a number.
        Serialize(attack).ShouldContain("\"damage\":\"130\"");
    }

    [TestCase("""{"name":"x","damage":true}""", "true")]
    [TestCase("""{"name":"x","damage":false}""", "false")]
    [TestCase("""{"name":"x","damage":12.5}""", "12.5")]
    public void AttackDamage_AcceptsOtherScalarShapes(string json, string expected)
        => Deserialize<Attack>(json).Damage.ShouldBe(expected);

    [Test]
    public void AttackDamage_WhenNotAScalar_ThrowsJsonException()
    {
        // An object or array here means the API changed shape, which should be
        // loud rather than silently coerced.
        Should.Throw<JsonException>(
            () => Deserialize<Attack>("""{"name":"x","damage":{"unexpected":1}}"""));
    }

    [Test]
    public void TcgPlayerPricing_RoundTripsItsDynamicPrintingKeys()
    {
        // TcgPlayerPricingConverter.Write: printing names are data, so they must
        // survive being written back out.
        var original = Fixture.Load<Card>("card-pokemon-full.json");
        var pricing = original.Pricing.ShouldNotBeNull().Tcgplayer.ShouldNotBeNull();

        var restored = Deserialize<Card>(Serialize(original))
            .Pricing.ShouldNotBeNull().Tcgplayer.ShouldNotBeNull();

        restored.Unit.ShouldBe(pricing.Unit);
        restored.Printings.Keys.ShouldBe(pricing.Printings.Keys, ignoreOrder: true);
        restored["normal"].ShouldNotBeNull().MarketPrice.ShouldBe(pricing["normal"]!.MarketPrice);
    }

    [Test]
    public void TcgPlayerPricing_WithNoPrintings_RoundTrips()
    {
        var original = new TcgPlayerPricing { Unit = "USD" };

        var restored = Deserialize<TcgPlayerPricing>(Serialize(original));

        restored.Unit.ShouldBe("USD");
        restored.Printings.ShouldBeEmpty();
    }

    [Test]
    public void TcgPlayerPricing_WithNullPrintingValue_SkipsIt()
    {
        // The source reports a printing with no data as null rather than
        // omitting the key.
        var pricing = Deserialize<TcgPlayerPricing>(
            """{"unit":"USD","normal":null,"holofoil":{"marketPrice":1.5}}""");

        pricing.Printings.ShouldNotContainKey("normal");
        pricing["holofoil"].ShouldNotBeNull().MarketPrice.ShouldBe(1.5m);
    }

    [Test]
    public void TcgPlayerPricing_WhenNotAnObject_ThrowsJsonException()
        => Should.Throw<JsonException>(() => Deserialize<TcgPlayerPricing>("\"not an object\""));

    [Test]
    public void TcgPlayerPricing_WhenNull_DeserializesToNull()
    {
        var pricing = Deserialize<Pricing>("""{"tcgplayer":null,"cardmarket":null}""");

        pricing.Tcgplayer.ShouldBeNull();
        pricing.Cardmarket.ShouldBeNull();
    }

    [Test]
    public void Set_RoundTrips()
    {
        var original = Fixture.Load<Set>("set-full.json");

        var restored = Deserialize<Set>(Serialize(original));

        restored.Id.ShouldBe(original.Id);
        restored.Cards.Count.ShouldBe(original.Cards.Count);
        restored.CardCount.ShouldNotBeNull().Total.ShouldBe(original.CardCount!.Total);
    }

    [Test]
    public void Serie_RoundTrips()
    {
        var original = Fixture.Load<Serie>("serie-full.json");

        var restored = Deserialize<Serie>(Serialize(original));

        restored.Id.ShouldBe(original.Id);
        restored.Sets.Count.ShouldBe(original.Sets.Count);
    }
}

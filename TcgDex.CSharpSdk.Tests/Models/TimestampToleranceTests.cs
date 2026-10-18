namespace TcgDex.Tests.Models;

using TcgDex.Models;

/// <summary>
/// Timestamps follow the same tolerance as <c>ReleaseDate</c>: a value that is
/// not a readable timestamp becomes <see langword="null"/> rather than failing
/// the whole card.
/// </summary>
[TestFixture]
public sealed class TimestampToleranceTests
{
    private const string Card = """{"id":"x-1","localId":"1","name":"X","category":"Pokemon","set":{"id":"x","name":"X"},"hp":60,"updated":UPDATED}""";

    [TestCase("\"soon\"")]
    [TestCase("\"\"")]
    [TestCase("12345")]
    [TestCase("true")]
    [TestCase("{\"at\":1}")]
    [TestCase("[1,2]")]
    public void CardUpdated_Unreadable_IsNullAndTheCardStillParses(string value)
    {
        Card card = Fixture.Parse<Card>(Card.Replace("UPDATED", value));

        card.Updated.ShouldBeNull();
        card.HitPoints.ShouldBe(60);
    }

    [Test]
    public void CardUpdated_Readable_IsKeptWithItsOffset()
        => Fixture.Parse<Card>(Card.Replace("UPDATED", "\"2026-07-01T21:27:44+01:00\"")).Updated
            .ShouldBe(new DateTimeOffset(2026, 7, 1, 21, 27, 44, TimeSpan.FromHours(1)));

    [Test]
    public void CardmarketUpdated_Unreadable_IsNull()
    {
        CardmarketPricing pricing = Fixture.Parse<CardmarketPricing>("""{"updated":"yesterday","avg":0.5}""");

        pricing.Updated.ShouldBeNull();
        pricing.Average.ShouldBe(0.5m);
    }

    [Test]
    public void TcgPlayerUpdated_Unreadable_IsNullAndPrintingsSurvive()
    {
        TcgPlayerPricing pricing = Fixture.Parse<TcgPlayerPricing>(
            """{"updated":"yesterday","unit":"USD","normal":{"lowPrice":1}}""");

        pricing.Updated.ShouldBeNull();
        pricing.Unit.ShouldBe("USD");
        pricing["normal"].ShouldNotBeNull().LowPrice.ShouldBe(1m);
    }

    [Test]
    public void TcgPlayerUpdated_NonString_IsNull()
        => Fixture.Parse<TcgPlayerPricing>("""{"updated":{"at":1},"normal":{"lowPrice":1}}""")
            .Updated.ShouldBeNull();

    [Test]
    public void TcgPlayerUpdated_Readable_IsKept()
        => Fixture.Parse<TcgPlayerPricing>("""{"updated":"2026-09-11T09:46:38.519Z"}""").Updated
            .ShouldBe(new DateTimeOffset(2026, 9, 11, 9, 46, 38, 519, TimeSpan.Zero));

    [Test]
    public void Updated_RoundTripsThroughTheSerializer()
    {
        Card original = Fixture.Parse<Card>(Card.Replace("UPDATED", "\"2026-07-01T21:27:44+01:00\""));

        string json = JsonSerializer.Serialize(original, TcgDex.Serialization.TcgDexJsonContext.Default.Card);

        Fixture.Parse<Card>(json).Updated.ShouldBe(original.Updated);
    }
}

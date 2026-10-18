namespace TcgDex.Tests.Models;

using System.IO;
using System.Text;
using TcgDex.Models;
using TcgDex.Serialization;

/// <summary>
/// The converters' argument guards name the argument that was null.
/// </summary>
/// <remarks>
/// Without a guard each of these still throws, but a <see cref="NullReferenceException"/>
/// from somewhere inside the writer or the options. Asserting only "it throws" passed
/// against every guard removed, which is how mutation testing found them, so each test
/// pins the exception type and the parameter name.
/// </remarks>
[TestFixture]
public sealed class ConverterGuardTests
{
    /// <summary>The TCGplayer block from the <c>swsh3-136</c> fixture, verbatim.</summary>
    private const string TcgPlayerJson =
        """{"unit":"USD","updated":"2026-09-11T09:46:38.519Z","normal":{"productId":219333,"lowPrice":0.02,"midPrice":0.24,"highPrice":25.24,"marketPrice":0.24,"directLowPrice":null},"reverse-holofoil":{"productId":219333,"lowPrice":0.17,"midPrice":0.41,"highPrice":19.98,"marketPrice":0.46,"directLowPrice":null}}""";

    [Test]
    public void FlexibleString_Write_WithNullWriter_NamesTheWriter()
        => Should.Throw<ArgumentNullException>(
                () => new FlexibleStringConverter().Write(null!, "50+", JsonSerializerOptions.Default))
            .ParamName.ShouldBe("writer");

    [Test]
    public void TcgPlayerPricing_Write_WithNullWriter_NamesTheWriter()
        => Should.Throw<ArgumentNullException>(
                () => new TcgPlayerPricingConverter().Write(null!, Parse(), TcgDexJsonContext.Default.Options))
            .ParamName.ShouldBe("writer");

    [Test]
    public void TcgPlayerPricing_Write_WithNullValue_NamesTheValue()
    {
        using MemoryStream stream = new();
        using Utf8JsonWriter writer = new(stream);

        Should.Throw<ArgumentNullException>(
                () => new TcgPlayerPricingConverter().Write(writer, null!, TcgDexJsonContext.Default.Options))
            .ParamName.ShouldBe("value");
    }

    [Test]
    public void TcgPlayerPricing_Read_WithNullOptions_NamesTheOptions()
        => Should.Throw<ArgumentNullException>(() =>
            {
                Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(TcgPlayerJson));
                reader.Read();
                return new TcgPlayerPricingConverter().Read(ref reader, typeof(TcgPlayerPricing), null!);
            })
            .ParamName.ShouldBe("options");

    [Test]
    public void TcgPlayerPricing_TheControl_RealJsonReadsAndRoundTrips()
    {
        // The control for the three above: with valid arguments the same calls succeed,
        // so the guards are what throws, not the payload.
        TcgPlayerPricing pricing = Parse();

        pricing.Unit.ShouldBe("USD");
        pricing.Updated.ShouldBe(new DateTimeOffset(2026, 9, 11, 9, 46, 38, 519, TimeSpan.Zero));
        pricing.Printings.Keys.OrderBy(k => k, StringComparer.Ordinal).ShouldBe(["normal", "reverse-holofoil"]);
        pricing.Printings["reverse-holofoil"].MarketPrice.ShouldBe(0.46m);

        string json = JsonSerializer.Serialize(pricing, TcgDexJsonContext.Default.Options);
        JsonSerializer.Deserialize<TcgPlayerPricing>(json, TcgDexJsonContext.Default.Options)
            .ShouldNotBeNull().Printings["normal"].MarketPrice.ShouldBe(0.24m);
    }

    private static TcgPlayerPricing Parse()
        => JsonSerializer.Deserialize<TcgPlayerPricing>(TcgPlayerJson, TcgDexJsonContext.Default.Options)
            .ShouldNotBeNull();
}

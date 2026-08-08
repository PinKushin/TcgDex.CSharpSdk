namespace TcgDex.Tests.Querying;

using System.Text.Json.Serialization.Metadata;
using TcgDex.Models;
using TcgDex.Querying;
using TcgDex.Serialization;

/// <summary>
/// The last corners of the translator and the JSON converters.
/// </summary>
/// <remarks>
/// Mostly negation, value formatting, and the malformed-input guards. Each is
/// a branch a caller can reach, so each gets a test rather than a coverage
/// exclusion.
/// </remarks>
[TestFixture]
public sealed class TranslatorEdgeTests
{
    private static CardQuery Query() => new();

    // ----- negation of each invertible operator -----

    [Test]
    public void NegatedEquality_BecomesNotEqual()
        => Query().Where(c => !(c.Name == "Furret")).ToQueryString().ShouldBe("name=neq:Furret");

    [Test]
    public void NegatedInequality_BecomesEqual()
        => Query().Where(c => !(c.Name != "Furret")).ToQueryString().ShouldBe("name=eq:Furret");

    [Test]
    public void NegatedNullCheck_BecomesNotNull()
        => Query().Where(c => !(c.Effect == null)).ToQueryString().ShouldBe("effect=notnull:");

    [Test]
    public void NegatedNotNullCheck_BecomesNull()
        => Query().Where(c => !(c.Effect != null)).ToQueryString().ShouldBe("effect=null:");

    // ----- string.Equals and empty arguments -----

    [Test]
    public void StringEqualsMethod_TranslatesToExactMatch()
        => Query().Where(c => c.Name.Equals("Furret")).ToQueryString().ShouldBe("name=eq:Furret");

    [Test]
    public void ContainsWithEmptyValue_IsRejectedWithAUsefulMessage()
    {
        // An empty filter value would match everything, which is never what the
        // caller meant.
        NotSupportedException exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Name.Contains("")));

        exception.Message.ShouldContain("name");
    }

    // ----- value formatting -----

    [TestCase(true, "true")]
    [TestCase(false, "false")]
    public void BooleanValue_IsFormattedAsLowercaseText(bool flag, string expected)
    {
        // Tested directly because no Card property is a bool, so no predicate
        // can reach this branch. It exists so that the first bool field added
        // does not silently emit "True" instead of "true".
        ExpressionTranslator.Format(flag).ShouldBe(expected);
    }

    [Test]
    public void NonFormattableValue_FallsBackToToString()
    {
        Uri value = new("https://example.test/x");

        ExpressionTranslator.Format(value).ShouldBe("https://example.test/x");
    }

    [Test]
    public void DecimalValue_UsesInvariantSeparator()
        => ExpressionTranslator.Format(1234.5m).ShouldBe("1234.5");

    [Test]
    public void StaticConstantValue_IsResolved()
        => Query().Where(c => c.Category == CardCategories.Pokemon)
            .ToQueryString().ShouldBe("category=eq:Pokemon");

    [Test]
    public void StaticReadOnlyFieldValue_IsResolved()
    {
        // A const is inlined by the compiler and never appears in the tree; a
        // static readonly field does, and has to be read reflectively with no
        // instance.
        Query().Where(c => c.Name == StaticDefaults.Name)
            .ToQueryString().ShouldBe("name=eq:Furret");
    }

    [Test]
    public void StaticPropertyValue_IsResolved()
        => Query().Where(c => c.Rarity == StaticDefaults.Rarity)
            .ToQueryString().ShouldBe("rarity=eq:Common");

    [Test]
    public void ConstantPredicate_IsRejected()
    {
        // `Where(c => true)` has no field to filter on, so it cannot be
        // translated into anything meaningful.
        NotSupportedException exception = Should.Throw<NotSupportedException>(() => Query().Where(c => true));

        exception.Message.ShouldNotBeNullOrWhiteSpace();
    }

    private static class StaticDefaults
    {
        internal static readonly string Name = "Furret";

        internal static string Rarity => "Common";
    }

    [Test]
    public void NumericValue_UsesInvariantFormatting()
    {
        // A comma decimal separator from a local culture would produce a filter
        // the API cannot parse.
        Query().Where(c => c.Hp > 1000).ToQueryString().ShouldBe("hp=gt:1000");
    }

    // ----- unsupported node shapes -----

    [Test]
    public void ArrayIndexerInPredicate_IsRejected()
        => Should.Throw<NotSupportedException>(() => Query().Where(c => c.Types[0] == "Grass"));

    [Test]
    public void MethodCallWithNoTarget_IsRejected()
        => Should.Throw<NotSupportedException>(
            () => Query().Where(c => string.IsNullOrEmpty(c.Name)));

    [Test]
    public void MethodCallWithTwoArguments_IsRejected()
        => Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Name.StartsWith("Fu", StringComparison.Ordinal)));

    [Test]
    public void ConditionalExpression_IsRejected()
        => Should.Throw<NotSupportedException>(
            () => Query().Where(c => (c.Hp > 100 ? "big" : "small") == "big"));

    [Test]
    public void NegatedUnsupportedOperand_IsRejected()
        => Should.Throw<NotSupportedException>(
            () => Query().Where(c => !c.Name.Equals("Furret", StringComparison.Ordinal)));

    // ----- converter guards -----

    private static T Deserialize<T>(string json)
        where T : notnull
    {
        JsonTypeInfo<T> info = (JsonTypeInfo<T>)TcgDexJsonContext.Default.Options.GetTypeInfo(typeof(T));
        return JsonSerializer.Deserialize(json, info)!;
    }

    private static string Serialize<T>(T value)
        where T : notnull
    {
        JsonTypeInfo<T> info = (JsonTypeInfo<T>)TcgDexJsonContext.Default.Options.GetTypeInfo(typeof(T));
        return JsonSerializer.Serialize(value, info);
    }

    [Test]
    public void AttackDamage_WhenExplicitlyNull_ReadsAsNull()
        => Deserialize<Attack>("""{"name":"x","damage":null}""").Damage.ShouldBeNull();

    [Test]
    public void AttackDamage_WhenNull_IsWrittenAsJsonNull()
    {
        string json = Serialize(new Attack { Name = "x", Damage = null });

        // Round-trips as null rather than being dropped or written as "".
        Deserialize<Attack>(json).Damage.ShouldBeNull();
    }

    [Test]
    public void TcgPlayerPricing_WhenExplicitlyNull_ReadsAsNull()
        => Deserialize<Pricing>("""{"tcgplayer":null}""").Tcgplayer.ShouldBeNull();

    [Test]
    public void TcgPlayerPricing_WithMalformedContents_ThrowsJsonException()
    {
        // A bare array where an object was expected means the shape changed,
        // which should be loud.
        Should.Throw<JsonException>(() => Deserialize<TcgPlayerPricing>("[1,2,3]"));
    }
}

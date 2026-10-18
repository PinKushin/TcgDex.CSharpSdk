namespace TcgDex.Serialization;

using TcgDex.Models;

/// <summary>
/// Source-generated serialization metadata for every type the SDK exchanges
/// with the API.
/// </summary>
/// <remarks>
/// Generating the metadata at compile time keeps the SDK trim- and AOT-safe and
/// avoids the reflection cost on the first call for each type. Any model added
/// to the SDK must be registered here or it will fail to serialize at runtime.
/// Internal (DECISIONS #20): public, every generated <c>JsonTypeInfo</c>
/// property would be pinned API, so renaming a model would break callers twice.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Card))]
[JsonSerializable(typeof(CardBrief))]
[JsonSerializable(typeof(IReadOnlyList<CardBrief>))]
[JsonSerializable(typeof(CardSet))]
[JsonSerializable(typeof(CardSetBrief))]
[JsonSerializable(typeof(IReadOnlyList<CardSetBrief>))]
[JsonSerializable(typeof(Series))]
[JsonSerializable(typeof(SeriesBrief))]
[JsonSerializable(typeof(IReadOnlyList<SeriesBrief>))]
[JsonSerializable(typeof(Attack))]
[JsonSerializable(typeof(Ability))]
[JsonSerializable(typeof(WeaknessOrResistance))]
[JsonSerializable(typeof(Legality))]
[JsonSerializable(typeof(Variants))]
[JsonSerializable(typeof(DetailedVariant))]
[JsonSerializable(typeof(Booster))]
[JsonSerializable(typeof(CardCount))]
[JsonSerializable(typeof(CardSetAbbreviation))]
[JsonSerializable(typeof(Pricing))]
[JsonSerializable(typeof(CardmarketPricing))]
[JsonSerializable(typeof(TcgPlayerPricing))]
[JsonSerializable(typeof(TcgPlayerPrice))]
[JsonSerializable(typeof(ThirdParty))]
[JsonSerializable(typeof(TcgDexProblem))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<int>))]
internal sealed partial class TcgDexJsonContext : JsonSerializerContext
{
}

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
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Card))]
[JsonSerializable(typeof(CardBrief))]
[JsonSerializable(typeof(IReadOnlyList<CardBrief>))]
[JsonSerializable(typeof(Set))]
[JsonSerializable(typeof(SetBrief))]
[JsonSerializable(typeof(IReadOnlyList<SetBrief>))]
[JsonSerializable(typeof(Serie))]
[JsonSerializable(typeof(SerieBrief))]
[JsonSerializable(typeof(IReadOnlyList<SerieBrief>))]
[JsonSerializable(typeof(Attack))]
[JsonSerializable(typeof(Ability))]
[JsonSerializable(typeof(WeaknessOrResistance))]
[JsonSerializable(typeof(Legality))]
[JsonSerializable(typeof(Variants))]
[JsonSerializable(typeof(DetailedVariant))]
[JsonSerializable(typeof(Booster))]
[JsonSerializable(typeof(CardCount))]
[JsonSerializable(typeof(SetAbbreviation))]
[JsonSerializable(typeof(Pricing))]
[JsonSerializable(typeof(CardmarketPricing))]
[JsonSerializable(typeof(TcgPlayerPricing))]
[JsonSerializable(typeof(TcgPlayerPrice))]
[JsonSerializable(typeof(TcgDexProblem))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<int>))]
public sealed partial class TcgDexJsonContext : JsonSerializerContext
{
}

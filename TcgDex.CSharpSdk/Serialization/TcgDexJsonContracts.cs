namespace TcgDex.Serialization;

using System.Text.Json.Serialization.Metadata;
using TcgDex.Models;

/// <summary>
/// Supplies the serializer options a transport deserializes with, honouring
/// <see cref="TcgDexOptions.DeserializePricing"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a contract modifier and not a second context.</b> Making pricing
/// optional needs the deserializer to behave differently for the same type, and
/// System.Text.Json gives a converter no per-call state to decide that with. The
/// obvious workarounds are both bad: a static flag is wrong the moment one
/// process holds two clients configured differently, and a second
/// <c>[JsonSerializable]</c> context duplicates the generated code for the whole
/// <see cref="Card"/> graph to change one property.
/// </para>
/// <para>
/// <see cref="JsonTypeInfoResolver.WithAddedModifier"/> is the supported way to
/// adjust a contract after the source generator has produced it. It is metadata
/// manipulation rather than reflection, so source generation, trimming and
/// Native AOT are all unaffected.
/// </para>
/// <para>
/// <b>The property is re-converted, not removed.</b> Removing it outright was
/// the first attempt and it throws: <see cref="Card"/> deserializes through a
/// parameterized constructor, and System.Text.Json requires every constructor
/// parameter to bind to a property on the contract — <c>"Each parameter in the
/// deserialization constructor on type 'TcgDex.Models.Card' must bind to an
/// object property or field"</c>. Substituting a converter that skips the value
/// leaves the shape intact and still avoids building the object graph.
/// </para>
/// <para>
/// So this saves constructing <see cref="Pricing"/> and its nested prices, not
/// the tokenizing: <see cref="Utf8JsonReader.Skip"/> still walks the block. It
/// is therefore worth less than deleting <c>pricing</c> from the payload
/// altogether would be, and that is not on offer — the API returns the same
/// 2,940 bytes whatever field selection it is asked for.
/// </para>
/// </remarks>
internal static class TcgDexJsonContracts
{
    /// <summary>The JSON property name the API uses for the pricing block.</summary>
    private const string PricingProperty = "pricing";

    /// <summary>
    /// Options with pricing omitted from <see cref="Card"/>. Built once: a
    /// <see cref="JsonSerializerOptions"/> caches its resolved contracts, so
    /// creating one per request would rebuild every type's metadata and cost far
    /// more than the block it is trying to skip.
    /// </summary>
    private static readonly JsonSerializerOptions WithoutPricing = BuildWithoutPricing();

    /// <summary>
    /// Returns the options matching <paramref name="options"/>.
    /// </summary>
    /// <param name="options">The client options in force.</param>
    /// <returns>
    /// The shared source-generated options, or a variant with the pricing
    /// property removed from <see cref="Card"/>.
    /// </returns>
    internal static JsonSerializerOptions For(TcgDexOptions options)
    {
        Guard.NotNull(options);

        return options.DeserializePricing
            ? TcgDexJsonContext.Default.Options
            : WithoutPricing;
    }

    private static JsonSerializerOptions BuildWithoutPricing()
    {
        IJsonTypeInfoResolver resolver = ((IJsonTypeInfoResolver)TcgDexJsonContext.Default)
            .WithAddedModifier(static typeInfo =>
            {
                if (typeInfo.Type != typeof(Card))
                {
                    return;
                }

                foreach (JsonPropertyInfo property in typeInfo.Properties)
                {
                    if (string.Equals(property.Name, PricingProperty, StringComparison.Ordinal))
                    {
                        property.CustomConverter = SkippedPricingConverter.Instance;
                    }
                }
            });

        // Copied from the generated context rather than constructed fresh, so
        // the naming policy, case-insensitivity and converters stay identical —
        // this option changes one property, not the serializer's behaviour.
        return new JsonSerializerOptions(TcgDexJsonContext.Default.Options)
        {
            TypeInfoResolver = resolver,
        };
    }

    /// <summary>Reads the pricing block without building anything from it.</summary>
    private sealed class SkippedPricingConverter : JsonConverter<Pricing?>
    {
        internal static readonly SkippedPricingConverter Instance = new();

        public override Pricing? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            // Skip advances past the whole value, however deeply nested. It is
            // a no-op on a null token, which is the shape for a card the API has
            // no prices for, so both cases land on the same answer.
            reader.Skip();

            return null;
        }

        /// <summary>
        /// Writes <see langword="null"/>, matching what was read.
        /// </summary>
        /// <remarks>
        /// Serialization is not a path this SDK exercises — it reads an API it
        /// does not write to — but a converter that threw here would turn any
        /// caller's attempt to serialize a <see cref="Card"/> into a crash, and
        /// a round trip through this contract genuinely has no pricing to emit.
        /// </remarks>
        public override void Write(Utf8JsonWriter writer, Pricing? value, JsonSerializerOptions options)
        {
            Guard.NotNull(writer);

            writer.WriteNullValue();
        }
    }
}

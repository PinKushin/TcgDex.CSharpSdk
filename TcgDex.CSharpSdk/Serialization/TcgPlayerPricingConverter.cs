namespace TcgDex.Serialization;

using TcgDex.Models;

/// <summary>
/// Splits the TCGplayer pricing object into its two fixed fields and a
/// dictionary of per-printing price blocks.
/// </summary>
/// <remarks>
/// The object mixes metadata (<c>unit</c>, <c>updated</c>) with one nested
/// object per printing, and the printing names vary by card — <c>normal</c> and
/// <c>reverse-holofoil</c> on <c>swsh3-136</c>, <c>holofoil</c> on
/// <c>base1-4</c>. Modelling those as fixed properties would silently drop any
/// printing not anticipated here, so every unrecognised property is collected
/// into <see cref="TcgPlayerPricing.Printings"/> instead.
/// </remarks>
internal sealed class TcgPlayerPricingConverter : JsonConverter<TcgPlayerPricing>
{
    private const string UnitProperty = "unit";
    private const string UpdatedProperty = "updated";

    /// <summary>The two metadata keys as UTF-8, for allocation-free matching.</summary>
    private static readonly byte[] UnitPropertyUtf8 = System.Text.Encoding.UTF8.GetBytes(UnitProperty);

    /// <inheritdoc cref="UnitPropertyUtf8" />
    private static readonly byte[] UpdatedPropertyUtf8 = System.Text.Encoding.UTF8.GetBytes(UpdatedProperty);

    /// <inheritdoc />
    public override TcgPlayerPricing? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // No null check: HandleNull defaults to false, so System.Text.Json
        // handles a null value itself and never invokes this converter for one.
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected an object for TCGplayer pricing but found {reader.TokenType}.");
        }

        string? unit = null;
        DateTimeOffset? updated = null;

        // No capacity hint. Sizing it for four printings was measured and made
        // allocations worse, not better — a card carries one to three, and the
        // extra buckets cost more than the resize they avoided.
        Dictionary<string, TcgPlayerPrice> printings = new(StringComparer.Ordinal);

        // Resolved once rather than per printing.
        JsonTypeInfo<TcgPlayerPrice> priceTypeInfo = PriceTypeInfo(options);

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            // Utf8JsonReader guarantees a PropertyName here: the loop exits on
            // EndObject, and malformed JSON fails inside the reader before
            // reaching this point.
            //
            // The two metadata keys are matched against UTF-8 without decoding
            // them. GetString allocates, and for `unit` and `updated` the
            // string is only ever compared and thrown away — so it is called
            // below for printing names alone, where the value is genuinely
            // needed as a dictionary key.
            bool isUnit = reader.ValueTextEquals(UnitPropertyUtf8);
            bool isUpdated = !isUnit && reader.ValueTextEquals(UpdatedPropertyUtf8);
            string? name = isUnit || isUpdated ? null : reader.GetString()!;

            reader.Read();

            if (isUnit)
            {
                unit = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                continue;
            }

            if (isUpdated)
            {
                updated = reader.TokenType == JsonTokenType.Null
                    ? null
                    : reader.GetDateTimeOffset();
                continue;
            }

            // Any other property is a printing name, and its value is a price
            // block (or null when the source has no data for it).
            if (reader.TokenType == JsonTokenType.Null)
            {
                continue;
            }

            TcgPlayerPrice? price = JsonSerializer.Deserialize(ref reader, priceTypeInfo);

            if (price is not null)
            {
                printings[name!] = price;
            }
        }

        return new TcgPlayerPricing
        {
            Unit = unit,
            Updated = updated,
            Printings = printings,
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TcgPlayerPricing value, JsonSerializerOptions options)
    {
        Guard.NotNull(writer);
        Guard.NotNull(value);

        writer.WriteStartObject();

        if (value.Unit is not null)
        {
            writer.WriteString(UnitProperty, value.Unit);
        }

        if (value.Updated is { } updated)
        {
            writer.WriteString(UpdatedProperty, updated);
        }

        // KeyValuePair<,> gained Deconstruct in .NET Core 2.0 but not in
        // netstandard2.0, so the pair is read through its properties.
        foreach (KeyValuePair<string, TcgPlayerPrice> printing in value.Printings)
        {
            writer.WritePropertyName(printing.Key);
            JsonSerializer.Serialize(writer, printing.Value, PriceTypeInfo(options));
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Resolves the price-block metadata from the caller's options rather than
    /// from a fixed context, so the converter stays AOT-safe without depending
    /// on the generated context that references it.
    /// </summary>
    private static JsonTypeInfo<TcgPlayerPrice> PriceTypeInfo(JsonSerializerOptions options)
    {
        Guard.NotNull(options);

        return (JsonTypeInfo<TcgPlayerPrice>)options.GetTypeInfo(typeof(TcgPlayerPrice));
    }
}

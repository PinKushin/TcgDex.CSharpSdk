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
public sealed class TcgPlayerPricingConverter : JsonConverter<TcgPlayerPricing>
{
    private const string UnitProperty = "unit";
    private const string UpdatedProperty = "updated";

    /// <inheritdoc />
    public override TcgPlayerPricing? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected an object for TCGplayer pricing but found {reader.TokenType}.");
        }

        string? unit = null;
        DateTimeOffset? updated = null;
        var printings = new Dictionary<string, TcgPlayerPrice>(StringComparer.Ordinal);

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected a property name but found {reader.TokenType}.");
            }

            var name = reader.GetString()!;
            reader.Read();

            switch (name)
            {
                case UnitProperty:
                    unit = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;

                case UpdatedProperty:
                    updated = reader.TokenType == JsonTokenType.Null
                        ? null
                        : reader.GetDateTimeOffset();
                    break;

                default:
                    // Any other property is a printing name, and its value is a
                    // price block (or null when the source has no data for it).
                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        break;
                    }

                    var price = JsonSerializer.Deserialize(ref reader, PriceTypeInfo(options));

                    if (price is not null)
                    {
                        printings[name] = price;
                    }

                    break;
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
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();

        if (value.Unit is not null)
        {
            writer.WriteString(UnitProperty, value.Unit);
        }

        if (value.Updated is { } updated)
        {
            writer.WriteString(UpdatedProperty, updated);
        }

        foreach (var (printing, price) in value.Printings)
        {
            writer.WritePropertyName(printing);
            JsonSerializer.Serialize(writer, price, PriceTypeInfo(options));
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
        ArgumentNullException.ThrowIfNull(options);

        return (JsonTypeInfo<TcgPlayerPrice>)options.GetTypeInfo(typeof(TcgPlayerPrice));
    }
}

namespace TcgDex.Serialization;

/// <summary>
/// Reads a JSON value that the API sends as either a string or a number, and
/// surfaces it as text.
/// </summary>
/// <remarks>
/// <para>
/// <c>attacks[].damage</c> is genuinely polymorphic: <c>xy1-1</c> returns the
/// number <c>60</c> while <c>swsh1-1</c> returns the string <c>"50+"</c>,
/// because the printed damage can carry a modifier. Typing the property as a
/// number therefore throws on a large share of real cards.
/// </para>
/// <para>
/// Text is the lossless representation — it keeps the <c>+</c> and <c>×</c>
/// modifiers — and matches how the GraphQL schema declares the same field.
/// </para>
/// </remarks>
internal sealed class FlexibleStringConverter : JsonConverter<string?>
{
    // No JsonTokenType.Null case: HandleNull defaults to false, so
    // System.Text.Json deals with null itself and never invokes this converter
    // for one. A case here would be unreachable.

    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => ReadNumber(ref reader),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => throw new JsonException(
                $"Expected a string, number or null but found {reader.TokenType}."),
        };

    private static string ReadNumber(ref Utf8JsonReader reader)
        => reader.TryGetInt64(out var whole)
            ? whole.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : reader.GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        Guard.NotNull(writer);

        // Likewise never called with null, so this writes the value directly.
        writer.WriteStringValue(value);
    }
}

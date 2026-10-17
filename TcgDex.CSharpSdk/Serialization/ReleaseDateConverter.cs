namespace TcgDex.Serialization;

using System.Globalization;

/// <summary>
/// Reads a <c>yyyy-MM-dd</c> release date as a <see cref="DateTime"/> of kind
/// <see cref="DateTimeKind.Unspecified"/>, and anything else as
/// <see langword="null"/>.
/// </summary>
/// <remarks>
/// <para>
/// Measured 2026-10-17: all 606 sets (en 220, fr 202, ja 184) carry a full
/// <c>yyyy-MM-dd</c>. Tolerance is still the contract — a malformed date on one
/// set must not fail a whole set or series listing — so an unparsable, partial
/// or non-string value becomes <see langword="null"/> rather than a throw.
/// </para>
/// <para>
/// <see cref="DateTime"/> rather than <c>DateOnly</c>, because netstandard2.0
/// lacks <c>DateOnly</c> and the public API is the same type on every target.
/// </para>
/// </remarks>
internal sealed class ReleaseDateConverter : JsonConverter<DateTime?>
{
    private const string Format = "yyyy-MM-dd";

    /// <inheritdoc />
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && DateTime.TryParseExact(reader.GetString(), Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
        {
            return date;
        }

        // Walks past an object or array; a no-op on a scalar.
        reader.Skip();
        return null;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        Guard.NotNull(writer);

        if (value is { } date)
        {
            writer.WriteStringValue(date.ToString(Format, CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

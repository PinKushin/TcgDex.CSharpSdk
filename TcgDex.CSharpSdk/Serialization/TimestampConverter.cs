namespace TcgDex.Serialization;

/// <summary>
/// Reads an ISO 8601 timestamp as a <see cref="DateTimeOffset"/>, and anything
/// else as <see langword="null"/>.
/// </summary>
/// <remarks>
/// The same tolerance as <see cref="ReleaseDateConverter"/>: an <c>updated</c>
/// value that cannot be read must not fail the card, or the pricing block, that
/// carries it. An unparsable, empty or non-string value becomes
/// <see langword="null"/> rather than a throw.
/// </remarks>
internal sealed class TimestampConverter : JsonConverter<DateTimeOffset?>
{
    /// <inheritdoc />
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ReadTimestamp(ref reader);

    /// <summary>
    /// Reads the current token as a timestamp, or <see langword="null"/>, leaving
    /// the reader on the token's last byte. Shared with converters that read a
    /// timestamp inside an object they parse by hand.
    /// </summary>
    internal static DateTimeOffset? ReadTimestamp(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out DateTimeOffset timestamp))
        {
            return timestamp;
        }

        // Walks past an object or array; a no-op on a scalar.
        reader.Skip();
        return null;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        Guard.NotNull(writer);

        // No null branch: HandleNull defaults to false for Nullable<T>, so
        // System.Text.Json writes null itself and never passes one here.
        writer.WriteStringValue(value.GetValueOrDefault());
    }
}

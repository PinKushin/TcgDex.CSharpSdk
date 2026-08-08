namespace TcgDex.Models;

/// <summary>
/// The reduced card shape returned by list endpoints and by a set's card list.
/// </summary>
/// <remarks>
/// List responses carry only these four fields. <see cref="Card.Category"/>,
/// <see cref="Card.Rarity"/> and <see cref="Card.TrainerType"/> are <em>not</em>
/// included — fetch the full card by <see cref="Id"/> to read them.
/// </remarks>
public sealed record CardBrief
{
    /// <summary>
    /// The card identifier, in <c>{setId}-{localId}</c> form. Not always
    /// URL-safe as printed: some ids arrive percent-encoded, such as
    /// <c>"exu-%3F"</c>.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The card's number within its set. Not necessarily numeric — values such
    /// as <c>"!"</c> and <c>"%3F"</c> occur in older promo sets.
    /// </summary>
    /// <remarks>
    /// TCGdex documents this as "String or Number", so an unquoted value is read
    /// as text rather than throwing. Every card the API currently serves quotes
    /// it; the converter is here because this property is <c>required</c>, so one
    /// unquoted value would fail the entire card.
    /// </remarks>
    [JsonConverter(typeof(Serialization.FlexibleStringConverter))]
    public required string LocalId { get; init; }

    /// <summary>The card's printed name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Base image URL without a file extension, or <see langword="null"/> when
    /// the card has no artwork on record.
    /// </summary>
    public string? Image { get; init; }
}

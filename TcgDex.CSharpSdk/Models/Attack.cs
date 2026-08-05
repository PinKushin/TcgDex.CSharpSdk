namespace TcgDex.Models;

/// <summary>
/// An attack printed on a Pokémon card.
/// </summary>
public sealed record Attack
{
    // See Card for why collections need a backing field rather than an
    // initializer: the JSON source generator discards initializers.
    private readonly IReadOnlyList<string> _cost = [];

    /// <summary>The attack's printed name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The energy required, one entry per energy symbol — for example
    /// <c>["Grass", "Grass", "Colorless"]</c>. These are energy type
    /// <em>names</em>, not a count.
    /// </summary>
    public IReadOnlyList<string> Cost
    {
        get => _cost;
        init => _cost = value ?? [];
    }

    /// <summary>
    /// The printed damage, kept verbatim as text.
    /// </summary>
    /// <remarks>
    /// The API returns this field as either a JSON number (<c>60</c>) or a JSON
    /// string (<c>"50+"</c>) depending on the card, so it is normalised to text
    /// to avoid losing the modifier. Use <see cref="BaseDamage"/> for the
    /// numeric part. Attacks that only apply an effect have no damage at all.
    /// </remarks>
    [JsonConverter(typeof(Serialization.FlexibleStringConverter))]
    public string? Damage { get; init; }

    /// <summary>The attack's rules text, if any.</summary>
    public string? Effect { get; init; }

    /// <summary>
    /// The leading numeric portion of <see cref="Damage"/> — <c>50</c> for
    /// <c>"50+"</c> — or <see langword="null"/> when the damage is absent or
    /// carries no leading number (for example <c>"×"</c>).
    /// </summary>
    [JsonIgnore]
    public int? BaseDamage
    {
        get
        {
            if (string.IsNullOrEmpty(Damage))
            {
                return null;
            }

            var length = 0;
            while (length < Damage.Length && char.IsAsciiDigit(Damage[length]))
            {
                length++;
            }

            return length == 0
                ? null
                : int.Parse(Damage.AsSpan(0, length), provider: System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

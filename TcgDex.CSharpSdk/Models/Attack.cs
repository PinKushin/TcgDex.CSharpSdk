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
            // Read once into a local: the property is nullable, and the
            // netstandard2.0 reference assembly does not annotate
            // string.IsNullOrEmpty with [NotNullWhen(false)], so an explicit
            // null test is what keeps the loop below provably safe on every
            // target.
            var damage = Damage;
            if (damage is null || damage.Length == 0)
            {
                return null;
            }

            // Deliberately an ASCII range test rather than char.IsDigit: the
            // latter accepts every Unicode decimal digit, which int.Parse would
            // then reject. char.IsAsciiDigit itself is .NET 7+, and this also
            // builds for netstandard2.0.
            var length = 0;
            while (length < damage.Length && damage[length] is >= '0' and <= '9')
            {
                length++;
            }

            if (length == 0)
            {
                return null;
            }

#if NETSTANDARD2_0
            // No span-based int.Parse overload here, so this allocates a
            // substring. CA1846 would prefer AsSpan and is right on the targets
            // where it applies — hence the split rather than a suppression.
            return int.Parse(
                damage.Substring(0, length),
                System.Globalization.CultureInfo.InvariantCulture);
#else
            return int.Parse(
                damage.AsSpan(0, length),
                provider: System.Globalization.CultureInfo.InvariantCulture);
#endif
        }
    }
}

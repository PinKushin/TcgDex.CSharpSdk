namespace TcgDex.Models;

/// <summary>
/// A full card, as returned by the single-card endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Which fields are populated depends on <see cref="Category"/>. Pokémon carry
/// <see cref="HitPoints"/>, <see cref="Types"/>, <see cref="Attacks"/> and the rest of
/// the battle data; Trainers carry <see cref="TrainerType"/> and
/// <see cref="Effect"/>; Energy cards carry <see cref="EnergyType"/>. Anything
/// category-specific is nullable because the API omits it entirely rather than
/// sending a null.
/// </para>
/// <para>
/// The API has no <c>type</c> field for a card's kind — that is
/// <see cref="Category"/>. <see cref="Types"/> is a different thing: a Pokémon's
/// elemental types.
/// </para>
/// </remarks>
public sealed record Card
{
    // System.Text.Json's source generator does not apply property initializers,
    // so a `= []` default is silently discarded and an omitted JSON array
    // arrives as null. These backing fields keep every collection non-null for
    // an absent property, an explicit JSON null, and a null passed by a caller.
    //
    // Each is a ValueList, so the record's own equality compares the items
    // rather than the list instances (DECISIONS #40).
    private readonly IReadOnlyList<DetailedVariant> _detailedVariants = ValueList<DetailedVariant>.Empty;
    private readonly IReadOnlyList<Booster> _boosters = ValueList<Booster>.Empty;
    private readonly IReadOnlyList<string> _types = ValueList<string>.Empty;
    private readonly IReadOnlyList<int> _dexIds = ValueList<int>.Empty;
    private readonly IReadOnlyList<Attack> _attacks = ValueList<Attack>.Empty;
    private readonly IReadOnlyList<Ability> _abilities = ValueList<Ability>.Empty;
    private readonly IReadOnlyList<WeaknessOrResistance> _weaknesses = ValueList<WeaknessOrResistance>.Empty;
    private readonly IReadOnlyList<WeaknessOrResistance> _resistances = ValueList<WeaknessOrResistance>.Empty;

    // ----- always present -----

    /// <summary>The card identifier, in <c>{setId}-{localId}</c> form.</summary>
    public required string Id { get; init; }

    /// <summary>The card's printed name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The card's kind: <c>"Pokemon"</c>, <c>"Trainer"</c> or <c>"Energy"</c>.
    /// See <see cref="CardCategories"/> for the known values.
    /// </summary>
    /// <remarks>
    /// Kept as text rather than an enum so that a category added by the API in
    /// future deserializes instead of throwing.
    /// </remarks>
    public required string Category { get; init; }

    /// <summary>The card's number within its set. Not necessarily numeric.</summary>
    /// <remarks>
    /// TCGdex documents this as "String or Number", so an unquoted value is read
    /// as text rather than throwing. Every card the API currently serves quotes
    /// it; the converter is here because this property is <c>required</c>, so one
    /// unquoted value would fail the entire card.
    /// </remarks>
    [JsonConverter(typeof(Serialization.FlexibleStringConverter))]
    public required string LocalId { get; init; }

    /// <summary>The set this card belongs to.</summary>
    public required CardSetBrief Set { get; init; }

    /// <summary>Which printings of this card exist.</summary>
    public Variants? Variants { get; init; }

    /// <summary>Per-printing detail, including pricing for each printing.</summary>
    /// <remarks>Empty rather than <see langword="null"/> when the API omits it.</remarks>
    [JsonPropertyName("variants_detailed")]
    public IReadOnlyList<DetailedVariant> DetailedVariants
    {
        get => _detailedVariants;
        init => _detailedVariants = ValueList<DetailedVariant>.From(value);
    }

    /// <summary>
    /// When this card's record was last updated; <see langword="null"/> when
    /// absent or not a readable timestamp.
    /// </summary>
    [JsonConverter(typeof(Serialization.TimestampConverter))]
    public DateTimeOffset? Updated { get; init; }

    // ----- common but not guaranteed -----

    /// <summary>The illustrator's name.</summary>
    public string? Illustrator { get; init; }

    /// <summary>
    /// Base image URL without a file extension, or <see langword="null"/> when
    /// the card has no artwork on record.
    /// </summary>
    public string? Image { get; init; }

    /// <summary>The card's rarity, for example <c>"Common"</c>.</summary>
    public string? Rarity { get; init; }

    /// <summary>Market pricing across the whole card.</summary>
    public Pricing? Pricing { get; init; }

    /// <summary>
    /// External marketplace product ids for the card as a whole, when the API
    /// has no per-printing breakdown to hang them on instead. See
    /// <see cref="Models.ThirdParty"/> for how this relates to the same ids on
    /// each <see cref="DetailedVariant"/>.
    /// </summary>
    public ThirdParty? ThirdParty { get; init; }

    /// <summary>Tournament legality.</summary>
    [JsonPropertyName("legal")]
    public Legality? Legality { get; init; }

    /// <summary>The regulation mark letter, on modern cards.</summary>
    public string? RegulationMark { get; init; }

    /// <summary>Boosters this card can be pulled from.</summary>
    public IReadOnlyList<Booster> Boosters
    {
        get => _boosters;
        init => _boosters = ValueList<Booster>.From(value);
    }

    /// <summary>
    /// The card's evolution stage, for example <c>"Basic"</c> or <c>"Stage1"</c>.
    /// Not exclusive to Pokémon — Energy cards carry it too.
    /// </summary>
    public string? Stage { get; init; }

    // ----- Pokémon -----

    /// <summary>Hit points.</summary>
    [JsonPropertyName("hp")]
    public int? HitPoints { get; init; }

    /// <summary>Elemental types, for example <c>["Grass"]</c>.</summary>
    public IReadOnlyList<string> Types
    {
        get => _types;
        init => _types = ValueList<string>.From(value);
    }

    /// <summary>National Pokédex numbers this card depicts.</summary>
    [JsonPropertyName("dexId")]
    public IReadOnlyList<int> DexIds
    {
        get => _dexIds;
        init => _dexIds = ValueList<int>.From(value);
    }

    /// <summary>The Pokémon this one evolves from.</summary>
    [JsonPropertyName("evolveFrom")]
    public string? EvolvesFrom { get; init; }

    /// <summary>Flavour text.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// The card's suffix, for example <c>"EX"</c> or <c>"ex"</c>. Case is
    /// meaningful — the two denote different eras.
    /// </summary>
    public string? Suffix { get; init; }

    /// <summary>The card's attacks.</summary>
    public IReadOnlyList<Attack> Attacks
    {
        get => _attacks;
        init => _attacks = ValueList<Attack>.From(value);
    }

    /// <summary>The card's abilities.</summary>
    public IReadOnlyList<Ability> Abilities
    {
        get => _abilities;
        init => _abilities = ValueList<Ability>.From(value);
    }

    /// <summary>Types this Pokémon is weak to.</summary>
    public IReadOnlyList<WeaknessOrResistance> Weaknesses
    {
        get => _weaknesses;
        init => _weaknesses = ValueList<WeaknessOrResistance>.From(value);
    }

    /// <summary>Types this Pokémon resists.</summary>
    public IReadOnlyList<WeaknessOrResistance> Resistances
    {
        get => _resistances;
        init => _resistances = ValueList<WeaknessOrResistance>.From(value);
    }

    /// <summary>The retreat cost, in energy.</summary>
    [JsonPropertyName("retreat")]
    public int? RetreatCost { get; init; }

    // ----- Trainer -----

    /// <summary>
    /// The Trainer subtype, for example <c>"Item"</c>, <c>"Supporter"</c>,
    /// <c>"Tool"</c> or <c>"Stadium"</c>.
    /// </summary>
    public string? TrainerType { get; init; }

    /// <summary>The card's rules text.</summary>
    public string? Effect { get; init; }

    // ----- Energy -----

    /// <summary>Whether this is <c>"Normal"</c> or <c>"Special"</c> energy.</summary>
    public string? EnergyType { get; init; }

    /// <summary>
    /// Whether the card's <see cref="Category"/> matches <paramref name="category"/>,
    /// ignoring case.
    /// </summary>
    /// <param name="category">The category to test, such as <c>"Pokemon"</c>.</param>
    /// <returns><see langword="true"/> when the categories match.</returns>
    public bool IsCategory(string category)
        => string.Equals(Category, category, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The card categories the API currently returns.
/// </summary>
/// <remarks>
/// Provided as constants rather than an enum so that an unrecognised category
/// still deserializes. Compare against <see cref="Card.Category"/> with
/// <see cref="Card.IsCategory"/>.
/// </remarks>
public static class CardCategories
{
    /// <summary>A Pokémon card.</summary>
    public const string Pokemon = "Pokemon";

    /// <summary>A Trainer card.</summary>
    public const string Trainer = "Trainer";

    /// <summary>An Energy card.</summary>
    public const string Energy = "Energy";
}

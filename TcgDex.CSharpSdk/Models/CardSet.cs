namespace TcgDex.Models;

/// <summary>
/// How many cards a set contains, broken down by printing where the API
/// reports it.
/// </summary>
public sealed record CardCount
{
    /// <summary>The set's official size, as printed on the cards.</summary>
    public int Official { get; init; }

    /// <summary>Total cards including secret rares and promos.</summary>
    public int Total { get; init; }

    /// <summary>Count of cards with a normal printing.</summary>
    public int? Normal { get; init; }

    /// <summary>Count of cards with a holofoil printing.</summary>
    public int? Holo { get; init; }

    /// <summary>Count of cards with a reverse-holofoil printing.</summary>
    public int? Reverse { get; init; }

    /// <summary>Count of cards with a first-edition printing.</summary>
    public int? FirstEd { get; init; }
}

/// <summary>
/// The abbreviated set reference embedded in a card.
/// </summary>
public sealed record SetBrief
{
    /// <summary>The set identifier, for example <c>"swsh3"</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The set's display name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Logo image URL, without file extension — append a quality and format
    /// before requesting it.
    /// </summary>
    public string? Logo { get; init; }

    /// <summary>Symbol image URL, without file extension. Served language-neutral.</summary>
    public string? Symbol { get; init; }

    /// <summary>The set's card counts.</summary>
    public CardCount? CardCount { get; init; }
}

/// <summary>
/// A full set, as returned by the single-set endpoint. Includes its card list.
/// </summary>
public sealed record Set
{
    // See Card for why collections need a backing field rather than an
    // initializer: the JSON source generator discards initializers.
    private readonly IReadOnlyList<CardBrief> _cards = [];

    /// <summary>The set identifier, for example <c>"swsh3"</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The set's display name.</summary>
    public required string Name { get; init; }

    /// <summary>The set's short code, when it has one.</summary>
    public SetAbbreviation? Abbreviation { get; init; }

    /// <summary>Logo image URL, without file extension.</summary>
    public string? Logo { get; init; }

    /// <summary>Symbol image URL, without file extension.</summary>
    public string? Symbol { get; init; }

    /// <summary>The set's card counts.</summary>
    public CardCount? CardCount { get; init; }

    /// <summary>Release date in <c>yyyy-MM-dd</c> form.</summary>
    public string? ReleaseDate { get; init; }

    /// <summary>Tournament legality for the set as a whole.</summary>
    public Legality? Legal { get; init; }

    /// <summary>The series this set belongs to.</summary>
    public SerieBrief? Serie { get; init; }

    /// <summary>The set's code in Pokémon TCG Online, when it has one.</summary>
    public string? TcgOnline { get; init; }

    /// <summary>The cards in this set, in brief form.</summary>
    public IReadOnlyList<CardBrief> Cards
    {
        get => _cards;
        init => _cards = value ?? [];
    }
}

/// <summary>
/// A set's official and localized short codes.
/// </summary>
public sealed record SetAbbreviation
{
    /// <summary>The official abbreviation.</summary>
    public string? Official { get; init; }

    /// <summary>The localized abbreviation for the requested language.</summary>
    public string? Localized { get; init; }
}

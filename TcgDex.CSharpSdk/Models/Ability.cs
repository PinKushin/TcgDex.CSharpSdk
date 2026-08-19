namespace TcgDex.Models;

/// <summary>
/// An ability printed on a Pokémon card.
/// </summary>
public sealed record Ability
{
    /// <summary>
    /// The ability's printed name, or <see langword="null"/> when the API omits
    /// it. Not <c>required</c>, for the same reason as <see cref="Attack.Name"/>:
    /// TCGdex ships malformed card records (a nameless attack on <c>2017sm-5</c>
    /// is the confirmed case), and an ability is the same kind of descriptive
    /// nested object, so the SDK degrades to a null name rather than making the
    /// whole card unreadable. A missing name is surfaced through a warning log,
    /// not by rejecting the card.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// The era-specific label for the ability — observed values include
    /// <c>"Ability"</c>, <c>"Pokemon Power"</c> and <c>"Poke-BODY"</c>. Treated
    /// as free text because the set grows with each era.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>The ability's rules text.</summary>
    public string? Effect { get; init; }
}

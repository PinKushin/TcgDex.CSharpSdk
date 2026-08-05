namespace TcgDex.Models;

/// <summary>
/// An ability printed on a Pokémon card.
/// </summary>
public sealed record Ability
{
    /// <summary>The ability's printed name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The era-specific label for the ability — observed values include
    /// <c>"Ability"</c>, <c>"Pokemon Power"</c> and <c>"Poke-BODY"</c>. Treated
    /// as free text because the set grows with each era.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>The ability's rules text.</summary>
    public string? Effect { get; init; }
}

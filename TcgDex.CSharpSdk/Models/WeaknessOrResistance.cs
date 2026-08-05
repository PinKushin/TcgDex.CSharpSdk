namespace TcgDex.Models;

/// <summary>
/// A weakness or resistance entry on a Pokémon card.
/// </summary>
public sealed record WeaknessOrResistance
{
    /// <summary>The energy type this applies to, for example <c>"Fighting"</c>.</summary>
    public required string Type { get; init; }

    /// <summary>
    /// The modifier as printed. Always text, never numeric — observed values
    /// include <c>"×2"</c> (a multiplier) and <c>"-20"</c> (a signed offset),
    /// which is why this is not exposed as a number.
    /// </summary>
    public string? Value { get; init; }
}

namespace TcgDex.Models;

/// <summary>
/// Tournament format legality for a card or set.
/// </summary>
public sealed record Legality
{
    /// <summary>Whether the card or set is legal in the Standard format.</summary>
    public bool Standard { get; init; }

    /// <summary>Whether the card or set is legal in the Expanded format.</summary>
    public bool Expanded { get; init; }
}

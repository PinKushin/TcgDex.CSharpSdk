namespace TcgDex.Models;

/// <summary>
/// A booster pack a card can be pulled from.
/// </summary>
/// <remarks>
/// Only <see cref="Id"/> and <see cref="Name"/> have been observed in REST
/// responses; the remaining fields are declared by the GraphQL schema and are
/// treated as optional.
/// </remarks>
public sealed record Booster
{
    /// <summary>Stable identifier, for example <c>"boo_A4-ho-oh"</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The booster's display name.</summary>
    public string? Name { get; init; }

    /// <summary>Logo image URL.</summary>
    public string? Logo { get; init; }

    /// <summary>Front artwork URL.</summary>
    [JsonPropertyName("artwork_front")]
    public string? ArtworkFront { get; init; }

    /// <summary>Back artwork URL.</summary>
    [JsonPropertyName("artwork_back")]
    public string? ArtworkBack { get; init; }
}

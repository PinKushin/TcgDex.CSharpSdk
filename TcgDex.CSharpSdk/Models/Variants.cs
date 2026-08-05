namespace TcgDex.Models;

/// <summary>
/// Which printings of a card exist. Each flag corresponds to a value from the
/// <c>/variants</c> endpoint.
/// </summary>
public sealed record Variants
{
    /// <summary>A standard non-foil printing exists.</summary>
    public bool Normal { get; init; }

    /// <summary>A reverse-holofoil printing exists.</summary>
    public bool Reverse { get; init; }

    /// <summary>A holofoil printing exists.</summary>
    public bool Holo { get; init; }

    /// <summary>A first-edition printing exists.</summary>
    public bool FirstEdition { get; init; }

    /// <summary>A W-Promo printing exists.</summary>
    public bool WPromo { get; init; }
}

/// <summary>
/// A single concrete printing of a card, with its own identifier and pricing.
/// </summary>
/// <remarks>
/// Richer than <see cref="Variants"/>: one card can have several entries of the
/// same <see cref="Type"/> that differ by stamp or foil treatment and carry
/// different prices. Note that <see cref="VariantId"/> and <see cref="Pricing"/>
/// are returned by REST but are absent from the GraphQL schema.
/// </remarks>
public sealed record DetailedVariant
{
    // See Card for why collections need a backing field rather than an
    // initializer: the JSON source generator discards initializers.
    private readonly IReadOnlyList<string> _stamp = [];

    /// <summary>The variant name, matching a <see cref="Variants"/> flag — for example <c>"normal"</c>.</summary>
    public string? Type { get; init; }

    /// <summary>A finer-grained classification within <see cref="Type"/>, when present.</summary>
    public string? Subtype { get; init; }

    /// <summary>The physical card size, for example <c>"standard"</c>.</summary>
    public string? Size { get; init; }

    /// <summary>Stamps applied to this printing, for example <c>["set-logo"]</c>.</summary>
    public IReadOnlyList<string> Stamp
    {
        get => _stamp;
        init => _stamp = value ?? [];
    }

    /// <summary>The foil treatment, when this printing has one.</summary>
    public string? Foil { get; init; }

    /// <summary>Stable identifier for this specific printing. REST only.</summary>
    public string? VariantId { get; init; }

    /// <summary>Market pricing for this specific printing. REST only.</summary>
    public Pricing? Pricing { get; init; }
}

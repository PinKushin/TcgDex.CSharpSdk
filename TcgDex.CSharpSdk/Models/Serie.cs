namespace TcgDex.Models;

/// <summary>
/// The abbreviated series reference returned by the series list endpoint and
/// embedded in a set.
/// </summary>
public sealed record SerieBrief
{
    /// <summary>The series identifier, for example <c>"swsh"</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The series' display name.</summary>
    public required string Name { get; init; }

    /// <summary>Logo image URL, without file extension.</summary>
    public string? Logo { get; init; }
}

/// <summary>
/// A full series, as returned by the single-series endpoint. Includes its sets.
/// </summary>
public sealed record Serie
{
    // See Card for why collections need a backing field rather than an
    // initializer: the JSON source generator discards initializers.
    private readonly IReadOnlyList<SetBrief> _sets = [];

    /// <summary>The series identifier, for example <c>"swsh"</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The series' display name.</summary>
    public required string Name { get; init; }

    /// <summary>Logo image URL, without file extension.</summary>
    public string? Logo { get; init; }

    /// <summary>Release date of the series' first set, in <c>yyyy-MM-dd</c> form.</summary>
    public string? ReleaseDate { get; init; }

    /// <summary>The earliest set in the series.</summary>
    public SetBrief? FirstSet { get; init; }

    /// <summary>The most recent set in the series.</summary>
    public SetBrief? LastSet { get; init; }

    /// <summary>The sets belonging to this series.</summary>
    public IReadOnlyList<SetBrief> Sets
    {
        get => _sets;
        init => _sets = value ?? [];
    }
}

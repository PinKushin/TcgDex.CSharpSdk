namespace TcgDex.Models;

/// <summary>
/// The abbreviated series reference returned by the series list endpoint and
/// embedded in a set.
/// </summary>
public sealed record SeriesBrief
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
public sealed record Series
{
    // See Card for why collections need a backing field rather than an
    // initializer: the JSON source generator discards initializers.
    private readonly IReadOnlyList<CardSetBrief> _sets = [];

    /// <summary>The series identifier, for example <c>"swsh"</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The series' display name.</summary>
    public required string Name { get; init; }

    /// <summary>Logo image URL, without file extension.</summary>
    public string? Logo { get; init; }

    /// <summary>
    /// Release date of the series' first set, as a date of kind
    /// <see cref="DateTimeKind.Unspecified"/> with no time component;
    /// <see langword="null"/> when absent or not a full <c>yyyy-MM-dd</c> date.
    /// </summary>
    [JsonConverter(typeof(Serialization.ReleaseDateConverter))]
    public DateTime? ReleaseDate { get; init; }

    /// <summary>The earliest set in the series.</summary>
    public CardSetBrief? FirstSet { get; init; }

    /// <summary>The most recent set in the series.</summary>
    public CardSetBrief? LastSet { get; init; }

    /// <summary>The sets belonging to this series.</summary>
    public IReadOnlyList<CardSetBrief> Sets
    {
        get => _sets;
        init => _sets = value ?? [];
    }
}

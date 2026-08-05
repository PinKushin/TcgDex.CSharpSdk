namespace TcgDex.Resources;

using TcgDex.Models;

/// <summary>
/// Shared plumbing for the resource clients: each one is a thin, typed façade
/// over the same transport.
/// </summary>
/// <param name="transport">The transport used to issue requests.</param>
internal abstract class ResourceBase(TcgDexTransport transport)
{
    /// <summary>The transport used to issue requests.</summary>
    protected TcgDexTransport Transport { get; } = transport;

    /// <summary>
    /// Percent-encodes an identifier for use in a path segment.
    /// </summary>
    /// <remarks>
    /// Card ids are not always URL-safe — <c>exu-!</c> and <c>exu-%3F</c> both
    /// exist — so ids are escaped rather than concatenated.
    /// </remarks>
    /// <param name="id">The raw identifier.</param>
    /// <returns>The escaped identifier.</returns>
    protected static string EscapeId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Uri.EscapeDataString(id);
    }
}

/// <inheritdoc cref="ICardResource" />
internal sealed class CardResource(TcgDexTransport transport, GraphQlTransport graphQl)
    : ResourceBase(transport), ICardResource
{
    public Task<IReadOnlyList<Card>> SearchDetailedAsync(
        Querying.CardFilter filter,
        int? page = null,
        int? itemsPerPage = null,
        CancellationToken cancellationToken = default)
        => graphQl.SearchAsync(filter, page, itemsPerPage, cancellationToken);

    public Task<Card?> GetAsync(string id, CancellationToken cancellationToken = default)
        => Transport.GetAsync<Card>($"cards/{EscapeId(id)}", cancellationToken);

    public Task<IReadOnlyList<CardBrief>> ListAsync(CancellationToken cancellationToken = default)
        => Transport.GetRequiredAsync<IReadOnlyList<CardBrief>>("cards", cancellationToken);

    public Task<IReadOnlyList<CardBrief>> ListAsync(
        Querying.CardQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return Transport.GetRequiredAsync<IReadOnlyList<CardBrief>>(
            query.ToRelativePath(),
            cancellationToken);
    }
}

/// <inheritdoc cref="ISetResource" />
internal sealed class SetResource(TcgDexTransport transport)
    : ResourceBase(transport), ISetResource
{
    public Task<Set?> GetAsync(string id, CancellationToken cancellationToken = default)
        => Transport.GetAsync<Set>($"sets/{EscapeId(id)}", cancellationToken);

    public Task<IReadOnlyList<SetBrief>> ListAsync(CancellationToken cancellationToken = default)
        => Transport.GetRequiredAsync<IReadOnlyList<SetBrief>>("sets", cancellationToken);
}

/// <inheritdoc cref="ISerieResource" />
internal sealed class SerieResource(TcgDexTransport transport)
    : ResourceBase(transport), ISerieResource
{
    public Task<Serie?> GetAsync(string id, CancellationToken cancellationToken = default)
        => Transport.GetAsync<Serie>($"series/{EscapeId(id)}", cancellationToken);

    public Task<IReadOnlyList<SerieBrief>> ListAsync(CancellationToken cancellationToken = default)
        => Transport.GetRequiredAsync<IReadOnlyList<SerieBrief>>("series", cancellationToken);
}

/// <inheritdoc cref="IRandomResource" />
internal sealed class RandomResource(TcgDexTransport transport)
    : ResourceBase(transport), IRandomResource
{
    public Task<Card> CardAsync(CancellationToken cancellationToken = default)
        => Transport.GetRequiredAsync<Card>("random/card", cancellationToken);

    public Task<Set> SetAsync(CancellationToken cancellationToken = default)
        => Transport.GetRequiredAsync<Set>("random/set", cancellationToken);

    public Task<Serie> SerieAsync(CancellationToken cancellationToken = default)
        => Transport.GetRequiredAsync<Serie>("random/serie", cancellationToken);
}

/// <inheritdoc cref="ICatalogResource" />
/// <remarks>
/// Every method here is the same call with a different path, so the two shapes
/// the API uses — arrays of text and arrays of numbers — are expressed once
/// rather than repeated thirteen times.
/// </remarks>
internal sealed class CatalogResource(TcgDexTransport transport)
    : ResourceBase(transport), ICatalogResource
{
    public Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken cancellationToken = default)
        => Text("categories", cancellationToken);

    public Task<IReadOnlyList<string>> RaritiesAsync(CancellationToken cancellationToken = default)
        => Text("rarities", cancellationToken);

    public Task<IReadOnlyList<string>> TypesAsync(CancellationToken cancellationToken = default)
        => Text("types", cancellationToken);

    public Task<IReadOnlyList<string>> IllustratorsAsync(CancellationToken cancellationToken = default)
        => Text("illustrators", cancellationToken);

    public Task<IReadOnlyList<string>> StagesAsync(CancellationToken cancellationToken = default)
        => Text("stages", cancellationToken);

    public Task<IReadOnlyList<string>> SuffixesAsync(CancellationToken cancellationToken = default)
        => Text("suffixes", cancellationToken);

    public Task<IReadOnlyList<string>> VariantsAsync(CancellationToken cancellationToken = default)
        => Text("variants", cancellationToken);

    public Task<IReadOnlyList<string>> EnergyTypesAsync(CancellationToken cancellationToken = default)
        => Text("energy-types", cancellationToken);

    public Task<IReadOnlyList<string>> RegulationMarksAsync(CancellationToken cancellationToken = default)
        => Text("regulation-marks", cancellationToken);

    public Task<IReadOnlyList<string>> TrainerTypesAsync(CancellationToken cancellationToken = default)
        => Text("trainer-types", cancellationToken);

    public Task<IReadOnlyList<int>> HitPointsAsync(CancellationToken cancellationToken = default)
        => Numbers("hp", cancellationToken);

    public Task<IReadOnlyList<int>> RetreatCostsAsync(CancellationToken cancellationToken = default)
        => Numbers("retreats", cancellationToken);

    public Task<IReadOnlyList<int>> DexIdsAsync(CancellationToken cancellationToken = default)
        => Numbers("dex-ids", cancellationToken);

    private Task<IReadOnlyList<string>> Text(string path, CancellationToken cancellationToken)
        => Transport.GetRequiredAsync<IReadOnlyList<string>>(path, cancellationToken);

    private Task<IReadOnlyList<int>> Numbers(string path, CancellationToken cancellationToken)
        => Transport.GetRequiredAsync<IReadOnlyList<int>>(path, cancellationToken);
}

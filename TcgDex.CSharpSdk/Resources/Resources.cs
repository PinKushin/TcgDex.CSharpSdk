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
        Guard.NotNullOrWhiteSpace(id);
        return Uri.EscapeDataString(id);
    }
}

/// <inheritdoc cref="ICardResource" />
internal sealed class CardResource(TcgDexTransport transport, GraphQLTransport graphQL)
    : ResourceBase(transport), ICardResource
{
    public Task<IReadOnlyList<Card>> SearchDetailedAsync(
        Querying.CardFilter filter,
        int? page = null,
        int? itemsPerPage = null,
        CancellationToken cancellationToken = default)
        => graphQL.SearchAsync(filter, page, itemsPerPage, cancellationToken);

    public Task<Card?> GetAsync(string id, CancellationToken cancellationToken = default)
        => Transport.GetAsync<Card>($"cards/{EscapeId(id)}", cancellationToken);

    public Task<IReadOnlyList<CardBrief>> ListAsync(CancellationToken cancellationToken = default)
        => Transport.GetRequiredAsync<IReadOnlyList<CardBrief>>("cards", cancellationToken);

    public Task<IReadOnlyList<CardBrief>> ListAsync(
        Querying.CardQuery query,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(query);

        return Transport.GetRequiredAsync<IReadOnlyList<CardBrief>>(
            query.ToRelativePath(),
            cancellationToken);
    }

    public async IAsyncEnumerable<CardBrief> StreamAsync(
        Querying.CardQuery query,
        int pageSize = 100,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(query);
        Guard.NotLessThan(pageSize, 1);

        // S1994: intentionally unbounded. The API reports no total and sends no
        // pagination headers, so a short page (checked and broken on below) is
        // the only end-of-results signal there is.
#pragma warning disable S1994
        for (int page = 1; ; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<CardBrief> batch = await ListAsync(query.Page(page, pageSize), cancellationToken)
                .ConfigureAwait(false);

            foreach (CardBrief card in batch)
            {
                yield return card;
            }

            // A short page is the only end-of-results signal the API gives: it
            // reports no total and sends no pagination headers. An exactly-full
            // final page therefore costs one extra empty request, which is the
            // price of not being told how many there are.
            if (batch.Count < pageSize)
            {
                yield break;
            }
        }
#pragma warning restore S1994
    }
}

/// <inheritdoc cref="ICardSetResource" />
internal sealed class CardSetResource(TcgDexTransport transport)
    : ResourceBase(transport), ICardSetResource
{
    public Task<CardSet?> GetAsync(string id, CancellationToken cancellationToken = default)
        => Transport.GetAsync<CardSet>($"sets/{EscapeId(id)}", cancellationToken);

    public Task<IReadOnlyList<CardSetBrief>> ListAsync(CancellationToken cancellationToken = default)
        => Transport.GetRequiredAsync<IReadOnlyList<CardSetBrief>>("sets", cancellationToken);
}

/// <inheritdoc cref="ISeriesResource" />
internal sealed class SeriesResource(TcgDexTransport transport)
    : ResourceBase(transport), ISeriesResource
{
    public Task<Series?> GetAsync(string id, CancellationToken cancellationToken = default)
        => Transport.GetAsync<Series>($"series/{EscapeId(id)}", cancellationToken);

    public Task<IReadOnlyList<SeriesBrief>> ListAsync(CancellationToken cancellationToken = default)
        => Transport.GetRequiredAsync<IReadOnlyList<SeriesBrief>>("series", cancellationToken);
}

/// <inheritdoc cref="IRandomResource" />
internal sealed class RandomResource(TcgDexTransport transport)
    : ResourceBase(transport), IRandomResource
{
    public Task<Card> GetCardAsync(CancellationToken cancellationToken = default)
        => Transport.GetRequiredAsync<Card>("random/card", cancellationToken);

    public Task<CardSet> GetSetAsync(CancellationToken cancellationToken = default)
        => Transport.GetRequiredAsync<CardSet>("random/set", cancellationToken);

    public Task<Series> GetSeriesAsync(CancellationToken cancellationToken = default)
        => Transport.GetRequiredAsync<Series>("random/serie", cancellationToken);
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
    public Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken = default)
        => Text("categories", cancellationToken);

    public Task<IReadOnlyList<string>> GetRaritiesAsync(CancellationToken cancellationToken = default)
        => Text("rarities", cancellationToken);

    public Task<IReadOnlyList<string>> GetTypesAsync(CancellationToken cancellationToken = default)
        => Text("types", cancellationToken);

    public Task<IReadOnlyList<string>> GetIllustratorsAsync(CancellationToken cancellationToken = default)
        => Text("illustrators", cancellationToken);

    public Task<IReadOnlyList<string>> GetStagesAsync(CancellationToken cancellationToken = default)
        => Text("stages", cancellationToken);

    public Task<IReadOnlyList<string>> GetSuffixesAsync(CancellationToken cancellationToken = default)
        => Text("suffixes", cancellationToken);

    public Task<IReadOnlyList<string>> GetVariantsAsync(CancellationToken cancellationToken = default)
        => Text("variants", cancellationToken);

    public Task<IReadOnlyList<string>> GetEnergyTypesAsync(CancellationToken cancellationToken = default)
        => Text("energy-types", cancellationToken);

    public Task<IReadOnlyList<string>> GetRegulationMarksAsync(CancellationToken cancellationToken = default)
        => Text("regulation-marks", cancellationToken);

    public Task<IReadOnlyList<string>> GetTrainerTypesAsync(CancellationToken cancellationToken = default)
        => Text("trainer-types", cancellationToken);

    public Task<IReadOnlyList<int>> GetHitPointsAsync(CancellationToken cancellationToken = default)
        => Numbers("hp", cancellationToken);

    public Task<IReadOnlyList<int>> GetRetreatCostsAsync(CancellationToken cancellationToken = default)
        => Numbers("retreats", cancellationToken);

    public Task<IReadOnlyList<int>> GetDexIdsAsync(CancellationToken cancellationToken = default)
        => Numbers("dex-ids", cancellationToken);

    private Task<IReadOnlyList<string>> Text(string path, CancellationToken cancellationToken)
        => Transport.GetRequiredAsync<IReadOnlyList<string>>(path, cancellationToken);

    private Task<IReadOnlyList<int>> Numbers(string path, CancellationToken cancellationToken)
        => Transport.GetRequiredAsync<IReadOnlyList<int>>(path, cancellationToken);
}

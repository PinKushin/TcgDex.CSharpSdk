namespace TcgDex.Resources;

using TcgDex.Models;

/// <summary>
/// Reads cards.
/// </summary>
public interface ICardResource
{
    /// <summary>Fetches a single card by identifier.</summary>
    /// <param name="id">The card id, for example <c>"swsh3-136"</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The card, or <see langword="null"/> if no card has that id.</returns>
    Task<Card?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Lists cards in brief form.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every card, as briefs.</returns>
    /// <remarks>
    /// List responses carry only id, localId, name and image. Category, rarity
    /// and trainerType require fetching the full card.
    /// </remarks>
    Task<IReadOnlyList<CardBrief>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the cards matching a query.</summary>
    /// <param name="query">Filters, sorting and pagination.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The matching cards, as briefs.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is null.</exception>
    /// <remarks>
    /// The API reports no total count, so a page shorter than the requested size
    /// is the only signal that the results are exhausted.
    /// </remarks>
    Task<IReadOnlyList<CardBrief>> ListAsync(
        Querying.CardQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for cards and returns each one <em>fully detailed</em> in a
    /// single request, using GraphQL.
    /// </summary>
    /// <param name="filter">Equality filters. GraphQL supports no other kind.</param>
    /// <param name="page">Optional 1-based page number.</param>
    /// <param name="itemsPerPage">Optional page size.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The matching cards, fully populated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="filter"/> is null.</exception>
    /// <exception cref="TcgDexApiException">The query failed or the server reported errors.</exception>
    /// <remarks>
    /// <para>
    /// Use this to avoid N+1: <see cref="ListAsync(Querying.CardQuery, CancellationToken)"/>
    /// returns briefs, so fetching full detail for a 12-card result costs 13
    /// requests against REST versus 1 here.
    /// </para>
    /// <para>
    /// Three limits come with it, all imposed by the GraphQL endpoint rather
    /// than by this SDK: results are <b>always English</b> regardless of the
    /// configured language, filters are <b>equality-only</b>, and
    /// <see cref="Card.Pricing"/> is <b>never populated</b>. When any of those
    /// matter, use the REST path instead.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Card>> SearchDetailedAsync(
        Querying.CardFilter filter,
        int? page = null,
        int? itemsPerPage = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads sets.
/// </summary>
public interface ISetResource
{
    /// <summary>Fetches a single set, including its card list.</summary>
    /// <param name="id">The set id, for example <c>"swsh3"</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The set, or <see langword="null"/> if no set has that id.</returns>
    Task<Set?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Lists every set in brief form.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every set, as briefs.</returns>
    Task<IReadOnlyList<SetBrief>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads series.
/// </summary>
public interface ISerieResource
{
    /// <summary>Fetches a single series, including its sets.</summary>
    /// <param name="id">The series id, for example <c>"swsh"</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The series, or <see langword="null"/> if none has that id.</returns>
    Task<Serie?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Lists every series in brief form.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every series, as briefs.</returns>
    Task<IReadOnlyList<SerieBrief>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches a random card, set or series.
/// </summary>
public interface IRandomResource
{
    /// <summary>Fetches a random card.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A randomly chosen card.</returns>
    Task<Card> CardAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches a random set.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A randomly chosen set.</returns>
    Task<Set> SetAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches a random series.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A randomly chosen series.</returns>
    Task<Serie> SerieAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the enumeration endpoints — the distinct values a field can take.
/// </summary>
/// <remarks>
/// Useful for building valid filters and for populating pickers. Note that
/// <see cref="HitPointsAsync"/>, <see cref="RetreatCostsAsync"/> and
/// <see cref="DexIdsAsync"/> return numbers where the rest return text.
/// </remarks>
public interface ICatalogResource
{
    /// <summary>The card categories: Pokemon, Trainer, Energy.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every category value in use.</returns>
    Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every rarity in use.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every rarity value in use.</returns>
    Task<IReadOnlyList<string>> RaritiesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every elemental energy type.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every type value in use.</returns>
    Task<IReadOnlyList<string>> TypesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every illustrator credited on a card.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every illustrator name in use.</returns>
    Task<IReadOnlyList<string>> IllustratorsAsync(CancellationToken cancellationToken = default);

    /// <summary>Every evolution stage.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every stage value in use.</returns>
    Task<IReadOnlyList<string>> StagesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every card-name suffix, such as EX or VMAX.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every suffix value in use.</returns>
    Task<IReadOnlyList<string>> SuffixesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every printing variant name.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every variant name in use.</returns>
    Task<IReadOnlyList<string>> VariantsAsync(CancellationToken cancellationToken = default);

    /// <summary>The energy categories: Normal and Special.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every energy type value in use.</returns>
    Task<IReadOnlyList<string>> EnergyTypesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every regulation mark.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every regulation mark in use.</returns>
    Task<IReadOnlyList<string>> RegulationMarksAsync(CancellationToken cancellationToken = default);

    /// <summary>Every Trainer subtype.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every trainer type value in use.</returns>
    Task<IReadOnlyList<string>> TrainerTypesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every hit-point value in use.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every HP value in use.</returns>
    Task<IReadOnlyList<int>> HitPointsAsync(CancellationToken cancellationToken = default);

    /// <summary>Every retreat cost in use.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every retreat cost in use.</returns>
    Task<IReadOnlyList<int>> RetreatCostsAsync(CancellationToken cancellationToken = default);

    /// <summary>Every National Pokédex number represented.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every dex id in use.</returns>
    Task<IReadOnlyList<int>> DexIdsAsync(CancellationToken cancellationToken = default);
}

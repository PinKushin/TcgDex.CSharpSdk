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
    /// <param name="filter">
    /// Equality filters, and optionally <see cref="Querying.CardFilter.Page"/> and
    /// <see cref="Querying.CardFilter.ItemsPerPage"/>. GraphQL supports no other kind of filter.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The matching cards, fully populated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="filter"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The filter's page or page size is less than one.</exception>
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
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams every card matching a query, fetching pages as they are consumed.
    /// </summary>
    /// <param name="query">
    /// Filters and sorting. The items-per-page given to <see cref="Querying.CardQuery.Page"/>
    /// is how many results each request fetches (100 when unset); its page number is ignored,
    /// because the stream starts at the first page.
    /// </param>
    /// <param name="cancellationToken">Stops enumeration and cancels the in-flight request.</param>
    /// <returns>An async sequence over every match.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// The API reports no total count and sends no pagination headers, so the
    /// end of the results can only be detected by receiving a page shorter than
    /// requested. Hand-rolling that loop is easy to get subtly wrong; this does
    /// it once.
    /// </para>
    /// <para>
    /// Pages are fetched lazily, so breaking out of the loop early stops the
    /// requests. Nothing is buffered beyond the current page.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<CardBrief> StreamAsync(
        Querying.CardQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads sets.
/// </summary>
public interface ICardSetResource
{
    /// <summary>Fetches a single set, including its card list.</summary>
    /// <param name="id">The set id, for example <c>"swsh3"</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The set, or <see langword="null"/> if no set has that id.</returns>
    Task<CardSet?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Lists every set in brief form.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every set, as briefs.</returns>
    Task<IReadOnlyList<CardSetBrief>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads series.
/// </summary>
public interface ISeriesResource
{
    /// <summary>Fetches a single series, including its sets.</summary>
    /// <param name="id">The series id, for example <c>"swsh"</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The series, or <see langword="null"/> if none has that id.</returns>
    Task<Series?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Lists every series in brief form.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every series, as briefs.</returns>
    Task<IReadOnlyList<SeriesBrief>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches a random card, set or series.
/// </summary>
public interface IRandomResource
{
    /// <summary>Fetches a random card.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A randomly chosen card.</returns>
    Task<Card> GetCardAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches a random set.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A randomly chosen set.</returns>
    Task<CardSet> GetSetAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches a random series.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A randomly chosen series.</returns>
    Task<Series> GetSeriesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the enumeration endpoints — the distinct values a field can take.
/// </summary>
/// <remarks>
/// Useful for building valid filters and for populating pickers. Note that
/// <see cref="GetHitPointsAsync"/>, <see cref="GetRetreatCostsAsync"/> and
/// <see cref="GetDexIdsAsync"/> return numbers where the rest return text.
/// </remarks>
public interface ICatalogResource
{
    /// <summary>The card categories: Pokemon, Trainer, Energy.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every category value in use.</returns>
    Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every rarity in use.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every rarity value in use.</returns>
    Task<IReadOnlyList<string>> GetRaritiesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every elemental energy type.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every type value in use.</returns>
    Task<IReadOnlyList<string>> GetTypesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every illustrator credited on a card.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every illustrator name in use.</returns>
    Task<IReadOnlyList<string>> GetIllustratorsAsync(CancellationToken cancellationToken = default);

    /// <summary>Every evolution stage.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every stage value in use.</returns>
    Task<IReadOnlyList<string>> GetStagesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every card-name suffix, such as EX or VMAX.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every suffix value in use.</returns>
    Task<IReadOnlyList<string>> GetSuffixesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every printing variant name.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every variant name in use.</returns>
    Task<IReadOnlyList<string>> GetVariantsAsync(CancellationToken cancellationToken = default);

    /// <summary>The energy categories: Normal and Special.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every energy type value in use.</returns>
    Task<IReadOnlyList<string>> GetEnergyTypesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every regulation mark.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every regulation mark in use.</returns>
    Task<IReadOnlyList<string>> GetRegulationMarksAsync(CancellationToken cancellationToken = default);

    /// <summary>Every Trainer subtype.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every trainer type value in use.</returns>
    Task<IReadOnlyList<string>> GetTrainerTypesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every hit-point value in use.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every HP value in use.</returns>
    Task<IReadOnlyList<int>> GetHitPointsAsync(CancellationToken cancellationToken = default);

    /// <summary>Every retreat cost in use.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every retreat cost in use.</returns>
    Task<IReadOnlyList<int>> GetRetreatCostsAsync(CancellationToken cancellationToken = default);

    /// <summary>Every National Pokédex number represented.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every dex id in use.</returns>
    Task<IReadOnlyList<int>> GetDexIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads any text enumeration endpoint by name, for one this SDK has no named method for yet.
    /// </summary>
    /// <param name="name">The endpoint, for example <c>"rarities"</c>; escaped as a single path segment.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every value in use.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, whitespace, <c>"."</c> or <c>".."</c>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <remarks>
    /// Upstream adds enumeration endpoints over time. A named method per endpoint
    /// would be a new interface member each time, which cannot ship without a
    /// major version, so this is the way to reach a new one in between
    /// (DECISIONS #36).
    /// </remarks>
    Task<IReadOnlyList<string>> GetValuesAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads any numeric enumeration endpoint by name, like <see cref="GetValuesAsync"/>.
    /// </summary>
    /// <param name="name">The endpoint, for example <c>"hp"</c>; escaped as a single path segment.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every value in use.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, whitespace, <c>"."</c> or <c>".."</c>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    Task<IReadOnlyList<int>> GetNumberValuesAsync(string name, CancellationToken cancellationToken = default);
}

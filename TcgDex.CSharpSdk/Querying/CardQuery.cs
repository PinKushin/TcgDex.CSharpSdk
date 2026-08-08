namespace TcgDex.Querying;

using System.Globalization;
using System.Linq.Expressions;
using TcgDex.Models;

/// <summary>
/// Builds a card query from strongly-typed predicates.
/// </summary>
/// <remarks>
/// <para>
/// Every method returns a new instance, so a partially-built query can be shared
/// and specialised without one caller's additions leaking into another's.
/// </para>
/// <para>
/// This is deliberately not an <see cref="IQueryable{T}"/>. The API supports
/// only the operators in <see cref="QueryOperator"/>, so an
/// <c>IQueryable</c> would have to throw for most of LINQ — a partial
/// implementation that fails at runtime instead of at the call site. A dedicated
/// builder makes the supported surface explicit.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// CardQuery query = new CardQuery()
///     .Where(c => c.Name.Contains("Pikachu"))
///     .Where(c => c.Hp > 100)
///     .OrderByDescending(c => c.Name)
///     .Page(1, 50);
///
/// IReadOnlyList&lt;CardBrief&gt; cards = await client.Cards.ListAsync(query, cancellationToken);
/// </code>
/// </example>
public sealed class CardQuery
{
    private const string AscendingOrder = "ASC";
    private const string DescendingOrder = "DESC";

    private readonly IReadOnlyList<QueryFilter> _filters;
    private readonly string? _sortField;
    private readonly string? _sortOrder;
    private readonly int? _page;
    private readonly int? _itemsPerPage;

    /// <summary>Creates an unfiltered query.</summary>
    public CardQuery()
        : this([], null, null, null, null)
    {
    }

    private CardQuery(
        IReadOnlyList<QueryFilter> filters,
        string? sortField,
        string? sortOrder,
        int? page,
        int? itemsPerPage)
    {
        _filters = filters;
        _sortField = sortField;
        _sortOrder = sortOrder;
        _page = page;
        _itemsPerPage = itemsPerPage;
    }

    /// <summary>
    /// Adds a filter. Multiple calls are combined with AND, matching the API's
    /// treatment of repeated parameters.
    /// </summary>
    /// <param name="predicate">The condition to translate.</param>
    /// <returns>A new query including this filter.</returns>
    /// <exception cref="NotSupportedException">
    /// The predicate has no equivalent in the API's filter syntax. The message
    /// names the offending expression and lists the supported forms.
    /// </exception>
    public CardQuery Where(Expression<Func<Card, bool>> predicate)
    {
        IReadOnlyList<QueryFilter> translated = ExpressionTranslator.Translate(predicate);

        return new CardQuery(
            [.. _filters, .. translated],
            _sortField,
            _sortOrder,
            _page,
            _itemsPerPage);
    }

    /// <summary>Sorts ascending by a field.</summary>
    /// <param name="selector">The property to sort on.</param>
    /// <returns>A new query with this ordering.</returns>
    public CardQuery OrderBy<TKey>(Expression<Func<Card, TKey>> selector)
        => WithSort(selector, AscendingOrder);

    /// <summary>Sorts descending by a field.</summary>
    /// <param name="selector">The property to sort on.</param>
    /// <returns>A new query with this ordering.</returns>
    public CardQuery OrderByDescending<TKey>(Expression<Func<Card, TKey>> selector)
        => WithSort(selector, DescendingOrder);

    /// <summary>
    /// Requests a single page of results.
    /// </summary>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="itemsPerPage">How many results per page.</param>
    /// <returns>A new query limited to that page.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either argument is less than one.</exception>
    /// <remarks>
    /// The API exposes no total count and sends no pagination headers, so the
    /// number of pages cannot be known up front — read pages until one comes
    /// back shorter than <paramref name="itemsPerPage"/>.
    /// </remarks>
    public CardQuery Page(int page, int itemsPerPage)
    {
        Guard.NotLessThan(page, 1);
        Guard.NotLessThan(itemsPerPage, 1);

        return new CardQuery(_filters, _sortField, _sortOrder, page, itemsPerPage);
    }

    /// <summary>
    /// Renders the query as a URL query string, without a leading <c>?</c>.
    /// </summary>
    /// <returns>The query string, or empty when nothing has been specified.</returns>
    /// <remarks>
    /// Filters are top-level parameters. There is no <c>q</c> parameter in this
    /// API, despite what some older documentation suggests.
    /// </remarks>
    public string ToQueryString()
    {
        List<string> parts = new(_filters.Count + 4);

        foreach (QueryFilter filter in _filters)
        {
            parts.Add(filter.Render());
        }

        if (_sortField is not null)
        {
            parts.Add($"sort:field={_sortField}");
            parts.Add($"sort:order={_sortOrder}");
        }

        if (_page is not null)
        {
            parts.Add($"pagination:page={_page.Value.ToString(CultureInfo.InvariantCulture)}");
            parts.Add($"pagination:itemsPerPage={_itemsPerPage!.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        return string.Join("&", parts);
    }

    /// <summary>Renders the relative request path for this query.</summary>
    /// <returns>A path such as <c>cards?name=eq:Furret</c>.</returns>
    internal string ToRelativePath()
    {
        string queryString = ToQueryString();

        return queryString.Length == 0 ? "cards" : $"cards?{queryString}";
    }

    private CardQuery WithSort<TKey>(Expression<Func<Card, TKey>> selector, string order)
    {
        Guard.NotNull(selector);

        string field = ExpressionTranslator.SortFieldName(selector);

        return new CardQuery(_filters, field, order, _page, _itemsPerPage);
    }
}

namespace TcgDex;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TcgDex.Diagnostics;
using TcgDex.Models;
using TcgDex.Querying;
using TcgDex.Serialization;

/// <summary>
/// Issues GraphQL queries, used only where they remove round trips.
/// </summary>
/// <remarks>
/// <para>
/// This is an optimisation, not the primary transport. Verified limits of the
/// TCGdex GraphQL endpoint:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>No language support.</b> There is no language argument or path
///     segment and <c>Accept-Language</c> is ignored, so results are always
///     English regardless of <see cref="TcgDexOptions.Language"/>.
///   </description></item>
///   <item><description>
///     <b>Equality-only filters.</b> Ranges, wildcards and null checks are
///     unavailable — use <see cref="CardQuery"/> against REST for those.
///   </description></item>
///   <item><description>
///     <b>No pricing.</b> The GraphQL schema has no <c>pricing</c> field, so
///     <see cref="Card.Pricing"/> is never populated on this path.
///   </description></item>
/// </list>
/// <para>
/// What it buys: a filtered list of <em>fully detailed</em> cards in one
/// request. The REST list endpoint returns only briefs, so the same result costs
/// one call per card — 13 round trips versus 1 for a search returning 12 cards.
/// </para>
/// </remarks>
internal sealed class GraphQlTransport(
    HttpClient httpClient,
    TcgDexOptions options,
    ILogger? logger = null)
{
    /// <summary>
    /// The card fields requested. Restricted to what the GraphQL schema
    /// actually declares — it has no <c>pricing</c> or <c>updated</c> field,
    /// and its <c>variants_detailed</c> omits pricing and variant ids.
    /// </summary>
    private const string CardSelection = """
        id name category localId rarity illustrator image hp types dexId
        stage suffix evolveFrom description retreat regulationMark trainerType
        effect energyType
        attacks { name cost damage effect }
        abilities { name type effect }
        weaknesses { type value }
        resistances { type value }
        variants { normal reverse holo firstEdition wPromo }
        legal { standard expanded }
        set { id name symbol logo cardCount { official total } }
        """;

    private readonly HttpClient _httpClient = httpClient
        ?? throw new ArgumentNullException(nameof(httpClient));

    private readonly TcgDexOptions _options = options
        ?? throw new ArgumentNullException(nameof(options));

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// Searches for cards, returning full detail for each in a single request.
    /// </summary>
    /// <param name="filter">Equality filters to apply.</param>
    /// <param name="page">Optional 1-based page number.</param>
    /// <param name="itemsPerPage">Optional page size.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The matching cards, fully populated.</returns>
    /// <exception cref="TcgDexApiException">The query failed or the server reported errors.</exception>
    internal async Task<IReadOnlyList<Card>> SearchAsync(
        CardFilter filter,
        int? page,
        int? itemsPerPage,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(filter);

        string query = BuildQuery(filter, page, itemsPerPage);
        GraphQlCardsResponse response = await PostAsync(query, cancellationToken).ConfigureAwait(false);

        // GraphQL answers 200 even when the query failed, so the errors array is
        // the only reliable failure signal.
        if (response.Errors is { Count: > 0 })
        {
            string messages = string.Join("; ", response.Errors.Select(e => e.Message));

            _logger.GraphQlErrors(messages);

            throw new TcgDexApiException($"The TCGdex GraphQL endpoint reported errors: {messages}");
        }

        IReadOnlyList<Card?>? cards = response.Data?.Cards;

        if (cards is null)
        {
            return [];
        }

        // A null entry means the server could not resolve a non-nullable field
        // for that card. Dropping it beats handing back a null the caller has to
        // guard on every iteration — but it is logged, so a missing card is
        // explainable rather than mysterious.
        Card[] resolved = cards.Where(card => card is not null).Select(card => card!).ToArray();

        int dropped = cards.Count - resolved.Length;
        if (dropped > 0)
        {
            _logger.GraphQlDroppedEntries(dropped);
        }

        _logger.GraphQlSearchCompleted(resolved.Length);

        return resolved;
    }

    private static string BuildQuery(CardFilter filter, int? page, int? itemsPerPage)
    {
        StringBuilder arguments = new();
        string filterArguments = filter.ToGraphQlArguments();

        if (filterArguments.Length > 0)
        {
            arguments.Append("filters:{").Append(filterArguments).Append('}');
        }

        if (page is not null || itemsPerPage is not null)
        {
            if (arguments.Length > 0)
            {
                arguments.Append(',');
            }

            arguments.Append("pagination:{");

            if (page is not null)
            {
                arguments.Append("page:").Append(page.Value);
            }

            if (itemsPerPage is not null)
            {
                if (page is not null)
                {
                    arguments.Append(',');
                }

                arguments.Append("itemsPerPage:").Append(itemsPerPage.Value);
            }

            arguments.Append('}');
        }

        string argumentList = arguments.Length > 0 ? $"({arguments})" : string.Empty;

        return $"{{ cards{argumentList} {{ {CardSelection} }} }}";
    }

    private async Task<GraphQlCardsResponse> PostAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            using JsonContent content = JsonContent.Create(
                new GraphQlRequest(query),
                GraphQlJsonContext.Default.GraphQlRequest);

            // The same ceiling the REST path has had all along. Without it this
            // path inherited HttpClient's 100-second default — the value
            // TcgDexOptions.Timeout exists to replace — and then reported the
            // expiry as "the GraphQL request timed out", naming a limit the SDK
            // had not set. With a caller-supplied client configured for
            // InfiniteTimeSpan there was no ceiling at all.
            //
            // The caller's own token is kept for the filters below: only it can
            // tell a budget expiry from the caller asking to stop.
            using CancellationTokenSource? budget =
                RequestBudget.Create(_options.Timeout, cancellationToken);

            CancellationToken deadline = budget?.Token ?? cancellationToken;

            using HttpRequestMessage httpRequest =
                new(HttpMethod.Post, _options.GraphQlEndpoint) { Content = content };

            // ResponseHeadersRead, not PostAsync. PostAsync defaults to
            // ResponseContentRead, which buffers the ENTIRE body inside
            // HttpClient before returning — so the BoundedContent read below was
            // rejecting a body whose memory had already been spent, and the real
            // ceiling was HttpContent's 2 GB rather than MaxResponseBytes. The
            // REST transport reads headers first for exactly this reason.
            using HttpResponseMessage httpResponse = await _httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, deadline)
                .ConfigureAwait(false);

            ArraySegment<byte> body = await BoundedContent
                .ReadAsBytesAsync(
                    httpResponse.Content,
                    _options.MaxResponseBytes,
                    _options.GraphQlEndpoint,
                    deadline)
                .ConfigureAwait(false);

            if (!httpResponse.IsSuccessStatusCode)
            {
                throw new TcgDexApiException(
                    $"The TCGdex GraphQL endpoint returned {(int)httpResponse.StatusCode}.",
                    httpResponse.StatusCode);
            }

            return JsonSerializer.Deserialize(
                    new ReadOnlySpan<byte>(body.Array, body.Offset, body.Count),
                    GraphQlJsonContext.Default.GraphQlCardsResponse)
                ?? throw new TcgDexApiException("The GraphQL endpoint returned an empty response.");
        }
        catch (HttpRequestException ex)
        {
            throw new TcgDexApiException("The GraphQL request could not be completed.", ex);
        }
        catch (JsonException ex)
        {
            throw new TcgDexApiException("The GraphQL response was not valid JSON.", HttpStatusCode.OK, null, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TcgDexApiException("The GraphQL request timed out.", ex);
        }
    }
}

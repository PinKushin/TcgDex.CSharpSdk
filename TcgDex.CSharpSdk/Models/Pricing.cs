namespace TcgDex.Models;

/// <summary>
/// Market pricing for a card, from each supported marketplace. Both sources are
/// independently optional — a card may have one, both, or neither.
/// </summary>
public sealed record Pricing
{
    /// <summary>Cardmarket pricing, in euros.</summary>
    public CardmarketPricing? Cardmarket { get; init; }

    /// <summary>TCGplayer pricing, in US dollars.</summary>
    public TcgPlayerPricing? Tcgplayer { get; init; }
}

/// <summary>
/// Cardmarket price points. Every figure is nullable: the API reports
/// <see langword="null"/> for a series that has no data rather than omitting it.
/// </summary>
/// <remarks>
/// The holo-suffixed properties map to hyphenated JSON keys (<c>avg-holo</c>),
/// which is why they carry explicit name mappings.
/// </remarks>
public sealed record CardmarketPricing
{
    /// <summary>When these prices were last refreshed.</summary>
    public DateTimeOffset? Updated { get; init; }

    /// <summary>Currency code; <c>"EUR"</c> for this source.</summary>
    public string? Unit { get; init; }

    /// <summary>Cardmarket's internal product identifier.</summary>
    public int? IdProduct { get; init; }

    /// <summary>Average sale price.</summary>
    public decimal? Avg { get; init; }

    /// <summary>Lowest available price.</summary>
    public decimal? Low { get; init; }

    /// <summary>Current price trend.</summary>
    public decimal? Trend { get; init; }

    /// <summary>Average price over the last day.</summary>
    public decimal? Avg1 { get; init; }

    /// <summary>Average price over the last 7 days.</summary>
    public decimal? Avg7 { get; init; }

    /// <summary>Average price over the last 30 days.</summary>
    public decimal? Avg30 { get; init; }

    /// <summary>Average sale price for the holofoil printing.</summary>
    [JsonPropertyName("avg-holo")]
    public decimal? AvgHolo { get; init; }

    /// <summary>Lowest available price for the holofoil printing.</summary>
    [JsonPropertyName("low-holo")]
    public decimal? LowHolo { get; init; }

    /// <summary>Price trend for the holofoil printing.</summary>
    [JsonPropertyName("trend-holo")]
    public decimal? TrendHolo { get; init; }

    /// <summary>Holofoil average over the last day.</summary>
    [JsonPropertyName("avg1-holo")]
    public decimal? Avg1Holo { get; init; }

    /// <summary>Holofoil average over the last 7 days.</summary>
    [JsonPropertyName("avg7-holo")]
    public decimal? Avg7Holo { get; init; }

    /// <summary>Holofoil average over the last 30 days.</summary>
    [JsonPropertyName("avg30-holo")]
    public decimal? Avg30Holo { get; init; }
}

/// <summary>
/// TCGplayer pricing, which reports one price block per printing.
/// </summary>
/// <remarks>
/// The printing names are <em>data, not schema</em> — a card carries whichever
/// keys apply to it (<c>normal</c> and <c>reverse-holofoil</c> on one card,
/// <c>holofoil</c> on another). They are therefore exposed as a dictionary
/// rather than as fixed properties, so a printing this SDK has never seen still
/// round-trips.
/// </remarks>
[JsonConverter(typeof(Serialization.TcgPlayerPricingConverter))]
public sealed record TcgPlayerPricing
{
    /// <summary>Currency code; <c>"USD"</c> for this source.</summary>
    public string? Unit { get; init; }

    /// <summary>When these prices were last refreshed.</summary>
    public DateTimeOffset? Updated { get; init; }

    /// <summary>
    /// Price blocks keyed by printing name, for example <c>"normal"</c>,
    /// <c>"holofoil"</c> or <c>"reverse-holofoil"</c>.
    /// </summary>
    public IReadOnlyDictionary<string, TcgPlayerPrice> Printings { get; init; }
        = new Dictionary<string, TcgPlayerPrice>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the price block for a printing, or <see langword="null"/> when the
    /// card has no prices for it.
    /// </summary>
    /// <param name="printing">The printing name, for example <c>"normal"</c>.</param>
    /// <returns>The matching price block, or <see langword="null"/>.</returns>
    public TcgPlayerPrice? this[string printing]
        => Printings.TryGetValue(printing, out var price) ? price : null;
}

/// <summary>
/// The price points TCGplayer reports for a single printing. Any individual
/// figure may be <see langword="null"/>.
/// </summary>
public sealed record TcgPlayerPrice
{
    /// <summary>TCGplayer's internal product identifier.</summary>
    public int? ProductId { get; init; }

    /// <summary>Lowest listed price.</summary>
    public decimal? LowPrice { get; init; }

    /// <summary>Median listed price.</summary>
    public decimal? MidPrice { get; init; }

    /// <summary>Highest listed price.</summary>
    public decimal? HighPrice { get; init; }

    /// <summary>Current market price.</summary>
    public decimal? MarketPrice { get; init; }

    /// <summary>Lowest price available through TCGplayer Direct.</summary>
    public decimal? DirectLowPrice { get; init; }
}

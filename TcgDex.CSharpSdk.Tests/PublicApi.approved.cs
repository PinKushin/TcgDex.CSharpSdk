namespace Microsoft.Extensions.DependencyInjection
{
    public static class TcgDexServiceCollectionExtensions
    {
        public static Microsoft.Extensions.DependencyInjection.IHttpClientBuilder AddTcgDex(this Microsoft.Extensions.DependencyInjection.IServiceCollection services, System.Action<TcgDex.TcgDexOptions>? configure = null) { }
        public static Microsoft.Extensions.DependencyInjection.IHttpClientBuilder AddTcgDexWithCaching(this Microsoft.Extensions.DependencyInjection.IServiceCollection services, System.Action<TcgDex.TcgDexOptions>? configure = null, System.Action<TcgDex.Caching.TcgDexCacheOptions>? configureCache = null) { }
    }
}
namespace TcgDex.Caching
{
    public sealed class CachedResponse
    {
        public CachedResponse() { }
        public required byte[] Body { get; init; }
        public string? ContentType { get; init; }
        public string? ETag { get; init; }
        public required System.DateTimeOffset StoredAt { get; init; }
        public bool IsFresh(System.DateTimeOffset now, System.TimeSpan timeToLive) { }
    }
    public interface ITcgDexResponseCache
    {
        System.Threading.Tasks.ValueTask<TcgDex.Caching.CachedResponse?> GetAsync(string key, System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.ValueTask RemoveAsync(string key, System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.ValueTask SetAsync(string key, TcgDex.Caching.CachedResponse response, System.TimeSpan timeToLive, System.Threading.CancellationToken cancellationToken = default);
    }
    public sealed class MemoryTcgDexResponseCache : TcgDex.Caching.ITcgDexResponseCache
    {
        public MemoryTcgDexResponseCache(int maxEntries = 512, System.TimeProvider? timeProvider = null) { }
        public int Count { get; }
        public void Clear() { }
        public System.Threading.Tasks.ValueTask<TcgDex.Caching.CachedResponse?> GetAsync(string key, System.Threading.CancellationToken cancellationToken = default) { }
        public System.Threading.Tasks.ValueTask RemoveAsync(string key, System.Threading.CancellationToken cancellationToken = default) { }
        public System.Threading.Tasks.ValueTask SetAsync(string key, TcgDex.Caching.CachedResponse response, System.TimeSpan timeToLive, System.Threading.CancellationToken cancellationToken = default) { }
    }
    public class TcgDexCacheOptions
    {
        public TcgDexCacheOptions() { }
        public System.TimeSpan CatalogTimeToLive { get; set; }
        public bool CoalesceConcurrentRequests { get; set; }
        public System.TimeSpan DefaultTimeToLive { get; set; }
        public int MaxEntries { get; set; }
        public System.TimeSpan PricingTimeToLive { get; set; }
        public virtual System.TimeSpan GetTimeToLive(System.Uri requestUri) { }
    }
    public sealed class TcgDexCacheStatistics
    {
        public long FreshHits { get; }
        public long Misses { get; }
        public long Revalidations { get; }
    }
    public sealed class TcgDexCachingHandler : System.Net.Http.DelegatingHandler
    {
        public TcgDexCachingHandler(TcgDex.Caching.ITcgDexResponseCache cache, TcgDex.Caching.TcgDexCacheOptions? options = null, System.TimeProvider? timeProvider = null, long maxResponseBytes = 33554432) { }
        public TcgDex.Caching.TcgDexCacheStatistics Statistics { get; }
        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken) { }
    }
}
namespace TcgDex.Diagnostics
{
    public static class TcgDexActivity
    {
        public const string SourceName = "TcgDex.CSharpSdk";
    }
}
namespace TcgDex
{
    public interface ITcgDexClient
    {
        TcgDex.Resources.ICardResource Cards { get; }
        TcgDex.Resources.ICatalogResource Catalog { get; }
        TcgDex.Resources.IRandomResource Random { get; }
        TcgDex.Resources.ISeriesResource Series { get; }
        TcgDex.Resources.ICardSetResource Sets { get; }
    }
    public sealed class TcgDexApiException : System.Exception
    {
        public TcgDexApiException() { }
        public TcgDexApiException(string message) { }
        public TcgDexApiException(string message, System.Exception? innerException) { }
        public TcgDexApiException(string message, TcgDex.TcgDexErrorKind kind, System.Uri? requestUri, System.Net.HttpStatusCode? statusCode = default, TcgDex.Models.TcgDexProblem? problem = null, System.Exception? innerException = null) { }
        public bool IsLanguageError { get; }
        public TcgDex.TcgDexErrorKind Kind { get; }
        public TcgDex.Models.TcgDexProblem? Problem { get; }
        public System.Uri? RequestUri { get; }
        public System.Net.HttpStatusCode? StatusCode { get; }
    }
    public sealed class TcgDexClient : System.IDisposable, TcgDex.ITcgDexClient
    {
        public TcgDexClient(System.Net.Http.HttpClient httpClient, TcgDex.TcgDexOptions? options = null) { }
        public TcgDexClient(System.Net.Http.HttpClient httpClient, TcgDex.TcgDexOptions? options, Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory) { }
        public TcgDex.Resources.ICardResource Cards { get; }
        public TcgDex.Resources.ICatalogResource Catalog { get; }
        public TcgDex.Resources.IRandomResource Random { get; }
        public TcgDex.Resources.ISeriesResource Series { get; }
        public TcgDex.Resources.ICardSetResource Sets { get; }
        public void Dispose() { }
        public static TcgDex.TcgDexClient Create(TcgDex.TcgDexOptions? options = null, System.Action<TcgDex.Caching.TcgDexCacheOptions>? configureCache = null, Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null) { }
    }
    public enum TcgDexErrorKind
    {
        Unspecified = 0,
        HttpStatus = 1,
        Network = 2,
        Timeout = 3,
        Canceled = 4,
        ResponseTooLarge = 5,
        InvalidResponse = 6,
        GraphQLError = 7,
    }
    public static class TcgDexLanguages
    {
        public const string ChineseSimplified = "zh-cn";
        public const string ChineseTraditional = "zh-tw";
        public const string Dutch = "nl";
        public const string English = "en";
        public const string French = "fr";
        public const string German = "de";
        public const string Indonesian = "id";
        public const string Italian = "it";
        public const string Japanese = "ja";
        public const string Korean = "ko";
        public const string Polish = "pl";
        public const string Portuguese = "pt";
        public const string PortugueseBrazil = "pt-br";
        public const string PortuguesePortugal = "pt-pt";
        public const string Russian = "ru";
        public const string Spanish = "es";
        public const string SpanishMexico = "es-mx";
        public const string Thai = "th";
        public static System.Collections.Generic.IReadOnlyList<string> All { get; }
        public static bool IsSupported(string? language) { }
    }
    public sealed class TcgDexOptions
    {
        public TcgDexOptions() { }
        public System.Uri BaseAddress { get; set; }
        public bool DeserializePricing { get; set; }
        public System.Uri GraphQLEndpoint { get; set; }
        public string Language { get; set; }
        public int MaxDeserializedCacheEntries { get; set; }
        public long MaxResponseBytes { get; set; }
        public System.TimeSpan Timeout { get; set; }
        public void Validate() { }
    }
}
namespace TcgDex.Models
{
    public sealed class Ability : System.IEquatable<TcgDex.Models.Ability>
    {
        public Ability() { }
        public string? Effect { get; init; }
        public string? Name { get; init; }
        public string? Type { get; init; }
    }
    public sealed class Attack : System.IEquatable<TcgDex.Models.Attack>
    {
        public Attack() { }
        [System.Text.Json.Serialization.JsonIgnore]
        public int? BaseDamage { get; }
        public System.Collections.Generic.IReadOnlyList<string> Cost { get; init; }
        [System.Text.Json.Serialization.JsonConverter(typeof(TcgDex.Serialization.FlexibleStringConverter?))]
        public string? Damage { get; init; }
        public string? Effect { get; init; }
        public string? Name { get; init; }
    }
    public sealed class Booster : System.IEquatable<TcgDex.Models.Booster>
    {
        public Booster() { }
        [System.Text.Json.Serialization.JsonPropertyName("artwork_back")]
        public string? ArtworkBack { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("artwork_front")]
        public string? ArtworkFront { get; init; }
        public required string Id { get; init; }
        public string? Logo { get; init; }
        public string? Name { get; init; }
    }
    public sealed class Card : System.IEquatable<TcgDex.Models.Card>
    {
        public Card() { }
        public System.Collections.Generic.IReadOnlyList<TcgDex.Models.Ability> Abilities { get; init; }
        public System.Collections.Generic.IReadOnlyList<TcgDex.Models.Attack> Attacks { get; init; }
        public System.Collections.Generic.IReadOnlyList<TcgDex.Models.Booster> Boosters { get; init; }
        public required string Category { get; init; }
        public string? Description { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("variants_detailed")]
        public System.Collections.Generic.IReadOnlyList<TcgDex.Models.DetailedVariant> DetailedVariants { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("dexId")]
        public System.Collections.Generic.IReadOnlyList<int> DexIds { get; init; }
        public string? Effect { get; init; }
        public string? EnergyType { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("evolveFrom")]
        public string? EvolvesFrom { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("hp")]
        public int? HitPoints { get; init; }
        public required string Id { get; init; }
        public string? Illustrator { get; init; }
        public string? Image { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("legal")]
        public TcgDex.Models.Legality? Legality { get; init; }
        [System.Text.Json.Serialization.JsonConverter(typeof(TcgDex.Serialization.FlexibleStringConverter?))]
        public required string LocalId { get; init; }
        public required string Name { get; init; }
        public TcgDex.Models.Pricing? Pricing { get; init; }
        public string? Rarity { get; init; }
        public string? RegulationMark { get; init; }
        public System.Collections.Generic.IReadOnlyList<TcgDex.Models.WeaknessOrResistance> Resistances { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("retreat")]
        public int? RetreatCost { get; init; }
        public required TcgDex.Models.CardSetBrief Set { get; init; }
        public string? Stage { get; init; }
        public string? Suffix { get; init; }
        public TcgDex.Models.ThirdParty? ThirdParty { get; init; }
        public string? TrainerType { get; init; }
        public System.Collections.Generic.IReadOnlyList<string> Types { get; init; }
        [System.Text.Json.Serialization.JsonConverter(typeof(TcgDex.Serialization.TimestampConverter?))]
        public System.DateTimeOffset? Updated { get; init; }
        public TcgDex.Models.Variants? Variants { get; init; }
        public System.Collections.Generic.IReadOnlyList<TcgDex.Models.WeaknessOrResistance> Weaknesses { get; init; }
        public bool IsCategory(string category) { }
    }
    public sealed class CardBrief : System.IEquatable<TcgDex.Models.CardBrief>
    {
        public CardBrief() { }
        public required string Id { get; init; }
        public string? Image { get; init; }
        [System.Text.Json.Serialization.JsonConverter(typeof(TcgDex.Serialization.FlexibleStringConverter))]
        public required string LocalId { get; init; }
        public required string Name { get; init; }
    }
    public static class CardCategories
    {
        public const string Energy = "Energy";
        public const string Pokemon = "Pokemon";
        public const string Trainer = "Trainer";
    }
    public sealed class CardCount : System.IEquatable<TcgDex.Models.CardCount>
    {
        public CardCount() { }
        [System.Text.Json.Serialization.JsonPropertyName("firstEd")]
        public int? FirstEdition { get; init; }
        public int? Holo { get; init; }
        public int? Normal { get; init; }
        public int Official { get; init; }
        public int? Reverse { get; init; }
        public int Total { get; init; }
    }
    public sealed class CardSet : System.IEquatable<TcgDex.Models.CardSet>
    {
        public CardSet() { }
        public TcgDex.Models.CardSetAbbreviation? Abbreviation { get; init; }
        public TcgDex.Models.CardCount? CardCount { get; init; }
        public System.Collections.Generic.IReadOnlyList<TcgDex.Models.CardBrief> Cards { get; init; }
        public required string Id { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("legal")]
        public TcgDex.Models.Legality? Legality { get; init; }
        public string? Logo { get; init; }
        public required string Name { get; init; }
        [System.Text.Json.Serialization.JsonConverter(typeof(TcgDex.Serialization.ReleaseDateConverter?))]
        public System.DateTime? ReleaseDate { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("serie")]
        public TcgDex.Models.SeriesBrief? Series { get; init; }
        public string? Symbol { get; init; }
        public string? TcgOnline { get; init; }
    }
    public sealed class CardSetAbbreviation : System.IEquatable<TcgDex.Models.CardSetAbbreviation>
    {
        public CardSetAbbreviation() { }
        public string? Localized { get; init; }
        public string? Official { get; init; }
    }
    public sealed class CardSetBrief : System.IEquatable<TcgDex.Models.CardSetBrief>
    {
        public CardSetBrief() { }
        public TcgDex.Models.CardCount? CardCount { get; init; }
        public required string Id { get; init; }
        public string? Logo { get; init; }
        public required string Name { get; init; }
        public string? Symbol { get; init; }
    }
    public sealed class CardmarketPricing : System.IEquatable<TcgDex.Models.CardmarketPricing>
    {
        public CardmarketPricing() { }
        [System.Text.Json.Serialization.JsonPropertyName("avg")]
        public decimal? Average { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("avg1")]
        public decimal? Average1Day { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("avg1-holo")]
        public decimal? Average1DayHolo { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("avg30")]
        public decimal? Average30Days { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("avg30-holo")]
        public decimal? Average30DaysHolo { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("avg7")]
        public decimal? Average7Days { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("avg7-holo")]
        public decimal? Average7DaysHolo { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("avg-holo")]
        public decimal? AverageHolo { get; init; }
        public decimal? Low { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("low-holo")]
        public decimal? LowHolo { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("idProduct")]
        public int? ProductId { get; init; }
        public decimal? Trend { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("trend-holo")]
        public decimal? TrendHolo { get; init; }
        public string? Unit { get; init; }
        [System.Text.Json.Serialization.JsonConverter(typeof(TcgDex.Serialization.TimestampConverter?))]
        public System.DateTimeOffset? Updated { get; init; }
    }
    public sealed class DetailedVariant : System.IEquatable<TcgDex.Models.DetailedVariant>
    {
        public DetailedVariant() { }
        public string? Foil { get; init; }
        public TcgDex.Models.Pricing? Pricing { get; init; }
        public string? Size { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("stamp")]
        public System.Collections.Generic.IReadOnlyList<string> Stamps { get; init; }
        public string? Subtype { get; init; }
        public TcgDex.Models.ThirdParty? ThirdParty { get; init; }
        public string? Type { get; init; }
        public string? VariantId { get; init; }
    }
    public static class ImageExtensions
    {
        public static System.Uri? GetImageUrl(this TcgDex.Models.Card card, TcgDex.Models.ImageQuality quality = 0, TcgDex.Models.ImageFormat format = 0) { }
        public static System.Uri? GetImageUrl(this TcgDex.Models.CardBrief card, TcgDex.Models.ImageQuality quality = 0, TcgDex.Models.ImageFormat format = 0) { }
        public static System.Uri? GetLogoUrl(this TcgDex.Models.CardSet set, TcgDex.Models.ImageFormat format = 0) { }
        public static System.Uri? GetLogoUrl(this TcgDex.Models.CardSetBrief set, TcgDex.Models.ImageFormat format = 0) { }
        public static System.Uri? GetSymbolUrl(this TcgDex.Models.CardSet set, TcgDex.Models.ImageFormat format = 0) { }
        public static System.Uri? GetSymbolUrl(this TcgDex.Models.CardSetBrief set, TcgDex.Models.ImageFormat format = 0) { }
    }
    public enum ImageFormat
    {
        Png = 0,
        Webp = 1,
        Jpg = 2,
    }
    public enum ImageQuality
    {
        High = 0,
        Low = 1,
    }
    public static class ImageUrl
    {
        public static System.Uri? Build(string? baseUrl, TcgDex.Models.ImageQuality quality = 0, TcgDex.Models.ImageFormat format = 0) { }
        public static System.Uri? BuildAsset(string? baseUrl, TcgDex.Models.ImageFormat format = 0) { }
    }
    public sealed class Legality : System.IEquatable<TcgDex.Models.Legality>
    {
        public Legality() { }
        public bool Expanded { get; init; }
        public bool Standard { get; init; }
    }
    public sealed class Pricing : System.IEquatable<TcgDex.Models.Pricing>
    {
        public Pricing() { }
        public TcgDex.Models.CardmarketPricing? Cardmarket { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("tcgplayer")]
        public TcgDex.Models.TcgPlayerPricing? TcgPlayer { get; init; }
    }
    public sealed class Series : System.IEquatable<TcgDex.Models.Series>
    {
        public Series() { }
        public TcgDex.Models.CardSetBrief? FirstSet { get; init; }
        public required string Id { get; init; }
        public TcgDex.Models.CardSetBrief? LastSet { get; init; }
        public string? Logo { get; init; }
        public required string Name { get; init; }
        [System.Text.Json.Serialization.JsonConverter(typeof(TcgDex.Serialization.ReleaseDateConverter))]
        public System.DateTime? ReleaseDate { get; init; }
        public System.Collections.Generic.IReadOnlyList<TcgDex.Models.CardSetBrief> Sets { get; init; }
    }
    public sealed class SeriesBrief : System.IEquatable<TcgDex.Models.SeriesBrief>
    {
        public SeriesBrief() { }
        public required string Id { get; init; }
        public string? Logo { get; init; }
        public required string Name { get; init; }
    }
    public sealed class TcgDexProblem : System.IEquatable<TcgDex.Models.TcgDexProblem>
    {
        public TcgDexProblem() { }
        public string? Details { get; init; }
        public string? Endpoint { get; init; }
        public string? Error { get; init; }
        public bool IsLanguageError { get; }
        [System.Text.Json.Serialization.JsonPropertyName("lang")]
        public string? Language { get; init; }
        public string? Method { get; init; }
        public int? Status { get; init; }
        public string? Title { get; init; }
        public string? Type { get; init; }
        public string Describe() { }
    }
    public sealed class TcgPlayerPrice : System.IEquatable<TcgDex.Models.TcgPlayerPrice>
    {
        public TcgPlayerPrice() { }
        public decimal? DirectLowPrice { get; init; }
        public decimal? HighPrice { get; init; }
        public decimal? LowPrice { get; init; }
        public decimal? MarketPrice { get; init; }
        public decimal? MidPrice { get; init; }
        public int? ProductId { get; init; }
    }
    [System.Text.Json.Serialization.JsonConverter(typeof(TcgDex.Serialization.TcgPlayerPricingConverter))]
    public sealed class TcgPlayerPricing : System.IEquatable<TcgDex.Models.TcgPlayerPricing>
    {
        public TcgPlayerPricing() { }
        public TcgDex.Models.TcgPlayerPrice? this[string printing] { get; }
        public System.Collections.Generic.IReadOnlyDictionary<string, TcgDex.Models.TcgPlayerPrice> Printings { get; init; }
        public string? Unit { get; init; }
        public System.DateTimeOffset? Updated { get; init; }
    }
    public sealed class ThirdParty : System.IEquatable<TcgDex.Models.ThirdParty>
    {
        public ThirdParty() { }
        [System.Text.Json.Serialization.JsonPropertyName("cardtrader")]
        public int? CardTrader { get; init; }
        public int? Cardmarket { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("tcgplayer")]
        public int? TcgPlayer { get; init; }
    }
    public sealed class Variants : System.IEquatable<TcgDex.Models.Variants>
    {
        public Variants() { }
        public bool FirstEdition { get; init; }
        public bool Holo { get; init; }
        public bool Normal { get; init; }
        public bool Reverse { get; init; }
        public bool WPromo { get; init; }
    }
    public sealed class WeaknessOrResistance : System.IEquatable<TcgDex.Models.WeaknessOrResistance>
    {
        public WeaknessOrResistance() { }
        public required string Type { get; init; }
        public string? Value { get; init; }
    }
}
namespace TcgDex.Querying
{
    public sealed class CardFilter : System.IEquatable<TcgDex.Querying.CardFilter>
    {
        public CardFilter() { }
        public string? Category { get; init; }
        public int? DexId { get; init; }
        public string? EnergyType { get; init; }
        public string? EvolvesFrom { get; init; }
        public int? HitPoints { get; init; }
        public string? Id { get; init; }
        public string? Illustrator { get; init; }
        public int? ItemsPerPage { get; init; }
        public string? LocalId { get; init; }
        public string? Name { get; init; }
        public int? Page { get; init; }
        public string? Rarity { get; init; }
        public string? RegulationMark { get; init; }
        public int? RetreatCost { get; init; }
        public string? Stage { get; init; }
        public string? Suffix { get; init; }
        public string? TrainerType { get; init; }
    }
    public sealed class CardQuery
    {
        public CardQuery() { }
        public TcgDex.Querying.CardQuery OrderBy<TKey>(System.Linq.Expressions.Expression<System.Func<TcgDex.Models.Card, TKey>> selector) { }
        public TcgDex.Querying.CardQuery OrderByDescending<TKey>(System.Linq.Expressions.Expression<System.Func<TcgDex.Models.Card, TKey>> selector) { }
        public TcgDex.Querying.CardQuery Page(int page, int itemsPerPage) { }
        public string ToQueryString() { }
        public TcgDex.Querying.CardQuery Where(System.Linq.Expressions.Expression<System.Func<TcgDex.Models.Card, bool>> predicate) { }
    }
}
namespace TcgDex.Resources
{
    public interface ICardResource
    {
        System.Threading.Tasks.Task<TcgDex.Models.Card?> GetAsync(string id, System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<TcgDex.Models.CardBrief>> ListAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<TcgDex.Models.CardBrief>> ListAsync(TcgDex.Querying.CardQuery query, System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<TcgDex.Models.Card>> SearchDetailedAsync(TcgDex.Querying.CardFilter filter, System.Threading.CancellationToken cancellationToken = default);
        System.Collections.Generic.IAsyncEnumerable<TcgDex.Models.CardBrief> StreamAsync(TcgDex.Querying.CardQuery query, System.Threading.CancellationToken cancellationToken = default);
    }
    public interface ICardSetResource
    {
        System.Threading.Tasks.Task<TcgDex.Models.CardSet?> GetAsync(string id, System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<TcgDex.Models.CardSetBrief>> ListAsync(System.Threading.CancellationToken cancellationToken = default);
    }
    public interface ICatalogResource
    {
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> GetCategoriesAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<int>> GetDexIdsAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> GetEnergyTypesAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<int>> GetHitPointsAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> GetIllustratorsAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<int>> GetNumberValuesAsync(string name, System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> GetRaritiesAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> GetRegulationMarksAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<int>> GetRetreatCostsAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> GetStagesAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> GetSuffixesAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> GetTrainerTypesAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> GetTypesAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> GetValuesAsync(string name, System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> GetVariantsAsync(System.Threading.CancellationToken cancellationToken = default);
    }
    public interface IRandomResource
    {
        System.Threading.Tasks.Task<TcgDex.Models.Card> GetCardAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<TcgDex.Models.Series> GetSeriesAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<TcgDex.Models.CardSet> GetSetAsync(System.Threading.CancellationToken cancellationToken = default);
    }
    public interface ISeriesResource
    {
        System.Threading.Tasks.Task<TcgDex.Models.Series?> GetAsync(string id, System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<TcgDex.Models.SeriesBrief>> ListAsync(System.Threading.CancellationToken cancellationToken = default);
    }
}
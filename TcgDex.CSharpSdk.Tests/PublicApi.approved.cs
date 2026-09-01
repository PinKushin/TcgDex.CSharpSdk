namespace TcgDex.Caching
{
    public sealed class CachedResponse : System.IEquatable<TcgDex.Caching.CachedResponse>
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
    public sealed class TcgDexCachingHandler : System.Net.Http.DelegatingHandler
    {
        public TcgDexCachingHandler(TcgDex.Caching.ITcgDexResponseCache cache, TcgDex.Caching.TcgDexCacheOptions? options = null, System.TimeProvider? timeProvider = null) { }
        public long FreshHits { get; }
        public long Misses { get; }
        public long Revalidations { get; }
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
        TcgDex.Resources.ISerieResource Series { get; }
        TcgDex.Resources.ISetResource Sets { get; }
    }
    public sealed class TcgDexApiException : System.Exception
    {
        public TcgDexApiException() { }
        public TcgDexApiException(string message) { }
        public TcgDexApiException(string message, System.Exception? innerException) { }
        public TcgDexApiException(string message, System.Net.HttpStatusCode statusCode, TcgDex.Models.TcgDexProblem? problem = null, System.Exception? innerException = null) { }
        public bool IsLanguageError { get; }
        public TcgDex.Models.TcgDexProblem? Problem { get; }
        public System.Net.HttpStatusCode? StatusCode { get; }
    }
    public sealed class TcgDexClient : System.IDisposable, TcgDex.ITcgDexClient
    {
        public TcgDexClient(System.Net.Http.HttpClient httpClient, TcgDex.TcgDexOptions? options = null) { }
        public TcgDexClient(System.Net.Http.HttpClient httpClient, TcgDex.TcgDexOptions? options, Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory) { }
        public TcgDex.Resources.ICardResource Cards { get; }
        public TcgDex.Resources.ICatalogResource Catalog { get; }
        public TcgDex.Resources.IRandomResource Random { get; }
        public TcgDex.Resources.ISerieResource Series { get; }
        public TcgDex.Resources.ISetResource Sets { get; }
        public void Dispose() { }
        public static TcgDex.TcgDexClient Create(TcgDex.TcgDexOptions? options = null, System.Action<TcgDex.Caching.TcgDexCacheOptions>? configureCache = null, Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null) { }
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
    public enum TcgDexMirror
    {
        Eu1 = 0,
        Eu2 = 1,
        Eu3 = 2,
        Na1 = 3,
        Na2 = 4,
        As1 = 5,
    }
    public sealed class TcgDexOptions
    {
        public TcgDexOptions() { }
        public System.Uri BaseAddress { get; set; }
        public bool DeserializePricing { get; set; }
        public System.TimeSpan FailoverAttemptTimeout { get; set; }
        public System.TimeSpan FailoverCooldown { get; set; }
        public System.Collections.Generic.IReadOnlyList<System.Uri> FailoverEndpoints { get; }
        public System.Uri GraphQlEndpoint { get; set; }
        public string Language { get; set; }
        public int MaxDeserializedCacheEntries { get; set; }
        public long MaxResponseBytes { get; set; }
        public System.TimeSpan Timeout { get; set; }
        public TcgDex.TcgDexOptions UseFailover() { }
        public TcgDex.TcgDexOptions UseFailover(params System.Uri[] endpoints) { }
        public TcgDex.TcgDexOptions UseFailover(params TcgDex.TcgDexMirror[] mirrors) { }
        public TcgDex.TcgDexOptions UseMirror(TcgDex.TcgDexMirror mirror) { }
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
        public System.Collections.Generic.IReadOnlyList<int> DexId { get; init; }
        public string? Effect { get; init; }
        public string? EnergyType { get; init; }
        public string? EvolveFrom { get; init; }
        public int? Hp { get; init; }
        public required string Id { get; init; }
        public string? Illustrator { get; init; }
        public string? Image { get; init; }
        public TcgDex.Models.Legality? Legal { get; init; }
        [System.Text.Json.Serialization.JsonConverter(typeof(TcgDex.Serialization.FlexibleStringConverter?))]
        public required string LocalId { get; init; }
        public required string Name { get; init; }
        public TcgDex.Models.Pricing? Pricing { get; init; }
        public string? Rarity { get; init; }
        public string? RegulationMark { get; init; }
        public System.Collections.Generic.IReadOnlyList<TcgDex.Models.WeaknessOrResistance> Resistances { get; init; }
        public int? Retreat { get; init; }
        public required TcgDex.Models.SetBrief Set { get; init; }
        public string? Stage { get; init; }
        public string? Suffix { get; init; }
        public string? TrainerType { get; init; }
        public System.Collections.Generic.IReadOnlyList<string> Types { get; init; }
        public System.DateTimeOffset? Updated { get; init; }
        public TcgDex.Models.Variants? Variants { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("variants_detailed")]
        public System.Collections.Generic.IReadOnlyList<TcgDex.Models.DetailedVariant> VariantsDetailed { get; init; }
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
        public int? FirstEd { get; init; }
        public int? Holo { get; init; }
        public int? Normal { get; init; }
        public int Official { get; init; }
        public int? Reverse { get; init; }
        public int Total { get; init; }
    }
    public sealed class CardmarketPricing : System.IEquatable<TcgDex.Models.CardmarketPricing>
    {
        public CardmarketPricing() { }
        public decimal? Avg { get; init; }
        public decimal? Avg1 { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("avg1-holo")]
        public decimal? Avg1Holo { get; init; }
        public decimal? Avg30 { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("avg30-holo")]
        public decimal? Avg30Holo { get; init; }
        public decimal? Avg7 { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("avg7-holo")]
        public decimal? Avg7Holo { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("avg-holo")]
        public decimal? AvgHolo { get; init; }
        public int? IdProduct { get; init; }
        public decimal? Low { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("low-holo")]
        public decimal? LowHolo { get; init; }
        public decimal? Trend { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("trend-holo")]
        public decimal? TrendHolo { get; init; }
        public string? Unit { get; init; }
        public System.DateTimeOffset? Updated { get; init; }
    }
    public sealed class DetailedVariant : System.IEquatable<TcgDex.Models.DetailedVariant>
    {
        public DetailedVariant() { }
        public string? Foil { get; init; }
        public TcgDex.Models.Pricing? Pricing { get; init; }
        public string? Size { get; init; }
        public System.Collections.Generic.IReadOnlyList<string> Stamp { get; init; }
        public string? Subtype { get; init; }
        public string? Type { get; init; }
        public string? VariantId { get; init; }
    }
    public static class ImageExtensions
    {
        public static string? GetImageUrl(this TcgDex.Models.Card card, TcgDex.Models.ImageQuality quality = 0, TcgDex.Models.ImageFormat format = 0) { }
        public static string? GetImageUrl(this TcgDex.Models.CardBrief card, TcgDex.Models.ImageQuality quality = 0, TcgDex.Models.ImageFormat format = 0) { }
        public static string? GetLogoUrl(this TcgDex.Models.Set set, TcgDex.Models.ImageFormat format = 0) { }
        public static string? GetLogoUrl(this TcgDex.Models.SetBrief set, TcgDex.Models.ImageFormat format = 0) { }
        public static string? GetSymbolUrl(this TcgDex.Models.Set set, TcgDex.Models.ImageFormat format = 0) { }
        public static string? GetSymbolUrl(this TcgDex.Models.SetBrief set, TcgDex.Models.ImageFormat format = 0) { }
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
        public static string? Build(string? baseUrl, TcgDex.Models.ImageQuality quality = 0, TcgDex.Models.ImageFormat format = 0) { }
        public static string? BuildAsset(string? baseUrl, TcgDex.Models.ImageFormat format = 0) { }
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
        public TcgDex.Models.TcgPlayerPricing? Tcgplayer { get; init; }
    }
    public sealed class Serie : System.IEquatable<TcgDex.Models.Serie>
    {
        public Serie() { }
        public TcgDex.Models.SetBrief? FirstSet { get; init; }
        public required string Id { get; init; }
        public TcgDex.Models.SetBrief? LastSet { get; init; }
        public string? Logo { get; init; }
        public required string Name { get; init; }
        public string? ReleaseDate { get; init; }
        public System.Collections.Generic.IReadOnlyList<TcgDex.Models.SetBrief> Sets { get; init; }
    }
    public sealed class SerieBrief : System.IEquatable<TcgDex.Models.SerieBrief>
    {
        public SerieBrief() { }
        public required string Id { get; init; }
        public string? Logo { get; init; }
        public required string Name { get; init; }
    }
    public sealed class Set : System.IEquatable<TcgDex.Models.Set>
    {
        public Set() { }
        public TcgDex.Models.SetAbbreviation? Abbreviation { get; init; }
        public TcgDex.Models.CardCount? CardCount { get; init; }
        public System.Collections.Generic.IReadOnlyList<TcgDex.Models.CardBrief> Cards { get; init; }
        public required string Id { get; init; }
        public TcgDex.Models.Legality? Legal { get; init; }
        public string? Logo { get; init; }
        public required string Name { get; init; }
        public string? ReleaseDate { get; init; }
        public TcgDex.Models.SerieBrief? Serie { get; init; }
        public string? Symbol { get; init; }
        public string? TcgOnline { get; init; }
    }
    public sealed class SetAbbreviation : System.IEquatable<TcgDex.Models.SetAbbreviation>
    {
        public SetAbbreviation() { }
        public string? Localized { get; init; }
        public string? Official { get; init; }
    }
    public sealed class SetBrief : System.IEquatable<TcgDex.Models.SetBrief>
    {
        public SetBrief() { }
        public TcgDex.Models.CardCount? CardCount { get; init; }
        public required string Id { get; init; }
        public string? Logo { get; init; }
        public required string Name { get; init; }
        public string? Symbol { get; init; }
    }
    public sealed class TcgDexProblem : System.IEquatable<TcgDex.Models.TcgDexProblem>
    {
        public TcgDexProblem() { }
        public string? Details { get; init; }
        public string? Endpoint { get; init; }
        public string? Error { get; init; }
        public bool IsLanguageError { get; }
        public string? Lang { get; init; }
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
        public string? EvolveFrom { get; init; }
        public int? Hp { get; init; }
        public string? Id { get; init; }
        public string? Illustrator { get; init; }
        public string? LocalId { get; init; }
        public string? Name { get; init; }
        public string? Rarity { get; init; }
        public string? RegulationMark { get; init; }
        public int? Retreat { get; init; }
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
    public enum QueryOperator
    {
        Like = 0,
        NotLike = 1,
        Equal = 2,
        NotEqual = 3,
        GreaterThan = 4,
        GreaterThanOrEqual = 5,
        LessThan = 6,
        LessThanOrEqual = 7,
        Null = 8,
        NotNull = 9,
    }
}
namespace TcgDex.Resources
{
    public interface ICardResource
    {
        System.Threading.Tasks.Task<TcgDex.Models.Card?> GetAsync(string id, System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<TcgDex.Models.CardBrief>> ListAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<TcgDex.Models.CardBrief>> ListAsync(TcgDex.Querying.CardQuery query, System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<TcgDex.Models.Card>> SearchDetailedAsync(TcgDex.Querying.CardFilter filter, int? page = default, int? itemsPerPage = default, System.Threading.CancellationToken cancellationToken = default);
        System.Collections.Generic.IAsyncEnumerable<TcgDex.Models.CardBrief> StreamAsync(TcgDex.Querying.CardQuery query, int pageSize = 100, System.Threading.CancellationToken cancellationToken = default);
    }
    public interface ICatalogResource
    {
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> CategoriesAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<int>> DexIdsAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> EnergyTypesAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<int>> HitPointsAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> IllustratorsAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> RaritiesAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> RegulationMarksAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<int>> RetreatCostsAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> StagesAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> SuffixesAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> TrainerTypesAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> TypesAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> VariantsAsync(System.Threading.CancellationToken cancellationToken = default);
    }
    public interface IRandomResource
    {
        System.Threading.Tasks.Task<TcgDex.Models.Card> CardAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<TcgDex.Models.Serie> SerieAsync(System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<TcgDex.Models.Set> SetAsync(System.Threading.CancellationToken cancellationToken = default);
    }
    public interface ISerieResource
    {
        System.Threading.Tasks.Task<TcgDex.Models.Serie?> GetAsync(string id, System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<TcgDex.Models.SerieBrief>> ListAsync(System.Threading.CancellationToken cancellationToken = default);
    }
    public interface ISetResource
    {
        System.Threading.Tasks.Task<TcgDex.Models.Set?> GetAsync(string id, System.Threading.CancellationToken cancellationToken = default);
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<TcgDex.Models.SetBrief>> ListAsync(System.Threading.CancellationToken cancellationToken = default);
    }
}
namespace TcgDex.Serialization
{
    [System.Text.Json.Serialization.JsonSerializable(typeof(System.Collections.Generic.IReadOnlyList<TcgDex.Models.CardBrief>))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(System.Collections.Generic.IReadOnlyList<TcgDex.Models.SerieBrief>))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(System.Collections.Generic.IReadOnlyList<TcgDex.Models.SetBrief>))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(System.Collections.Generic.IReadOnlyList<int>))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(System.Collections.Generic.IReadOnlyList<string>))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.Ability))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.Attack))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.Booster))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.Card))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.CardBrief))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.CardCount))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.CardmarketPricing))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.DetailedVariant))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.Legality))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.Pricing))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.Serie))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.SerieBrief))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.Set))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.SetAbbreviation))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.SetBrief))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.TcgDexProblem))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.TcgPlayerPrice))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.TcgPlayerPricing))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.Variants))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TcgDex.Models.WeaknessOrResistance))]
    [System.Text.Json.Serialization.JsonSourceGenerationOptions(DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull, PropertyNameCaseInsensitive=true, PropertyNamingPolicy=System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase, ReadCommentHandling=System.Text.Json.JsonCommentHandling.Disallow)]
    public sealed class TcgDexJsonContext : System.Text.Json.Serialization.JsonSerializerContext, System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver
    {
        public TcgDexJsonContext() { }
        public TcgDexJsonContext(System.Text.Json.JsonSerializerOptions options) { }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.Ability> Ability { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.Attack> Attack { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<bool> Boolean { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.Booster> Booster { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.Card> Card { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.CardBrief> CardBrief { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.CardCount> CardCount { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.CardmarketPricing> CardmarketPricing { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<System.DateTimeOffset> DateTimeOffset { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<decimal> Decimal { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.DetailedVariant> DetailedVariant { get; }
        protected override System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<System.Collections.Generic.IReadOnlyList<TcgDex.Models.Ability>> IReadOnlyListAbility { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<System.Collections.Generic.IReadOnlyList<TcgDex.Models.Attack>> IReadOnlyListAttack { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<System.Collections.Generic.IReadOnlyList<TcgDex.Models.Booster>> IReadOnlyListBooster { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<System.Collections.Generic.IReadOnlyList<TcgDex.Models.CardBrief>> IReadOnlyListCardBrief { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<System.Collections.Generic.IReadOnlyList<TcgDex.Models.DetailedVariant>> IReadOnlyListDetailedVariant { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<System.Collections.Generic.IReadOnlyList<int>> IReadOnlyListInt32 { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<System.Collections.Generic.IReadOnlyList<TcgDex.Models.SerieBrief>> IReadOnlyListSerieBrief { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<System.Collections.Generic.IReadOnlyList<TcgDex.Models.SetBrief>> IReadOnlyListSetBrief { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<System.Collections.Generic.IReadOnlyList<string>> IReadOnlyListString { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<System.Collections.Generic.IReadOnlyList<TcgDex.Models.WeaknessOrResistance>> IReadOnlyListWeaknessOrResistance { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<int> Int32 { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.Legality> Legality { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<System.DateTimeOffset?> NullableDateTimeOffset { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<decimal?> NullableDecimal { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<int?> NullableInt32 { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.Pricing> Pricing { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.Serie> Serie { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.SerieBrief> SerieBrief { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.Set> Set { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.SetAbbreviation> SetAbbreviation { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.SetBrief> SetBrief { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<string> String { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.TcgDexProblem> TcgDexProblem { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.TcgPlayerPrice> TcgPlayerPrice { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.TcgPlayerPricing> TcgPlayerPricing { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.Variants> Variants { get; }
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TcgDex.Models.WeaknessOrResistance> WeaknessOrResistance { get; }
        public static TcgDex.Serialization.TcgDexJsonContext Default { get; }
        public override System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(System.Type type) { }
    }
}
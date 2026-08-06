namespace TcgDex.IntegrationTests;

/// <summary>
/// Every supported language, exercised against the live API.
/// </summary>
/// <remarks>
/// <para>
/// The SDK hard-codes its language list, so it can drift from the service. If
/// TCGdex adds or drops a language, these tests are what notices.
/// </para>
/// <para>
/// <b>Card ids are not universal across languages.</b> Each language is backed
/// by its own card pool: <c>swsh3-136</c> is a Western card and returns 404 in
/// <c>ja</c>, <c>ko</c>, <c>th</c>, <c>id</c>, <c>zh-cn</c> and <c>pt-br</c>,
/// whose databases hold different sets. So a language is proven live by asking
/// it for its <em>own</em> first card rather than for a shared id.
/// </para>
/// </remarks>
[TestFixture]
public sealed class LanguageTests : LiveApiFixture
{
    /// <summary>
    /// Languages that share the Western card pool and genuinely localise names,
    /// used to prove the language segment changes the response rather than
    /// being silently ignored.
    /// </summary>
    private static readonly (string Language, string Expected)[] LocalisedNames =
    [
        (TcgDexLanguages.English, "Furret"),
        (TcgDexLanguages.French, "Fouinar"),
        (TcgDexLanguages.German, "Wiesenior"),
    ];

    /// <summary>
    /// Languages the API accepts but has not populated with card data. They
    /// return HTTP 200 with empty arrays rather than an error, and
    /// <c>nl</c>, <c>pl</c> and <c>ru</c> carry a handful of sets with no cards
    /// in them.
    /// </summary>
    private static readonly string[] LanguagesWithoutCardData = ["pt-pt", "nl", "pl", "ru"];

    [TestCaseSource(nameof(PopulatedLanguages))]
    public async Task PopulatedLanguages_ServeTheirOwnCards(string language)
    {
        using var httpClient = new HttpClient();
        var client = new TcgDexClient(httpClient, new TcgDexOptions { Language = language });

        var page = await client.Cards.ListAsync(new CardQuery().Page(1, 1), Timeout);

        page.ShouldNotBeEmpty($"language '{language}' is advertised as supported");

        // Round-trip that language's own card id, proving detail lookups work
        // there too and not just the list endpoint.
        var card = await client.Cards.GetAsync(page[0].Id, Timeout);

        card.ShouldNotBeNull($"'{page[0].Id}' came from the {language} list, so it must resolve there");
        card.Name.ShouldNotBeNullOrWhiteSpace();
    }

    [TestCaseSource(nameof(PopulatedLanguages))]
    public async Task PopulatedLanguages_ServeCatalogData(string language)
    {
        using var httpClient = new HttpClient();
        var client = new TcgDexClient(httpClient, new TcgDexOptions { Language = language });

        var categories = await client.Catalog.CategoriesAsync(Timeout);

        categories.ShouldNotBeEmpty($"language '{language}' should expose categories");
    }

    [TestCaseSource(nameof(EmptyLanguages))]
    public async Task LanguagesWithoutData_ReturnEmptyRatherThanFailing(string language)
    {
        // The API accepts these but has no card data for them. Empty results
        // are the correct outcome — the SDK must not turn them into errors, and
        // must not turn them into nulls either.
        using var httpClient = new HttpClient();
        var client = new TcgDexClient(httpClient, new TcgDexOptions { Language = language });

        var cards = await client.Cards.ListAsync(new CardQuery().Page(1, 5), Timeout);
        var categories = await client.Catalog.CategoriesAsync(Timeout);

        cards.ShouldNotBeNull();
        cards.ShouldBeEmpty();
        categories.ShouldNotBeNull();
        categories.ShouldBeEmpty();
    }

    [TestCaseSource(nameof(LocalisedNameCases))]
    public async Task WesternLanguages_ReturnTranslatedNames(string language, string expected)
    {
        using var httpClient = new HttpClient();
        var client = new TcgDexClient(httpClient, new TcgDexOptions { Language = language });

        var card = await client.Cards.GetAsync("swsh3-136", Timeout);

        card.ShouldNotBeNull().Name.ShouldBe(expected);
    }

    [TestCase("ja")]
    [TestCase("ko")]
    [TestCase("zh-cn")]
    public async Task AsianLanguages_DoNotShareWesternCardIds(string language)
    {
        // Documents the pool split rather than treating it as a defect: a
        // missing card is a clean null, not an exception.
        using var httpClient = new HttpClient();
        var client = new TcgDexClient(httpClient, new TcgDexOptions { Language = language });

        var card = await client.Cards.GetAsync("swsh3-136", Timeout);

        card.ShouldBeNull($"'{language}' has its own card pool and does not contain this Western id");
    }

    [Test]
    public void UnsupportedLanguage_IsRejectedBeforeAnyRequest()
    {
        // Validation happens at construction, so no request is made at all and
        // the message names the valid set.
        using var httpClient = new HttpClient();

        var exception = Should.Throw<ArgumentException>(
            () => new TcgDexClient(httpClient, new TcgDexOptions { Language = "zz" }));

        exception.Message.ShouldContain("zz");
        exception.Message.ShouldContain("en");
    }

    private static IEnumerable<string> AllLanguages() => TcgDexLanguages.All;

    private static IEnumerable<string> PopulatedLanguages()
        => TcgDexLanguages.All.Except(LanguagesWithoutCardData);

    private static string[] EmptyLanguages() => LanguagesWithoutCardData;

    private static IEnumerable<TestCaseData> LocalisedNameCases()
        => LocalisedNames.Select(c => new TestCaseData(c.Language, c.Expected));
}

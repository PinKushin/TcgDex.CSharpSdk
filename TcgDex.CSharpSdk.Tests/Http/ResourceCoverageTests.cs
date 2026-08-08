namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;
using TcgDex.Models;
using TcgDex.Querying;

/// <summary>
/// Every remaining resource method, asserted by the request URI it produces.
/// </summary>
/// <remarks>
/// Mechanical but not pointless: the enumeration paths are hyphenated
/// (<c>energy-types</c>, <c>regulation-marks</c>, <c>dex-ids</c>) and trivially
/// mistyped as camelCase, which would 404 at runtime with no compile-time
/// signal.
/// </remarks>
[TestFixture]
public sealed class ResourceCoverageTests
{
    private static TcgDexClient CreateClient(RecordingHandler handler)
        => new(new HttpClient(handler), new TcgDexOptions());

    private static async Task<string> UriForAsync(
        Func<ITcgDexClient, CancellationToken, Task> call,
        string responseFixture = "list-categories.json")
    {
        RecordingHandler handler = new RecordingHandler().RespondWithJsonFile(HttpStatusCode.OK, responseFixture);

        await call(CreateClient(handler), CancellationToken.None);

        return handler.SingleRequestUri;
    }

    [Test]
    public async Task Catalog_TextEndpoints_UseTheDocumentedPaths()
    {
        (await UriForAsync((c, t) => c.Catalog.CategoriesAsync(t)))
            .ShouldEndWith("/en/categories");
        (await UriForAsync((c, t) => c.Catalog.RaritiesAsync(t)))
            .ShouldEndWith("/en/rarities");
        (await UriForAsync((c, t) => c.Catalog.TypesAsync(t)))
            .ShouldEndWith("/en/types");
        (await UriForAsync((c, t) => c.Catalog.IllustratorsAsync(t)))
            .ShouldEndWith("/en/illustrators");
        (await UriForAsync((c, t) => c.Catalog.StagesAsync(t)))
            .ShouldEndWith("/en/stages");
        (await UriForAsync((c, t) => c.Catalog.SuffixesAsync(t)))
            .ShouldEndWith("/en/suffixes");
        (await UriForAsync((c, t) => c.Catalog.VariantsAsync(t)))
            .ShouldEndWith("/en/variants");
    }

    [Test]
    public async Task Catalog_HyphenatedEndpoints_KeepTheirHyphens()
    {
        (await UriForAsync((c, t) => c.Catalog.EnergyTypesAsync(t)))
            .ShouldEndWith("/en/energy-types");
        (await UriForAsync((c, t) => c.Catalog.RegulationMarksAsync(t)))
            .ShouldEndWith("/en/regulation-marks");
        (await UriForAsync((c, t) => c.Catalog.TrainerTypesAsync(t)))
            .ShouldEndWith("/en/trainer-types");
        (await UriForAsync((c, t) => c.Catalog.DexIdsAsync(t), "list-retreats-int.json"))
            .ShouldEndWith("/en/dex-ids");
    }

    [Test]
    public async Task Catalog_NumericEndpoints_UseTheDocumentedPaths()
    {
        (await UriForAsync((c, t) => c.Catalog.HitPointsAsync(t), "list-retreats-int.json"))
            .ShouldEndWith("/en/hp");
        (await UriForAsync((c, t) => c.Catalog.RetreatCostsAsync(t), "list-retreats-int.json"))
            .ShouldEndWith("/en/retreats");
    }

    [Test]
    public async Task Random_UsesTheSingularResourceNames()
    {
        // `random/serie`, not `random/series` — the opposite of the list
        // endpoint, which is an easy inconsistency to get wrong.
        (await UriForAsync((c, t) => c.Random.CardAsync(t), "card-pokemon-full.json"))
            .ShouldEndWith("/en/random/card");
        (await UriForAsync((c, t) => c.Random.SetAsync(t), "set-full.json"))
            .ShouldEndWith("/en/random/set");
        (await UriForAsync((c, t) => c.Random.SerieAsync(t), "serie-full.json"))
            .ShouldEndWith("/en/random/serie");
    }

    [Test]
    public async Task Sets_ListAsync_RequestsTheCollection()
        => (await UriForAsync((c, t) => c.Sets.ListAsync(t), "list-cards-brief.json"))
            .ShouldEndWith("/en/sets");

    [Test]
    public async Task Series_ListAsync_RequestsTheCollection()
        => (await UriForAsync((c, t) => c.Series.ListAsync(t), "list-cards-brief.json"))
            .ShouldEndWith("/en/series");

    [Test]
    public async Task Sets_GetAsync_WhenMissing_ReturnsNull()
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.NotFound, "error-not-found.json");

        (await CreateClient(handler).Sets.GetAsync("nope", CancellationToken.None)).ShouldBeNull();
    }

    [Test]
    public async Task Series_GetAsync_WhenMissing_ReturnsNull()
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.NotFound, "error-not-found.json");

        (await CreateClient(handler).Series.GetAsync("nope", CancellationToken.None)).ShouldBeNull();
    }

    // ----- option and filter guards -----

    [Test]
    public void Options_WithRelativeBaseAddress_AreRejected()
    {
        TcgDexOptions options = new() { BaseAddress = new Uri("/v2/", UriKind.Relative) };

        Should.Throw<ArgumentException>(options.Validate)
            .Message.ShouldContain("absolute");
    }

    [Test]
    public void Options_GraphQlEndpoint_IsSeparateFromBaseAddress()
    {
        // GraphQL sits outside the language segment, so it is configured
        // independently rather than derived from BaseAddress.
        TcgDexOptions options = new();

        options.GraphQlEndpoint.ToString().ShouldNotContain("/en/");
        options.GraphQlEndpoint.ToString().ShouldEndWith("/graphql");
    }

    [TestCase("line\nbreak", @"line\nbreak")]
    [TestCase("carriage\rreturn", @"carriage\rreturn")]
    [TestCase("tab\there", @"tab\there")]
    [TestCase("back\\slash", @"back\\slash")]
    public async Task CardFilter_EscapesControlCharacters(string value, string expectedEscaped)
    {
        // Any of these left raw would produce a malformed GraphQL document.
        // Asserted by looking for the escaped two-character sequence inside the
        // filter argument — the document as a whole legitimately contains
        // newlines, because the field selection spans several lines.
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, """{"data":{"cards":[]}}""");

        await CreateClient(handler).Cards.SearchDetailedAsync(
            new CardFilter { Illustrator = value }, cancellationToken: CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(handler.SingleRequestBody);
        string query = document.RootElement.GetProperty("query").GetString()!;

        query.ShouldContain(expectedEscaped);
    }

    [Test]
    public void DetailedVariant_WithNullStamp_CoercesToEmpty()
        => new DetailedVariant { Stamp = null! }.Stamp.ShouldBeEmpty();

    // ----- exception constructors -----

    [Test]
    public void ApiException_DefaultConstructor_HasNoStatus()
    {
        TcgDexApiException exception = new();

        exception.StatusCode.ShouldBeNull();
        exception.Problem.ShouldBeNull();
        exception.IsLanguageError.ShouldBeFalse();
    }

    [Test]
    public void ApiException_MessageConstructor_KeepsTheMessage()
        => new TcgDexApiException("something went wrong").Message.ShouldBe("something went wrong");

    [Test]
    public void ApiException_WithInnerException_KeepsBoth()
    {
        InvalidOperationException inner = new("cause");

        TcgDexApiException exception = new("outer", inner);

        exception.Message.ShouldBe("outer");
        exception.InnerException.ShouldBeSameAs(inner);
    }
}

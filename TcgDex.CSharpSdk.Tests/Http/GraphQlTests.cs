namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;
using TcgDex.Querying;

/// <summary>
/// The GraphQL search path: the document sent, and the failure handling.
/// </summary>
[TestFixture]
public sealed class GraphQlTests
{
    private const string EmptyResult = """{"data":{"cards":[]}}""";

    private static TcgDexClient CreateClient(RecordingHandler handler)
        => new(new HttpClient(handler), new TcgDexOptions());

    /// <summary>
    /// Runs a search and returns the GraphQL document that was sent.
    /// </summary>
    /// <remarks>
    /// The body is decoded rather than matched as raw text: System.Text.Json
    /// escapes quotes as <c>"</c> by default, so asserting on the wire
    /// encoding would test the serializer's escaping rather than the query the
    /// SDK builds.
    /// </remarks>
    private static async Task<string> CaptureQueryAsync(
        RecordingHandler handler,
        CardFilter filter,
        int? page = null,
        int? itemsPerPage = null)
    {
        await CreateClient(handler).Cards
            .SearchDetailedAsync(filter, page, itemsPerPage, CancellationToken.None);

        using var document = JsonDocument.Parse(handler.SingleRequestBody);

        return document.RootElement.GetProperty("query").GetString()!;
    }

    [Test]
    public async Task SearchDetailed_PostsToTheGraphQlEndpoint()
    {
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, EmptyResult);

        await CreateClient(handler).Cards
            .SearchDetailedAsync(new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None);

        // GraphQL sits outside the language segment, because it has no language
        // support at all.
        handler.SingleRequestUri.ShouldBe("https://api.tcgdex.net/v2/graphql");
        handler.SingleRequest.Method.ShouldBe(HttpMethod.Post);
    }

    [Test]
    public async Task SearchDetailed_SendsEqualityFilterArguments()
    {
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, EmptyResult);

        var query = await CaptureQueryAsync(handler, new CardFilter { Name = "Furret", Hp = 110 });

        query.ShouldContain("""filters:{name:"Furret",hp:110}""");
    }

    [Test]
    public async Task SearchDetailed_SendsPaginationArguments()
    {
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, EmptyResult);

        var query = await CaptureQueryAsync(handler, new CardFilter { Category = "Pokemon" }, 2, 25);

        query.ShouldContain("pagination:{page:2,itemsPerPage:25}");
    }

    [Test]
    public async Task SearchDetailed_RequestsTheDetailFieldsThatMakeItWorthwhile()
    {
        // The entire point of this path is full detail in one round trip. If the
        // selection loses these, it is just a slower version of the REST list.
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, EmptyResult);

        var query = await CaptureQueryAsync(handler, new CardFilter { Name = "Furret" });

        foreach (var field in new[] { "hp", "types", "attacks", "weaknesses", "legal", "set" })
        {
            query.ShouldContain(field);
        }
    }

    [Test]
    public async Task SearchDetailed_DoesNotRequestFieldsTheSchemaLacks()
    {
        // GraphQL's Card has no pricing and no updated field; asking for either
        // fails the whole query.
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, EmptyResult);

        var query = await CaptureQueryAsync(handler, new CardFilter { Name = "Furret" });

        query.ShouldNotContain("pricing");
        query.ShouldNotContain("updated");
        query.ShouldNotContain("variants_detailed");
    }

    [Test]
    public async Task SearchDetailed_EscapesQuotesInFilterValues()
    {
        // An unescaped quote would terminate the GraphQL string literal and
        // change the query being executed.
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, EmptyResult);

        var query = await CaptureQueryAsync(handler, new CardFilter { Illustrator = "\"Big Mama\" Tagawa" });

        // Quotes are backslash-escaped inside the GraphQL string literal, so the
        // value cannot break out of it and alter the query.
        query.ShouldContain("\\\"Big Mama\\\" Tagawa");
    }

    [Test]
    public async Task SearchDetailed_WithNoFilter_OmitsTheArgumentList()
    {
        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, EmptyResult);

        var query = await CaptureQueryAsync(handler, new CardFilter());

        query.ShouldContain("{ cards {");
    }

    [Test]
    public async Task SearchDetailed_DeserializesCards()
    {
        const string Response = """
            {"data":{"cards":[
              {"id":"swsh3-136","name":"Furret","category":"Pokemon","localId":"136",
               "hp":110,"types":["Colorless"],
               "set":{"id":"swsh3","name":"Darkness Ablaze"}}
            ]}}
            """;

        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, Response);

        var cards = await CreateClient(handler).Cards
            .SearchDetailedAsync(new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None);

        var card = cards.ShouldHaveSingleItem();
        card.Name.ShouldBe("Furret");
        card.Hp.ShouldBe(110);
    }

    [Test]
    public void SearchDetailed_WhenServerReportsErrors_Throws()
    {
        // GraphQL answers HTTP 200 even for a failed query, so the errors array
        // is the only reliable failure signal.
        const string Response = """
            {"errors":[{"message":"Cannot return null for non-nullable field Card.rarity."}]}
            """;

        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, Response);

        var exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None)).Result;

        exception.Message.ShouldContain("non-nullable");
    }

    [Test]
    public async Task SearchDetailed_DropsNullEntriesRatherThanReturningThem()
    {
        // The server nulls an entry it cannot fully resolve. Handing that back
        // would force callers to null-check every element.
        const string Response = """
            {"data":{"cards":[null,{"id":"swsh3-136","name":"Furret","category":"Pokemon","localId":"136",
             "set":{"id":"swsh3","name":"Darkness Ablaze"}}]}}
            """;

        var handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, Response);

        var cards = await CreateClient(handler).Cards
            .SearchDetailedAsync(new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None);

        cards.ShouldHaveSingleItem().Name.ShouldBe("Furret");
    }
}

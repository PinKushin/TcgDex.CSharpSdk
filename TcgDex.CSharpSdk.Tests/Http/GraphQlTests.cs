namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TcgDex;
using TcgDex.Models;
using TcgDex.Querying;
using TcgDex.Tests.Diagnostics;

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

        using JsonDocument document = JsonDocument.Parse(handler.SingleRequestBody);

        return document.RootElement.GetProperty("query").GetString()!;
    }

    [Test]
    public async Task SearchDetailed_PostsToTheGraphQlEndpoint()
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, EmptyResult);

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
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, EmptyResult);

        string query = await CaptureQueryAsync(handler, new CardFilter { Name = "Furret", Hp = 110 });

        query.ShouldContain("""filters:{name:"Furret",hp:110}""");
    }

    [Test]
    public async Task SearchDetailed_SendsPaginationArguments()
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, EmptyResult);

        string query = await CaptureQueryAsync(handler, new CardFilter { Category = "Pokemon" }, 2, 25);

        query.ShouldContain("pagination:{page:2,itemsPerPage:25}");
    }

    [Test]
    public async Task SearchDetailed_RequestsTheDetailFieldsThatMakeItWorthwhile()
    {
        // The entire point of this path is full detail in one round trip. If the
        // selection loses these, it is just a slower version of the REST list.
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, EmptyResult);

        string query = await CaptureQueryAsync(handler, new CardFilter { Name = "Furret" });

        foreach (string? field in new[] { "hp", "types", "attacks", "weaknesses", "legal", "set" })
        {
            query.ShouldContain(field);
        }
    }

    [Test]
    public async Task SearchDetailed_DoesNotRequestFieldsTheSchemaLacks()
    {
        // GraphQL's Card has no pricing and no updated field; asking for either
        // fails the whole query.
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, EmptyResult);

        string query = await CaptureQueryAsync(handler, new CardFilter { Name = "Furret" });

        query.ShouldNotContain("pricing");
        query.ShouldNotContain("updated");
        query.ShouldNotContain("variants_detailed");
    }

    [Test]
    public async Task SearchDetailed_EscapesQuotesInFilterValues()
    {
        // An unescaped quote would terminate the GraphQL string literal and
        // change the query being executed.
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, EmptyResult);

        string query = await CaptureQueryAsync(handler, new CardFilter { Illustrator = "\"Big Mama\" Tagawa" });

        // Quotes are backslash-escaped inside the GraphQL string literal, so the
        // value cannot break out of it and alter the query.
        query.ShouldContain("\\\"Big Mama\\\" Tagawa");
    }

    [Test]
    public async Task SearchDetailed_EscapesControlCharactersInFilterValues()
    {
        // Not an injection route — only a quote or a backslash can break out of
        // a string literal, and both are handled above. This is about producing a
        // *valid* query: the GraphQL grammar forbids raw control characters inside
        // a string, so passing one through unescaped turns a caller's odd input
        // into a syntax error from the server rather than a clean result.
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, EmptyResult);

        // Built from char codes rather than written as literals: control bytes
        // pasted into a source file are invisible and get mangled in transit.
        string hostile = "Furret" + (char)0x08 + (char)0x0C + (char)0x01 + (char)0x1F;

        string query = await CaptureQueryAsync(handler, new CardFilter { Name = hostile });

        // Backspace and form feed have dedicated escapes in the grammar;
        // everything else below U+0020 has to go out as a \u escape.
        query.ShouldContain(@"Furret\b\f");

        // The \u forms for the two without dedicated escapes. Asserted as the
        // hex portion so this file never contains a backslash-u sequence that
        // an editor or a pipe might reinterpret.
        query.ShouldContain("u0001");
        query.ShouldContain("u001F");

        // And no raw control byte survives, or the escaping achieved nothing.
        query.ShouldNotContain(((char)0x08).ToString());
        query.ShouldNotContain(((char)0x01).ToString());
    }

    [Test]
    public async Task SearchDetailed_WithNoFilter_OmitsTheArgumentList()
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, EmptyResult);

        string query = await CaptureQueryAsync(handler, new CardFilter());

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

        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, Response);

        IReadOnlyList<Card> cards = await CreateClient(handler).Cards
            .SearchDetailedAsync(new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None);

        Card card = cards.ShouldHaveSingleItem();
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

        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, Response);

        TcgDexApiException exception = Should.ThrowAsync<TcgDexApiException>(async () =>
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

        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, Response);

        IReadOnlyList<Card> cards = await CreateClient(handler).Cards
            .SearchDetailedAsync(new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None);

        cards.ShouldHaveSingleItem().Name.ShouldBe("Furret");
    }

    // ----- boundaries that fire on empty collections -----

    [Test]
    public async Task AnEmptyErrorsArray_IsNotTreatedAsAFailure()
    {
        // `Errors is { Count: > 0 }` versus `>= 0`. A server that returns an
        // empty errors array alongside good data is reporting success, and
        // treating that as a failure would break every such response. Only a
        // present-but-empty array distinguishes the two — null and non-empty
        // both behave the same either way.
        RecordingHandler handler = new RecordingHandler().RespondWith(
            HttpStatusCode.OK,
            """{"data":{"cards":[]},"errors":[]}""");

        IReadOnlyList<Card> cards = await CreateClient(handler).Cards.SearchDetailedAsync(
            new CardFilter { Name = "Furret" },
            cancellationToken: CancellationToken.None);

        cards.ShouldBeEmpty();
    }

    [Test]
    public void ReportedErrors_AreJoinedIntoTheException()
    {
        // Every message, separated — not just the first, and not run together.
        // A server reporting two problems should surface both.
        RecordingHandler handler = new RecordingHandler().RespondWith(
            HttpStatusCode.OK,
            """{"errors":[{"message":"first problem"},{"message":"second problem"}]}""");

        TcgDexApiException exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" },
                cancellationToken: CancellationToken.None)).Result;

        exception.Message.ShouldContain("first problem");
        exception.Message.ShouldContain("second problem");
        exception.Message.ShouldContain("; ");
    }

    [Test]
    public async Task WhenNothingIsDropped_NoWarningIsLogged()
    {
        // `dropped > 0` versus `>= 0`. The mutated form warns about zero
        // dropped entries on every successful search — noise that would train
        // a consumer to ignore the warning that matters.
        RecordingLogger log = new(LogLevel.Trace);

        RecordingHandler handler = new RecordingHandler().RespondWith(
            HttpStatusCode.OK,
            """{"data":{"cards":[{"id":"swsh3-136","name":"Furret","category":"Pokemon","localId":"136","set":{"id":"swsh3","name":"Darkness Ablaze"}}]}}""");

        TcgDexClient client = new(new HttpClient(handler), new TcgDexOptions(), log.Factory);

        IReadOnlyList<Card> cards = await client.Cards.SearchDetailedAsync(
            new CardFilter { Name = "Furret" },
            cancellationToken: CancellationToken.None);

        cards.Count.ShouldBe(1);
        log.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
    }

    // ----- argument separators -----

    [Test]
    public async Task ASingleArgument_CarriesNoLeadingComma()
    {
        // The separator is only written between arguments. With the guard
        // removed the document opens with a stray comma, which the server
        // rejects — and no test looked at a single-argument query.
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, EmptyResult);

        string query = await CaptureQueryAsync(handler, new CardFilter { Name = "Furret" });

        query.ShouldNotContain("(,");
        query.ShouldNotContain("(filters:{name:\"Furret\"},)");
    }

    [Test]
    public async Task FiltersAndPaging_AreSeparatedFromEachOther()
    {
        // And with more than one argument the separator must actually appear,
        // or the document runs them together into nonsense.
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, EmptyResult);

        string query = await CaptureQueryAsync(
            handler,
            new CardFilter { Name = "Furret" },
            page: 2,
            itemsPerPage: 50);

        query.ShouldContain("},pagination:");
    }

    // ----- failure messages -----

    [Test]
    public void AnHttpFailure_NamesTheStatusCode()
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.BadGateway, "{}");

        TcgDexApiException exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" },
                cancellationToken: CancellationToken.None)).Result;

        exception.Message.ShouldContain("502");
        exception.Message.ShouldContain("GraphQL");
    }

    [Test]
    public void ANetworkFailure_SaysSo()
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(_ => throw new HttpRequestException("connection reset"));

        TcgDexApiException exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" },
                cancellationToken: CancellationToken.None)).Result;

        exception.Message.ShouldContain("could not be completed");
    }

    [Test]
    public void AMalformedBody_SaysItWasNotValidJson()
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, "<html>nope</html>");

        TcgDexApiException exception = Should.ThrowAsync<TcgDexApiException>(async () =>
            await CreateClient(handler).Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" },
                cancellationToken: CancellationToken.None)).Result;

        exception.Message.ShouldContain("not valid JSON");
    }}

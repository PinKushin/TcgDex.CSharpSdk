namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;
using TcgDex.Querying;

/// <summary>
/// Options are read once, when the client is built.
/// </summary>
/// <remarks>
/// The REST transport always copied what it needed at construction; the GraphQL
/// one kept the options object and read it per request. So mutating options
/// after construction changed GraphQL and not REST, and a change that broke the
/// options (a relative endpoint, say) reached GraphQL unvalidated. The owner
/// chose freezing both.
/// </remarks>
[TestFixture]
public sealed class OptionsFrozenTests
{
    [Test]
    public async Task GraphQl_IgnoresOptionsChangedAfterConstruction()
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, """{"data":{"cards":[]}}""");
        TcgDexOptions options = new();
        TcgDexClient client = new(new HttpClient(handler), options);

        options.GraphQlEndpoint = new Uri("https://elsewhere.example/graphql");
        options.MaxResponseBytes = 1;

        await client.Cards.SearchDetailedAsync(new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None);

        handler.SingleRequestUri.ShouldBe("https://api.tcgdex.net/v2/graphql");
    }

    [Test]
    public async Task Rest_IgnoresOptionsChangedAfterConstruction()
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.NotFound, string.Empty);
        TcgDexOptions options = new();
        TcgDexClient client = new(new HttpClient(handler), options);

        options.Language = TcgDexLanguages.German;

        await client.Cards.GetAsync("swsh3-136", CancellationToken.None);

        handler.SingleRequestUri.ShouldBe("https://api.tcgdex.net/v2/en/cards/swsh3-136");
    }
}

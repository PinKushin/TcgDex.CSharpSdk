namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;

/// <summary>
/// <c>GetValuesAsync</c> and <c>GetNumberValuesAsync</c>: the way to reach an
/// enumeration endpoint the SDK has no named method for yet (DECISIONS #36).
/// </summary>
[TestFixture]
public sealed class CatalogEscapeHatchTests
{
    private static TcgDexClient CreateClient(RecordingHandler handler)
        => new(new HttpClient(handler), new TcgDexOptions());

    [Test]
    public async Task GetValuesAsync_RequestsTheNamedEndpointAndReturnsItsText()
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, """["Alpha","Beta"]""");

        IReadOnlyList<string> values = await CreateClient(handler).Catalog.GetValuesAsync("boosters", CancellationToken.None);

        handler.SingleRequestUri.ShouldBe("https://api.tcgdex.net/v2/en/boosters");
        values.ShouldBe(["Alpha", "Beta"]);
    }

    [Test]
    public async Task GetNumberValuesAsync_RequestsTheNamedEndpointAndReturnsItsNumbers()
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, "[3,5,8]");

        IReadOnlyList<int> values = await CreateClient(handler).Catalog.GetNumberValuesAsync("hp-steps", CancellationToken.None);

        handler.SingleRequestUri.ShouldBe("https://api.tcgdex.net/v2/en/hp-steps");
        values.ShouldBe([3, 5, 8]);
    }

    [Test]
    public async Task GetValuesAsync_EscapesTheName_SoItStaysOneSegment()
    {
        // A name is data, not a path: "../cards" must not reach a different
        // endpoint than the caller named.
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, "[]");

        await CreateClient(handler).Catalog.GetValuesAsync("../cards", CancellationToken.None);

        handler.SingleRequestUri.ShouldBe("https://api.tcgdex.net/v2/en/..%2Fcards");
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task GetValuesAsync_WithABlankName_ThrowsAndSendsNothing(string name)
    {
        RecordingHandler handler = new();

        await Should.ThrowAsync<ArgumentException>(
            () => CreateClient(handler).Catalog.GetValuesAsync(name, CancellationToken.None));

        handler.Requests.ShouldBeEmpty();
    }

    [Test]
    public async Task GetNumberValuesAsync_WithANullName_ThrowsAndSendsNothing()
    {
        RecordingHandler handler = new();

        await Should.ThrowAsync<ArgumentNullException>(
            () => CreateClient(handler).Catalog.GetNumberValuesAsync(null!, CancellationToken.None));

        handler.Requests.ShouldBeEmpty();
    }
}

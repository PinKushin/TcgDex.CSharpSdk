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
/// <remarks>
/// The two methods share one escaping rule, so every rule is exercised through
/// both: a mutation that drops it from only one must fail a test.
/// </remarks>
[TestFixture]
public sealed class CatalogEscapeHatchTests
{
    private static TcgDexClient CreateClient(RecordingHandler handler)
        => new(new HttpClient(handler), new TcgDexOptions());

    /// <summary>Calls the text or the numeric method, discarding the result.</summary>
    private static async Task CallAsync(TcgDexClient client, bool numeric, string name)
    {
        if (numeric)
        {
            await client.Catalog.GetNumberValuesAsync(name, CancellationToken.None);
        }
        else
        {
            await client.Catalog.GetValuesAsync(name, CancellationToken.None);
        }
    }

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

    [TestCase(false)]
    [TestCase(true)]
    public async Task TheName_IsEscaped_SoItStaysOneSegment(bool numeric)
    {
        // A name is data, not a path: "../cards" must not reach a different
        // endpoint than the caller named.
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, "[]");

        await CallAsync(CreateClient(handler), numeric, "../cards");

        handler.SingleRequestUri.ShouldBe("https://api.tcgdex.net/v2/en/..%2Fcards");
    }

    [TestCase(false, ".")]
    [TestCase(false, "..")]
    [TestCase(true, ".")]
    [TestCase(true, "..")]
    public async Task ADotSegmentName_IsRejected_BecauseTheUriLayerWouldCollapseIt(bool numeric, string name)
    {
        // Uri.EscapeDataString leaves dots alone, and resolving "." or ".."
        // against the language base lands on /v2/en/ or /v2/: a different
        // endpoint from the one named.
        RecordingHandler handler = new();

        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
            () => CallAsync(CreateClient(handler), numeric, name));

        exception.ParamName.ShouldBe("name");
        handler.Requests.ShouldBeEmpty();
    }

    [TestCase(false, "")]
    [TestCase(false, "   ")]
    [TestCase(true, "")]
    [TestCase(true, "   ")]
    public async Task ABlankName_ThrowsNamingName_AndSendsNothing(bool numeric, string name)
    {
        RecordingHandler handler = new();

        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
            () => CallAsync(CreateClient(handler), numeric, name));

        exception.ParamName.ShouldBe("name");
        handler.Requests.ShouldBeEmpty();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ANullName_ThrowsNamingName_AndSendsNothing(bool numeric)
    {
        RecordingHandler handler = new();

        ArgumentNullException exception = await Should.ThrowAsync<ArgumentNullException>(
            () => CallAsync(CreateClient(handler), numeric, null!));

        exception.ParamName.ShouldBe("name");
        handler.Requests.ShouldBeEmpty();
    }

    [TestCase(".")]
    [TestCase("..")]
    public async Task AnId_OfDotSegments_IsRejectedToo(string id)
    {
        // The rule lives in the shared segment escaper, so the long-standing id
        // path gets it as well: GetAsync("..") would otherwise request /v2/en/cards/..
        // which resolves to /v2/en/ and fails to deserialize as a card.
        RecordingHandler handler = new();

        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
            () => CreateClient(handler).Cards.GetAsync(id, CancellationToken.None));

        exception.ParamName.ShouldBe("id");
        handler.Requests.ShouldBeEmpty();
    }
}

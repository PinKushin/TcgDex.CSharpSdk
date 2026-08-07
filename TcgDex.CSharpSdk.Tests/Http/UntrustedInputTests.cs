namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Threading;
using TcgDex;

/// <summary>
/// Identifiers that reach the SDK from outside the program.
/// </summary>
/// <remarks>
/// <para>
/// A card id is usually a constant, but an SDK cannot assume that: the common
/// shape is a search box or a route parameter feeding straight into
/// <c>Cards.GetAsync(id)</c>. So an id is untrusted input, and the property
/// that matters is that no id can move the request off the configured path.
/// </para>
/// <para>
/// These assert the URI the transport actually produced rather than that the
/// call succeeded — a request can be issued happily and still have gone
/// somewhere it should not.
/// </para>
/// </remarks>
[TestFixture]
public sealed class UntrustedInputTests
{
    private const string Base = "https://api.tcgdex.net/v2/en/";

    private static TcgDexClient CreateClient(RecordingHandler handler)
        => new(new HttpClient(handler), new TcgDexOptions());

    private static string RequestUriFor(string id)
    {
        var handler = new RecordingHandler()
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        _ = CreateClient(handler).Cards.GetAsync(id, CancellationToken.None).Result;

        return handler.SingleRequestUri;
    }

    // No case for a pre-encoded "..%2F..%2Fadmin". It looks like the most
    // hostile input of the set, but Uri never decodes %2F, so it cannot escape
    // the path even with the escaping removed entirely — verified by deleting
    // the EscapeDataString call and watching every case here fail except that
    // one. A test that passes whether or not the protection exists documents
    // nothing and would give false confidence in the suite.
    [TestCase("../../admin", TestName = "Traversal_DotDotSegments")]
    [TestCase("../../../../../../etc/passwd", TestName = "Traversal_RepeatedAscent")]
    [TestCase("%2e%2e/%2e%2e/x", TestName = "Traversal_EncodedDots")]
    [TestCase(@"\..\..\x", TestName = "Traversal_Backslashes")]
    [TestCase("a/b", TestName = "Traversal_PlainSlash")]
    public void HostileId_CannotEscapeTheConfiguredPath(string id)
    {
        var uri = RequestUriFor(id);

        // The whole property in one assertion: whatever the id contains, the
        // request still addresses a card below the configured language root.
        uri.ShouldStartWith(Base + "cards/");

        // And the separators are encoded rather than structural, so the id
        // cannot introduce a path segment of its own.
        uri.Substring((Base + "cards/").Length).ShouldNotContain("/");
    }

    [Test]
    public void AbsoluteUrlAsId_IsTreatedAsAnId_NotADestination()
    {
        // The dangerous shape: if the id were concatenated rather than escaped,
        // Uri resolution would treat an absolute URL as a replacement and the
        // request would leave for another host entirely.
        var uri = RequestUriFor("https://evil.example/x");

        uri.ShouldStartWith(Base + "cards/");
        uri.ShouldNotContain("evil.example/x");
    }

    [Test]
    public void LegitimateIdWithReservedCharacters_SurvivesUnchanged()
    {
        // exu-! is a real card. Escaping must not be so aggressive that valid
        // ids stop resolving — a test that only proves hostile input is blocked
        // would pass just as well if every id were mangled.
        RequestUriFor("exu-!").ShouldBe(Base + "cards/exu-%21");
    }
}

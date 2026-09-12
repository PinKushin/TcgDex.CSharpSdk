namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;
using TcgDex.Querying;

/// <summary>
/// The <c>User-Agent</c> both transports identify themselves with.
/// </summary>
/// <remarks>
/// Requested by TCGdex directly, to identify traffic in logs that carry no IP address (GDPR) — see
/// <see cref="TcgDexUserAgent"/>'s own remarks for the full context. Covers both request-building
/// paths independently: REST and GraphQL construct their <see cref="HttpRequestMessage"/> in two
/// different places, so a header applied to one is not evidence it reaches the other.
/// </remarks>
[TestFixture]
public sealed class UserAgentTests
{
    /// <summary>
    /// The exact value every request should carry, computed the same way the header itself is —
    /// from the assembly's own version, not a string this test invents independently. What this
    /// proves is narrower than it looks: that the header is present and correctly formed. It
    /// would not catch the header being built from the WRONG assembly's version, since both sides
    /// read the same one.
    /// </summary>
    private static readonly string s_expectedHeader = BuildExpectedHeader();

    private static string BuildExpectedHeader()
    {
        Version? version = typeof(TcgDexClient).Assembly.GetName().Version;
        string productVersion = version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";

        return $"TcgDex.CSharpSdk/{productVersion}";
    }

    private static TcgDexClient CreateClient(RecordingHandler handler)
        => new(new HttpClient(handler), new TcgDexOptions());

    [Test]
    public async Task RestRequest_CarriesTheUserAgent()
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.NotFound, "{}");

        await CreateClient(handler).Cards.GetAsync("swsh3-136", CancellationToken.None);

        handler.Requests[0].Headers.UserAgent.ToString().ShouldBe(s_expectedHeader);
    }

    [Test]
    public async Task GraphQlRequest_CarriesTheUserAgent()
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, """{"data":{"cards":[]}}""");

        await CreateClient(handler).Cards.SearchDetailedAsync(
            new CardFilter { Name = "Furret" }, cancellationToken: CancellationToken.None);

        handler.Requests[0].Headers.UserAgent.ToString().ShouldBe(s_expectedHeader);
    }
}

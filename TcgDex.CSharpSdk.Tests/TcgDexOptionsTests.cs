namespace TcgDex.Tests;

using TcgDex;

/// <summary>
/// Validation of the endpoint configuration.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TcgDexOptions.BaseAddress"/> is the only way to point this SDK at
/// a server other than the official one, so its validation is the whole safety
/// net for that feature. It used to share the net with the failover endpoint
/// list, which carried a trailing-slash guard of its own; when failover was
/// removed in 0.5.0 that guard went with it, and the identical hazard on
/// <c>BaseAddress</c> turned out never to have been checked or tested.
/// </para>
/// </remarks>
[TestFixture]
public sealed class TcgDexOptionsTests
{
    [Test]
    public void BaseAddress_WithoutATrailingSlash_Throws()
    {
        // Not cosmetic. Request paths are resolved RELATIVE to this address, and
        // without the slash the last segment is treated as a file name and
        // replaced: 'https://mine/v2' + 'en/cards/x' resolves to
        // 'https://mine/en/cards/x', silently dropping '/v2'. The server then
        // answers 404, the SDK maps 404 to null, and the caller is told a card
        // that exists does not.
        TcgDexOptions options = new()
        {
            BaseAddress = new Uri("https://tcgdex.example.dev/v2"),
        };

        ArgumentException error = Should.Throw<ArgumentException>(options.Validate);

        // The message has to name the fix, because the symptom a caller sees is
        // "no cards found" — nothing that points at a URI at all.
        error.Message.ShouldContain("must end with '/'");
    }

    [Test]
    public void BaseAddress_WithATrailingSlash_IsAccepted()
    {
        // The control. Without it the guard above would pass against a build that
        // rejected every custom base address, which would break the one capability
        // this validation exists to protect.
        TcgDexOptions options = new()
        {
            BaseAddress = new Uri("https://tcgdex.example.dev/v2/"),
        };

        Should.NotThrow(options.Validate);
    }

    [Test]
    public void BaseAddress_ThatIsRelative_Throws()
    {
        TcgDexOptions options = new()
        {
            BaseAddress = new Uri("/v2/", UriKind.Relative),
        };

        Should.Throw<ArgumentException>(options.Validate)
            .Message.ShouldContain("absolute URI");
    }

    [Test]
    public void TheDefaultBaseAddress_IsTheOfficialHost()
    {
        // api.tcgdex.net rather than a prefixed node: TCGdex retired the
        // per-node hostnames when the new infrastructure was deployed, and the
        // main route now routes around a node that is down.
        TcgDexOptions options = new();

        options.BaseAddress.ToString().ShouldBe("https://api.tcgdex.net/v2/");
        options.GraphQlEndpoint.ToString().ShouldBe("https://api.tcgdex.net/v2/graphql");
    }
}

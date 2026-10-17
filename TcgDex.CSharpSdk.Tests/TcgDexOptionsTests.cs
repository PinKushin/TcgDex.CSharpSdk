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
        error.Message.ShouldContain(
            "Without it the last path segment is replaced rather than appended to, " +
            "so requests would silently lose the API root.");
    }

    // The next three pin the whole message, not a keyword: each names the value it
    // rejected and the setting that removes the limit, and that is the part a caller
    // reading the exception needs.

    [Test]
    public void Timeout_OfZero_Throws_AndNamesTheInfiniteAlternative()
        => Should.Throw<ArgumentException>(new TcgDexOptions { Timeout = TimeSpan.Zero }.Validate)
            .Message.ShouldContain(
                "Timeout must be positive, but was 00:00:00. Use Timeout.InfiniteTimeSpan to remove the limit.");

    [Test]
    public void MaxDeserializedCacheEntries_Negative_Throws_AndNamesZero()
        => Should.Throw<ArgumentException>(new TcgDexOptions { MaxDeserializedCacheEntries = -1 }.Validate)
            .Message.ShouldContain(
                "MaxDeserializedCacheEntries cannot be negative, but was -1. Use zero to disable the cache.");

    [Test]
    public void Language_Unsupported_Throws_AndListsTheLanguagesCommaSeparated()
    {
        string message = Should.Throw<ArgumentException>(new TcgDexOptions { Language = "xx" }.Validate).Message;

        message.ShouldContain("Language 'xx' is not supported by the TCGdex API.");
        message.ShouldContain($"Supported languages are: {string.Join(", ", TcgDexLanguages.All)}.");
        message.ShouldContain("en, fr", Case.Sensitive, "the list must be readable, not run together");
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
        options.GraphQLEndpoint.ToString().ShouldBe("https://api.tcgdex.net/v2/graphql");
    }

    [Test]
    public void GraphQLEndpoint_ThatIsRelative_Throws()
    {
        // Unchecked, a relative endpoint escaped as an InvalidOperationException
        // from Uri.Scheme when the client was built, rather than the
        // ArgumentException naming the property that Validate promises.
        TcgDexOptions options = new()
        {
            GraphQLEndpoint = new Uri("graphql", UriKind.Relative),
        };

        ArgumentException error = Should.Throw<ArgumentException>(options.Validate);

        error.ParamName.ShouldBe(nameof(TcgDexOptions.GraphQLEndpoint));
        error.Message.ShouldContain("absolute URI");
    }
}

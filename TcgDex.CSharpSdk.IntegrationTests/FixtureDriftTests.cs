namespace TcgDex.IntegrationTests;

using System.IO;
using System.Text.Json;

/// <summary>
/// Checks that the recorded fixtures still match the live API.
/// </summary>
/// <remarks>
/// <para>
/// The offline suite — every unit test in this repository — is written against
/// recordings in <c>TcgDex.CSharpSdk.Tests/Fixtures</c>. Those tests prove the
/// SDK is correct <em>against the recording</em>. They cannot notice when TCGdex
/// changes, because a frozen snapshot never disagrees with itself.
/// </para>
/// <para>
/// This is the check that closes that gap. It re-fetches each fixture and
/// compares the response shape, so a removed or retyped field fails here with a
/// precise message rather than silently invalidating hundreds of offline
/// assertions.
/// </para>
/// <para>
/// Shape only: prices and <c>updated</c> timestamps change constantly, and a
/// check that fails every day teaches everyone to ignore it.
/// </para>
/// </remarks>
[TestFixture]
public sealed class FixtureDriftTests : LiveApiFixture
{
    private static readonly Uri ApiRoot = new("https://api.tcgdex.net/v2/");

    /// <summary>
    /// Fixtures whose shape legitimately varies between fetches, with the reason.
    /// </summary>
    /// <remarks>
    /// Empty today. Anything added here needs a comment justifying it, because
    /// an exclusion is a place the offline suite stops being verified.
    /// </remarks>
    private static readonly Dictionary<string, string> Excluded = new(StringComparer.Ordinal);

    [TestCaseSource(nameof(FixtureCases))]
    public async Task RecordedFixture_StillMatchesTheLiveApi(string fixture, string source)
    {
        if (Excluded.TryGetValue(fixture, out var reason))
        {
            Assert.Ignore($"'{fixture}' is excluded: {reason}");
        }

        var recordedJson = await File.ReadAllTextAsync(FixturePath(fixture), Timeout);

        using var httpClient = new HttpClient();
        using var response = await httpClient.GetAsync(new Uri(ApiRoot, source), Timeout);

        var liveJson = await response.Content.ReadAsStringAsync(Timeout);

        var recorded = JsonShape.Describe(recordedJson);
        var live = JsonShape.Describe(liveJson);

        var (breaking, additive) = JsonShape.Compare(recorded, live);

        // Additive changes are reported but do not fail: the API gaining a field
        // is worth knowing without breaking a build over it.
        if (additive.Count > 0)
        {
            TestContext.Out.WriteLine(JsonShape.Report(fixture, source, additive));
        }

        breaking.ShouldBeEmpty(JsonShape.Report(fixture, source, breaking));
    }

    [Test]
    public void EveryFixtureIsListedInTheManifest()
    {
        // A fixture with no manifest entry is never drift-checked, which is the
        // failure mode this whole file exists to prevent.
        var onDisk = Directory
            .EnumerateFiles(FixtureDirectory, "*.json")
            .Select(Path.GetFileName)
            .Where(name => name is not null && name != "manifest.json")
            .ToList();

        var listed = LoadManifest().Keys;

        onDisk.ShouldAllBe(name => listed.Contains(name!));
    }

    [Test]
    public void EveryManifestEntryHasAFixture()
    {
        foreach (var fixture in LoadManifest().Keys)
        {
            File.Exists(FixturePath(fixture))
                .ShouldBeTrue($"'{fixture}' is in the manifest but not on disk");
        }
    }

    /// <summary>
    /// The fixtures live in the unit-test project, so the path is resolved
    /// relative to the repository root rather than to this project's output.
    /// </summary>
    private static string FixtureDirectory
    {
        get
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "TcgDex.CSharpSdk.Tests")))
            {
                directory = directory.Parent;
            }

            if (directory is null)
            {
                throw new DirectoryNotFoundException(
                    "Could not locate the repository root from the test output directory.");
            }

            return Path.Combine(directory.FullName, "TcgDex.CSharpSdk.Tests", "Fixtures");
        }
    }

    private static string FixturePath(string fixture) => Path.Combine(FixtureDirectory, fixture);

    private static Dictionary<string, string> LoadManifest()
    {
        var json = File.ReadAllText(Path.Combine(FixtureDirectory, "manifest.json"));

        using var document = JsonDocument.Parse(json);

        var fixtures = document.RootElement.GetProperty("fixtures");
        var manifest = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in fixtures.EnumerateObject())
        {
            manifest[entry.Name] = entry.Value.GetString()!;
        }

        return manifest;
    }

    private static IEnumerable<TestCaseData> FixtureCases()
        => LoadManifest().Select(entry =>
            new TestCaseData(entry.Key, entry.Value).SetName($"drift: {entry.Key}"));
}

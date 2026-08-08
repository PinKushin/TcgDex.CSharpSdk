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
        if (Excluded.TryGetValue(fixture, out string? reason))
        {
            Assert.Ignore($"'{fixture}' is excluded: {reason}");
        }

        string recordedJson = await File.ReadAllTextAsync(FixturePath(fixture), Timeout);

        using HttpClient httpClient = new();
        using HttpResponseMessage response = await httpClient.GetAsync(new Uri(ApiRoot, source), Timeout);

        string liveJson = await response.Content.ReadAsStringAsync(Timeout);

        IReadOnlyDictionary<string, string> recorded = JsonShape.Describe(recordedJson);
        IReadOnlyDictionary<string, string> live = JsonShape.Describe(liveJson);

        (IReadOnlyList<string>? breaking, IReadOnlyList<string>? additive) = JsonShape.Compare(recorded, live);

        // Breaking first, because a retyped field is the more urgent of the two
        // and its message should not be buried under a list of new ones.
        breaking.ShouldBeEmpty(JsonShape.Report(fixture, source, breaking));

        // Additive changes fail too. They used to be written to TestContext.Out
        // and nothing more, which made them invisible in practice: this fixture
        // only runs in the weekly scheduled job, so "reported" meant a line in
        // the stdout of a run that reported green and nobody opened.
        //
        // A field the API starts serving is exactly the drift worth acting on —
        // it is how `pricing`, `variants_detailed` and `updated` came to be
        // served by TCGdex for a long time while the official JS SDK's types
        // omitted all three. Silently logging that class of change is how an SDK
        // falls behind the API it wraps.
        //
        // Failing costs nothing here. These tests are gated to
        // `schedule || workflow_dispatch`, so no pull request is ever blocked by
        // this; the red run *is* the notification. To respond: model the new
        // field, then refresh the recording with scripts/Update-Fixtures.ps1 —
        // in that order, because refreshing first makes this pass whether or not
        // the model was updated.
        additive.ShouldBeEmpty(JsonShape.Report(fixture, source, additive));
    }

    [Test]
    public void EveryFixtureIsListedInTheManifest()
    {
        // A fixture with no manifest entry is never drift-checked, which is the
        // failure mode this whole file exists to prevent.
        List<string?> onDisk = Directory
            .EnumerateFiles(FixtureDirectory, "*.json")
            .Select(Path.GetFileName)
            .Where(name => name is not null && name != "manifest.json")
            .ToList();

        Dictionary<string, string>.KeyCollection listed = LoadManifest().Keys;

        onDisk.ShouldAllBe(name => listed.Contains(name!));
    }

    [Test]
    public void EveryManifestEntryHasAFixture()
    {
        foreach (string fixture in LoadManifest().Keys)
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
            DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);

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
        string json = File.ReadAllText(Path.Combine(FixtureDirectory, "manifest.json"));

        using JsonDocument document = JsonDocument.Parse(json);

        JsonElement fixtures = document.RootElement.GetProperty("fixtures");
        Dictionary<string, string> manifest = new(StringComparer.Ordinal);

        foreach (JsonProperty entry in fixtures.EnumerateObject())
        {
            manifest[entry.Name] = entry.Value.GetString()!;
        }

        return manifest;
    }

    private static IEnumerable<TestCaseData> FixtureCases()
        => LoadManifest().Select(entry =>
            new TestCaseData(entry.Key, entry.Value).SetName($"drift: {entry.Key}"));
}

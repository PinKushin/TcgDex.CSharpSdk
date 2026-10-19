namespace TcgDex.PackageSmokeTest;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TcgDex.Models;
using TcgDex.Querying;

/// <summary>
/// A consumer of the PACKED SDK. Public API only, because that is all a consumer has.
/// </summary>
/// <remarks>
/// <para>
/// Arguments: the package version and the git revision it was built from, both supplied by
/// <c>scripts/Test-Package.ps1</c>. The first check compares them with what actually loaded,
/// which is what makes the rest worth reading: a restore that quietly picked up nuget.org's
/// copy of the same version would otherwise pass every check below.
/// </para>
/// <para>
/// Offline. Responses come from a stub handler.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>A trimmed real response, embedded so the test needs no network.</summary>
    private const string CardJson = """
        {
          "category": "Pokemon",
          "id": "swsh3-136",
          "localId": "136",
          "name": "Furret",
          "hp": 110,
          "set": { "id": "swsh3", "name": "Darkness Ablaze" },
          "attacks": [
            { "cost": ["Colorless"], "name": "Feelin' Fine", "effect": "Draw 3 cards." },
            { "cost": ["Colorless","Colorless"], "name": "Tail Smash", "damage": 130 }
          ],
          "pricing": {
            "tcgplayer": { "unit": "USD", "normal": { "marketPrice": 0.12 } }
          }
        }
        """;

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine(
                $"usage: PackageSmokeTest <package version> <git revision>; got {args.Length} argument(s): {string.Join(" | ", args)}");
            return 2;
        }

        string version = args[0];
        string revision = args[1];
        List<string> failures = new();

        Console.WriteLine($"Runtime: {RuntimeEnvironmentName()}");

        Check(failures, "this is the package that was just packed", () => PackageIdentity(version, revision));
        Check(failures, "the asset for this target framework was loaded", LoadedAsset);
        Check(failures, "a card is read through the client", ReadACard);
        Check(failures, "missing is null; a failure is one exception type", ErrorContract);
        Check(failures, "a query is translated", QueryTranslation);
        Check(failures, "an image URL is built", ImageUrls);
        Check(failures, "unsupported options are rejected", OptionsValidation);
        Check(failures, "AddTcgDex registers a client", () => Registers(static services => services.AddTcgDex()));
        Check(failures, "AddTcgDexWithCaching registers a client", () => Registers(static services => services.AddTcgDexWithCaching()));

        if (failures.Count > 0)
        {
            Console.Error.WriteLine($"\nFAILED: {failures.Count} check(s) did not pass.");
            return 1;
        }

        Console.WriteLine("\nAll checks passed.");
        return 0;
    }

    private static string RuntimeEnvironmentName()
        => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

    private static void Check(List<string> failures, string name, Func<string?> check)
    {
        string? failure;

        try
        {
            failure = check();
        }
        catch (Exception ex)
        {
            // Reporting which check threw is the whole job here, not handling it.
            failure = $"threw {ex.GetType().Name}: {ex.Message}";
        }

        if (failure is null)
        {
            Console.WriteLine($"  ok    {name}");
        }
        else
        {
            Console.WriteLine($"  FAIL  {name}: {failure}");
            failures.Add(name);
        }
    }

    private static Assembly Sdk => typeof(TcgDexClient).Assembly;

    /// <summary>
    /// The loaded assembly is the one this run packed: same version, same commit.
    /// </summary>
    /// <remarks>
    /// nuget.org already holds a package with the version in the csproj, so a restore that is
    /// not pinned to the local feed would install that one and look healthy. Its commit is
    /// different, which is what this compares.
    /// </remarks>
    private static string? PackageIdentity(string version, string revision)
    {
        string? informational = Sdk.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (informational is null)
        {
            return "the assembly has no informational version";
        }

        int plus = informational.IndexOf('+');

        if (plus < 0)
        {
            return $"'{informational}' carries no source revision, so the build cannot be identified";
        }

        string loadedVersion = informational.Substring(0, plus);
        string loadedRevision = informational.Substring(plus + 1);

        if (!string.Equals(loadedVersion, version, StringComparison.Ordinal))
        {
            return $"expected version {version} but loaded {loadedVersion}";
        }

        // One may be an abbreviation of the other, depending on the SDK that built it.
        bool sameRevision = loadedRevision.Length > 0
            && (revision.StartsWith(loadedRevision, StringComparison.OrdinalIgnoreCase)
                || loadedRevision.StartsWith(revision, StringComparison.OrdinalIgnoreCase));

        return sameRevision
            ? null
            : $"expected revision {revision} but loaded {loadedRevision}: this is a different build of {version}";
    }

    /// <summary>
    /// net472 must run the netstandard2.0 asset; each modern target must run its own, not
    /// the netstandard one it could have fallen back to.
    /// </summary>
    private static string? LoadedAsset()
    {
#if NET10_0
        const string Expected = ".NETCoreApp,Version=v10.0";
#elif NET8_0
        const string Expected = ".NETCoreApp,Version=v8.0";
#elif NET472
        const string Expected = ".NETStandard,Version=v2.0";
#else
#error No expected asset is defined for this target framework.
#endif
        string? actual = Sdk.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

        return actual == Expected ? null : $"expected the {Expected} asset but loaded {actual}";
    }

    private static HttpClient Stub(HttpStatusCode status, string body)
        => new(new StubHandler(status, body));

    private static string? ReadACard()
    {
        using HttpClient http = Stub(HttpStatusCode.OK, CardJson);
        using TcgDexClient client = new(http, new TcgDexOptions());

        Card? card = client.Cards.GetAsync("swsh3-136", CancellationToken.None).GetAwaiter().GetResult();

        if (card is null)
        {
            return "the card came back null";
        }

        if (card.Name != "Furret" || card.HitPoints != 110 || card.Set.Name != "Darkness Ablaze")
        {
            return $"unexpected card: {card.Name}, {card.HitPoints} HP, set '{card.Set.Name}'";
        }

        // The converter for a damage that is a number on one card and text on another.
        if (card.Attacks.Count != 2 || card.Attacks[1].BaseDamage != 130)
        {
            return $"attack damage was not read: {card.Attacks.Count} attacks";
        }

        return card.Pricing?.TcgPlayer is null ? "pricing was not read" : null;
    }

    private static string? ErrorContract()
    {
        using (HttpClient http = Stub(HttpStatusCode.NotFound, string.Empty))
        using (TcgDexClient client = new(http, new TcgDexOptions()))
        {
            Card? missing = client.Cards.GetAsync("nope-1", CancellationToken.None).GetAwaiter().GetResult();

            if (missing is not null)
            {
                return "a 404 did not return null";
            }
        }

        using (HttpClient http = Stub(HttpStatusCode.InternalServerError, string.Empty))
        using (TcgDexClient client = new(http, new TcgDexOptions()))
        {
            try
            {
                client.Cards.GetAsync("swsh3-136", CancellationToken.None).GetAwaiter().GetResult();
                return "a 500 did not throw";
            }
            catch (TcgDexApiException ex)
            {
                return ex.Kind == TcgDexErrorKind.HttpStatus && ex.StatusCode == HttpStatusCode.InternalServerError
                    ? null
                    : $"a 500 threw kind {ex.Kind} with status {ex.StatusCode}";
            }
        }
    }

    private static string? QueryTranslation()
    {
        int minimumHp = 250;

        string actual = new CardQuery()
            .Where(c => c.Name == "Furret")
            .Where(c => c.HitPoints > minimumHp)
            .OrderByDescending(c => c.Name)
            .Page(2, 50)
            .ToQueryString();

        const string Expected =
            "name=eq:Furret&hp=gt:250&sort:field=name&sort:order=DESC&pagination:page=2&pagination:itemsPerPage=50";

        return actual == Expected ? null : $"got '{actual}'";
    }

    private static string? ImageUrls()
    {
        Uri? url = ImageUrl.Build("https://assets.tcgdex.net/en/swsh/swsh3/136");

        return url?.AbsoluteUri == "https://assets.tcgdex.net/en/swsh/swsh3/136/high.png"
            ? null
            : $"got '{url}'";
    }

    private static string? OptionsValidation()
    {
        try
        {
            new TcgDexOptions { Language = "zz" }.Validate();
            return "an unsupported language was accepted";
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? Registers(Func<IServiceCollection, IHttpClientBuilder> register)
    {
        ServiceCollection services = new();
        _ = register(services);

        using ServiceProvider provider = services.BuildServiceProvider();

        ITcgDexClient? client = provider.GetService<ITcgDexClient>();

        return client is null ? "ITcgDexClient did not resolve" : null;
    }

    /// <summary>Answers every request with one canned response.</summary>
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}

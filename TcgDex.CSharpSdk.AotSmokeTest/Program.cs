namespace TcgDex.AotSmokeTest;

using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using TcgDex.Models;
using TcgDex.Querying;
using TcgDex.Serialization;

/// <summary>
/// Exercises the SDK's reflection-sensitive paths under Native AOT.
/// </summary>
/// <remarks>
/// <para>
/// The two things that break under AOT are reflection-based serialization and
/// runtime code generation. This app drives both paths — source-generated JSON
/// deserialization, and expression-tree translation with a captured variable —
/// so that if either regresses, the published binary fails rather than the
/// problem surfacing in a consumer's application.
/// </para>
/// <para>
/// Runs offline against embedded JSON so it is deterministic and safe in CI.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>A trimmed-down real response, embedded so the test needs no network or content files.</summary>
    private const string CardJson = """
        {
          "category": "Pokemon",
          "id": "swsh3-136",
          "illustrator": "tetsuya koizumi",
          "localId": "136",
          "name": "Furret",
          "rarity": "Uncommon",
          "hp": 110,
          "types": ["Colorless"],
          "stage": "Stage1",
          "set": { "id": "swsh3", "name": "Darkness Ablaze",
                   "cardCount": { "official": 189, "total": 201 } },
          "attacks": [
            { "cost": ["Colorless"], "name": "Feelin' Fine", "effect": "Draw 3 cards." },
            { "cost": ["Colorless","Colorless"], "name": "Tail Smash", "damage": 130 }
          ],
          "weaknesses": [{ "type": "Fighting", "value": "×2" }],
          "variants": { "normal": true, "reverse": true, "holo": false,
                        "firstEdition": false, "wPromo": false },
          "legal": { "standard": false, "expanded": true },
          "pricing": {
            "cardmarket": { "unit": "EUR", "updated": "2026-08-06T00:00:00Z", "avg": 0.11 },
            "tcgplayer": { "unit": "USD", "updated": "2026-08-06T00:00:00Z",
                           "normal": { "marketPrice": 0.12, "lowPrice": 0.02 } }
          }
        }
        """;

    private static int Main()
    {
        var failures = new List<string>();

        Check(failures, "source-generated deserialization", SourceGeneratedDeserialization);
        Check(failures, "polymorphic damage converter", PolymorphicDamageConverter);
        Check(failures, "collection guards", CollectionGuards);
        Check(failures, "expression-tree query translation", ExpressionTreeTranslation);
        Check(failures, "captured variable without Expression.Compile", CapturedVariable);
        Check(failures, "options validation", OptionsValidation);
        Check(failures, "contract modifier for optional pricing", PricingContractModifier);

        if (failures.Count > 0)
        {
            Console.Error.WriteLine($"\nFAILED — {failures.Count} check(s) did not pass under Native AOT.");
            return 1;
        }

        Console.WriteLine("\nAll checks passed under Native AOT.");
        return 0;
    }

    private static void Check(List<string> failures, string name, Func<string?> check)
    {
        string? failure;

        try
        {
            failure = check();
        }
        catch (Exception ex)
        {
            // A bare catch is right here: the point of the smoke test is to
            // report which AOT-sensitive path threw, not to handle it.
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

    private static Card DeserializeCard()
    {
        var typeInfo = (JsonTypeInfo<Card>)TcgDexJsonContext.Default.Options.GetTypeInfo(typeof(Card));
        return JsonSerializer.Deserialize(CardJson, typeInfo)
            ?? throw new InvalidOperationException("card deserialized to null");
    }

    private static string? SourceGeneratedDeserialization()
    {
        var card = DeserializeCard();

        if (card.Name != "Furret")
        {
            return $"expected name 'Furret' but got '{card.Name}'";
        }

        return card.Set.Name == "Darkness Ablaze"
            ? null
            : $"nested set did not deserialize: '{card.Set.Name}'";
    }

    /// <summary>
    /// <see cref="TcgDexOptions.DeserializePricing"/> off, which is the only
    /// place the SDK customises a source-generated contract at runtime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>JsonTypeInfoResolver.WithAddedModifier</c> is documented as AOT-safe,
    /// and "documented as safe" is the same class of claim as "source generation
    /// is faster on every call" — which this project already had to retract
    /// after measuring it. The modifier walks a generated contract and swaps a
    /// property's converter; if trimming ever removed the metadata that depends
    /// on, this is where it would surface.
    /// </para>
    /// <para>
    /// Both directions are checked. A modifier that silently did nothing would
    /// pass a test that only looked at the default.
    /// </para>
    /// </remarks>
    private static string? PricingContractModifier()
    {
        // Driven through the public client rather than the internal contract
        // factory, so this covers the path a consumer actually takes — and, as a
        // side effect, exercises the deserialization cache under AOT too.
        var kept = Fetch(new TcgDexOptions());
        var skipped = Fetch(new TcgDexOptions { DeserializePricing = false });

        if (kept?.Pricing is null)
        {
            return "pricing was not populated by default";
        }

        if (skipped is null)
        {
            return "the card did not deserialize with DeserializePricing off";
        }

        if (skipped.Pricing is not null)
        {
            return "pricing was populated even with DeserializePricing off";
        }

        // The rest of the card must survive having one property re-converted.
        return skipped.Name == "Furret"
            ? null
            : $"the modified contract broke the rest of the card: '{skipped.Name}'";
    }

    /// <summary>Fetches the embedded card through a client with a stub transport.</summary>
    private static Card? Fetch(TcgDexOptions options)
    {
        using var http = new System.Net.Http.HttpClient(new StubHandler(CardJson));
        using var client = new TcgDexClient(http, options);

        return client.Cards.GetAsync("swsh3-136", CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>Answers every request with the embedded card, tagged so the cache engages.</summary>
    private sealed class StubHandler(string body) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(
                    body,
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };

            response.Headers.TryAddWithoutValidation("ETag", "W/\"aot\"");

            return Task.FromResult(response);
        }
    }

    private static string? PolymorphicDamageConverter()
    {
        var card = DeserializeCard();
        var attack = card.Attacks.Count > 1 ? card.Attacks[1] : null;

        if (attack?.Damage != "130")
        {
            return $"expected numeric damage normalised to \"130\" but got '{attack?.Damage}'";
        }

        return attack.BaseDamage == 130 ? null : $"BaseDamage was {attack.BaseDamage}";
    }

    private static string? CollectionGuards()
    {
        var card = DeserializeCard();

        // `abilities` is absent from the JSON above. Source-generated
        // deserialization discards property initializers, so this is null
        // unless the model guards it.
        return card.Abilities is null
            ? "Abilities was null for an absent JSON property"
            : null;
    }

    private static string? ExpressionTreeTranslation()
    {
        var actual = new CardQuery()
            .Where(c => c.Name == "Furret")
            .Where(c => c.Hp > 100)
            .OrderByDescending(c => c.Name)
            .Page(2, 50)
            .ToQueryString();

        const string Expected =
            "name=eq:Furret&hp=gt:100&sort:field=name&sort:order=DESC" +
            "&pagination:page=2&pagination:itemsPerPage=50";

        return actual == Expected ? null : $"got '{actual}'";
    }

    private static string? CapturedVariable()
    {
        // The captured local lands in a compiler-generated closure. Resolving it
        // by compiling the expression would fail under AOT; the SDK reads the
        // closure field reflectively instead.
        var minimumHp = 250;

        var actual = new CardQuery().Where(c => c.Hp > minimumHp).ToQueryString();

        return actual == "hp=gt:250" ? null : $"got '{actual}'";
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
}

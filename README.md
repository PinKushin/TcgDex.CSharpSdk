# TcgDex.CSharpSdk

A .NET SDK for the [TCGdex](https://tcgdex.dev) Pokémon TCG API — strongly typed
models, a fluent query builder over the full REST filter syntax, and first-class
support for dependency injection, trimming and Native AOT.

[![CI](https://github.com/PinKushin/TcgDex.CSharpSdk/actions/workflows/ci.yml/badge.svg)](https://github.com/PinKushin/TcgDex.CSharpSdk/actions/workflows/ci.yml)

Targets **.NET 8**, **.NET 10** and **netstandard2.0** — so it runs on modern
.NET, on **Unity**, and on **.NET Framework 4.6.1+**. No API key required —
TCGdex is a free, public, read-only API.

The API surface is identical on every target, async included. One difference is
worth knowing before you rely on it: connection recycling, which keeps a
long-lived client from pinning stale DNS, uses `SocketsHttpHandler` on modern
.NET and `ServicePoint.ConnectionLeaseTimeout` on .NET Framework. Same
guarantee, different mechanism. Response cancellation mid-body is best-effort on
netstandard2.0, because the cancellable `HttpContent` read overloads do not
exist there.

---

## Install

```bash
dotnet add package TcgDex.CSharpSdk
```

## Quick start

With dependency injection, which wires the client through `IHttpClientFactory`:

```csharp
builder.Services.AddTcgDex();
```

```csharp
public sealed class CardLookup(ITcgDexClient tcgdex)
{
    public async Task<string?> DescribeAsync(string id, CancellationToken ct)
    {
        var card = await tcgdex.Cards.GetAsync(id, ct);

        return card is null ? null : $"{card.Name} ({card.Category}) — {card.Rarity}";
    }
}
```

Without a container:

```csharp
using var http = new HttpClient();
var tcgdex = new TcgDexClient(http, new TcgDexOptions());

var card = await tcgdex.Cards.GetAsync("swsh3-136", cancellationToken);
Console.WriteLine(card?.Name);   // Furret
```

## Querying

Predicates are written in C# and translated to the API's filter syntax:

```csharp
var query = new CardQuery()
    .Where(c => c.Name.Contains("Pikachu"))
    .Where(c => c.Hp > 100)
    .OrderByDescending(c => c.Name)
    .Page(1, 50);

var cards = await tcgdex.Cards.ListAsync(query, cancellationToken);
```

which becomes:

```
cards?name=Pikachu&hp=gt:100&sort:field=name&sort:order=DESC&pagination:page=1&pagination:itemsPerPage=50
```

Supported translations, covering every operator the API has:

| C# | Query syntax |
|---|---|
| `c.Name == "Furret"` | `name=eq:Furret` |
| `c.Name != "Furret"` | `name=neq:Furret` |
| `c.Hp > 100` / `>=` / `<` / `<=` | `hp=gt:100` / `gte:` / `lt:` / `lte:` |
| `c.Name.Contains("pika")` | `name=pika` |
| `c.Name.StartsWith("fu")` | `name=fu*` |
| `c.Name.EndsWith("chu")` | `name=*chu` |
| `!c.Name.Contains("pika")` | `name=not:pika` |
| `c.Effect == null` | `effect=null:` |
| `c.Effect != null` | `effect=notnull:` |
| `a && b` | separate parameters |
| `c.Name == "a" \|\| c.Name == "b"` | `name=eq:a\|b` |

Two limits are inherited from the API rather than chosen here:

- **`||` works only within a single field.** An OR across two fields has no
  encoding, so it throws rather than silently dropping half your predicate.
- **No total count exists.** The API sends no count and no pagination headers,
  so page until you get back fewer items than you asked for.

Anything the API cannot express is rejected with a message naming the offending
expression — never approximated into a filter that would quietly return the
wrong cards.

### Full card detail in one request

`ListAsync` returns briefs, so getting full detail for a result set costs one
call per card. When you need the detail, `SearchDetailedAsync` fetches it in a
single request over GraphQL:

```csharp
var cards = await tcgdex.Cards.SearchDetailedAsync(
    new CardFilter { Name = "Furret" },
    cancellationToken: ct);

// 12 fully populated cards — hp, types, attacks, weaknesses, set — in one hop.
// The REST equivalent is 13 round trips.
```

Three limits come with it, all imposed by the TCGdex GraphQL endpoint rather
than by this SDK:

| | REST | `SearchDetailedAsync` |
|---|---|---|
| Languages | all 18 | **English only** |
| Filters | all ten operators | **equality only** |
| `Pricing` | populated | **never populated** |

So reach for it when you want breadth cheaply, and stay on REST when you need
a language, a range filter, or prices.

### Caching

The API sends `no-store`, but it honours `If-None-Match`. Enabling the cache
means fresh reads skip the network entirely, and stale reads are *revalidated* —
an unchanged 22 KB set response costs a `304` and **0 bytes** instead of a
re-download.

```csharp
builder.Services.AddTcgDexWithCaching();
```

Errors are never cached, non-`GET` requests bypass it, and concurrent reads of
the same URL are collapsed into a single request. See
[Caching](https://pinkushin.github.io/TcgDex.CSharpSdk/caching.html).

## What you can read

```csharp
Card?  card  = await tcgdex.Cards.GetAsync("swsh3-136", ct);
Set?   set   = await tcgdex.Sets.GetAsync("swsh3", ct);      // includes its cards
Serie? serie = await tcgdex.Series.GetAsync("swsh", ct);     // includes its sets
Card   lucky = await tcgdex.Random.CardAsync(ct);

// Distinct values, useful for building filters and pickers
IReadOnlyList<string> rarities = await tcgdex.Catalog.RaritiesAsync(ct);
IReadOnlyList<int>    hpValues = await tcgdex.Catalog.HitPointsAsync(ct);
```

`Catalog` covers all thirteen enumeration endpoints: categories, rarities,
types, illustrators, stages, suffixes, variants, energy types, regulation marks,
trainer types, HP, retreat costs and dex ids.

## Languages

18 are supported. Set one at registration:

```csharp
builder.Services.AddTcgDex(options => options.Language = TcgDexLanguages.French);
```

An unsupported code fails at registration with a message listing the valid set,
rather than surfacing later as a 404 that looks like a missing card.

## Error handling

One rule, applied everywhere:

- **A missing resource returns `null`.** Asking for a card that does not exist
  is a normal outcome, not an exception.
- **Everything else throws `TcgDexApiException`** — server errors, unsupported
  languages, timeouts and unparseable bodies alike, so you catch one type rather
  than four.

```csharp
try
{
    var card = await tcgdex.Cards.GetAsync(id, ct);
    if (card is null) { /* no such card */ }
}
catch (TcgDexApiException ex)
{
    logger.LogError(ex, "TCGdex failed with {Status}", ex.StatusCode);
}
```

Worth knowing: the API returns **404 for an unsupported language too**, so the
status code alone cannot distinguish that from a missing card. The SDK
discriminates on the error body and exposes `ex.IsLanguageError`.

## Trimming and Native AOT

The SDK is trim- and AOT-safe: serialization is source-generated, and the query
builder walks expression trees rather than calling `Expression.Compile()`, which
would emit IL at runtime.

This is verified, not asserted — CI publishes a Native AOT binary and runs it on
every push. See `TcgDex.CSharpSdk.AotSmokeTest`.

## Cards are modelled as the API returns them

Fields are populated by category: Pokémon carry `Hp`, `Types`, `Attacks` and
`Weaknesses`; Trainers carry `TrainerType` and `Effect`; Energy cards carry
`EnergyType`. Anything category-specific is nullable because the API omits it
entirely rather than sending null.

Collections are **never null** — an absent array arrives as empty, so iterating
a Trainer's `Attacks` is safe.

A few shapes are irregular and the SDK smooths them over:

- `Attack.Damage` is text, because the API sends `60` on one card and `"50+"` on
  another. `Attack.BaseDamage` gives you the numeric part.
- `WeaknessOrResistance.Value` is text — values include `"×2"` and `"-20"`.
- TCGplayer prices are keyed by printing name, and the names vary per card, so
  they are exposed as a dictionary: `card.Pricing?.Tcgplayer?["holofoil"]`.

## Images

`Image`, `Logo` and `Symbol` are base URLs **without a file extension**. The
helpers build the right form for each:

```csharp
string? art  = card.GetImageUrl(ImageQuality.High, ImageFormat.Png);
string? logo = card.Set.GetLogoUrl();
string? sym  = card.Set.GetSymbolUrl(ImageFormat.Webp);
```

They are separate methods because **card artwork takes a quality segment and set
assets do not**:

```
https://assets.tcgdex.net/en/swsh/swsh3/136/high.png   card    200
https://assets.tcgdex.net/en/swsh/swsh3/logo.png       logo    200
https://assets.tcgdex.net/en/swsh/swsh3/logo/high.png  logo    404
```

The three fields look alike on the model, so applying the card pattern to a logo
is an easy mistake — and returns 404.

Each helper returns `null` rather than a broken URL when the asset is absent;
some cards genuinely have no artwork.

### Logging and tracing

Structured logs via `ILogger` and OpenTelemetry spans via `ActivitySource`, both
free when nothing is listening. With DI it is automatic; everything is under the
`TcgDex` category.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(TcgDexActivity.SourceName));
```

The SDK takes **no dependency on any telemetry vendor** — it writes to .NET's
standard abstractions, so Serilog, Application Insights, Datadog, Sentry or
anything else picks it up by configuring that tool in your application. See
[Logging and tracing](https://pinkushin.github.io/TcgDex.CSharpSdk/observability.html).

## Documentation

**[Full documentation and API reference](https://pinkushin.github.io/TcgDex.CSharpSdk/)**

- [Getting started](https://pinkushin.github.io/TcgDex.CSharpSdk/getting-started.html)
- [Querying](https://pinkushin.github.io/TcgDex.CSharpSdk/querying.html)
- [API reference](https://pinkushin.github.io/TcgDex.CSharpSdk/api/)

In this repository:

- [`docs/api-info.md`](https://github.com/PinKushin/TcgDex.CSharpSdk/blob/main/docs/api-info.md)
  — the API reference this SDK is built against, verified field by field against
  live responses.
- [`docs/learnings.md`](https://github.com/PinKushin/TcgDex.CSharpSdk/blob/main/docs/learnings.md)
  — non-obvious behaviour discovered while building it.

## Security

Found a vulnerability? **Report it privately** through
[GitHub Security Advisories](https://github.com/PinKushin/TcgDex.CSharpSdk/security/advisories/new)
rather than opening an issue — see [`SECURITY.md`](SECURITY.md), which also sets
out what this library actually exposes and what is not in scope.

## Contributing

```bash
dotnet build -warnaserror          # zero warnings is enforced
dotnet test TcgDex.CSharpSdk.Tests # unit tests, offline
dotnet test TcgDex.CSharpSdk.IntegrationTests --filter "TestCategory=Integration"
```

Coverage is gated in CI and the same check runs locally. **Run this before
pushing** — it is the gate most easily forgotten, because everything else is
green by the time you get here:

```bash
dotnet test TcgDex.CSharpSdk.Tests/TcgDex.CSharpSdk.Tests.csproj   --collect:"XPlat Code Coverage" --settings coverlet.runsettings   --results-directory ./TestResults
pwsh ./scripts/Check-Coverage.ps1
```

`--settings coverlet.runsettings` is not optional. Without it the
source-generated files swamp the hand-written ones and the number means nothing
— 1,455 lines counted against the real 986.

Two further gates that are not part of a normal push:

- **The public API is pinned.** Adding, removing or changing anything public
  fails `PublicApiTests` until the diff is accepted into
  `TcgDex.CSharpSdk.Tests/PublicApi.approved.cs`. That commit is the record of
  what consumers are promised.
- **Mutation testing and fuzzing** run periodically rather than per push. See
  [`docs/measuring.md`](https://github.com/PinKushin/TcgDex.CSharpSdk/blob/main/docs/measuring.md)
  for both, including how to run the fuzzer locally under WSL.

Unit tests run against recorded API responses in
`TcgDex.CSharpSdk.Tests/Fixtures`, so they need no network. Integration tests
hit the live API and run weekly in CI rather than per push, so a TCGdex outage
does not redden a pull request.

## License

MIT — see [LICENSE.txt](https://github.com/PinKushin/TcgDex.CSharpSdk/blob/main/LICENSE.txt).

This is an unofficial, community-maintained SDK. It is not affiliated with
TCGdex, Nintendo, Creatures Inc., GAME FREAK inc. or The Pokémon Company.
Pokémon and all related names are trademarks of their respective owners.

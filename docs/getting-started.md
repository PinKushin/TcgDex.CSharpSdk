# Getting started

## Install

```bash
dotnet add package TcgDex.CSharpSdk
```

Targets .NET 8 and .NET 10. TCGdex is a free, public, read-only API — there is no
key to configure and no account to create.

## With dependency injection

`AddTcgDex` registers the client through `IHttpClientFactory`, which manages
handler lifetime and connection pooling for you:

```csharp
builder.Services.AddTcgDex();
```

Then inject `ITcgDexClient` anywhere:

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

`AddTcgDex` returns the `IHttpClientBuilder`, so you can attach your own handlers
or resilience policies:

```csharp
builder.Services
    .AddTcgDex(options => options.Language = TcgDexLanguages.French)
    .AddStandardResilienceHandler();
```

## Without a container

```csharp
using var http = new HttpClient();
var tcgdex = new TcgDexClient(http, new TcgDexOptions());

var card = await tcgdex.Cards.GetAsync("swsh3-136", cancellationToken);
```

Reuse the `HttpClient`. Constructing one per call is the usual cause of socket
exhaustion.

## What you can read

```csharp
Card?  card  = await tcgdex.Cards.GetAsync("swsh3-136", ct);
Set?   set   = await tcgdex.Sets.GetAsync("swsh3", ct);      // includes its cards
Serie? serie = await tcgdex.Series.GetAsync("swsh", ct);     // includes its sets
Card   lucky = await tcgdex.Random.CardAsync(ct);

IReadOnlyList<string> rarities = await tcgdex.Catalog.RaritiesAsync(ct);
IReadOnlyList<int>    hpValues = await tcgdex.Catalog.HitPointsAsync(ct);
```

`Catalog` covers all thirteen enumeration endpoints — categories, rarities,
types, illustrators, stages, suffixes, variants, energy types, regulation marks,
trainer types, HP, retreat costs and dex ids. They are the practical way to build
valid filters and populate pickers.

## Languages

Eighteen are accepted. Set one at registration:

```csharp
builder.Services.AddTcgDex(options => options.Language = TcgDexLanguages.German);
```

An unsupported code throws at registration with a message listing the valid set,
rather than surfacing later as a 404 that looks like a missing card.

Two things to know, both properties of the API rather than the SDK:

- **Four accepted languages have no card data.** `pt-pt`, `nl`, `pl` and `ru`
  return empty results rather than errors.
- **Card ids are not universal.** Each language has its own card pool, so
  `swsh3-136` exists in `en`/`fr`/`de` but 404s in `ja`, `ko`, `th`, `id`,
  `zh-cn` and `pt-br`. Take ids from the list endpoint of the language you are
  working in.

## Handling errors

One rule:

- **A missing resource returns `null`.** Asking for a card that does not exist is
  a normal outcome, not an exception.
- **Everything else throws `TcgDexApiException`** — server errors, unsupported
  languages, timeouts and unparseable bodies alike.

```csharp
try
{
    var card = await tcgdex.Cards.GetAsync(id, ct);

    if (card is null)
    {
        // No such card.
    }
}
catch (TcgDexApiException ex)
{
    logger.LogError(ex, "TCGdex failed with {Status}", ex.StatusCode);
}
```

The API returns **404 for an unsupported language too**, so a status code alone
cannot tell that apart from a missing card. The SDK discriminates on the error
body and exposes `ex.IsLanguageError`.

## Images

`Image`, `Logo` and `Symbol` are base URLs **without a file extension**. Append a
quality and format:

```csharp
var url = $"{card.Image}/high.png";   // or low.webp, high.jpg, ...
```

Some cards genuinely have no artwork, so `Image` can be null.

## Reading the models

Fields are populated by category: Pokémon carry `Hp`, `Types`, `Attacks` and
`Weaknesses`; Trainers carry `TrainerType` and `Effect`; Energy cards carry
`EnergyType`. Anything category-specific is nullable, because the API omits it
rather than sending null.

**Collections are never null** — an absent array arrives empty, so iterating a
Trainer's `Attacks` is safe.

Three shapes are irregular, and the SDK smooths them:

- `Attack.Damage` is text, because the API sends `60` on one card and `"50+"` on
  another. `Attack.BaseDamage` gives the numeric part.
- `WeaknessOrResistance.Value` is text — `"×2"`, `"-20"`.
- TCGplayer prices are keyed by printing name, and the names vary per card, so
  they are a dictionary: `card.Pricing?.Tcgplayer?["holofoil"]`.

Next: **[Querying](querying.md)**.

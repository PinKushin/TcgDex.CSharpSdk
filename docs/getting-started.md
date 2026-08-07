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

## Timeouts

One request may take 30 seconds, headers and body together. Change it or remove
it entirely:

```csharp
builder.Services.AddTcgDex(options =>
{
    options.Timeout = TimeSpan.FromSeconds(10);      // stricter
    options.Timeout = Timeout.InfiniteTimeSpan;      // no limit
});
```

The default replaces `HttpClient`'s own 100 seconds, which nobody chose and
which leaves a caller blocked for over a minute and a half on an endpoint that
has stopped answering. The live API returns its largest response, the 2.3 MB
card list, in well under a second.

An expiry throws `TcgDexApiException`, like every other failure. Cancellation
**you** requested stays an `OperationCanceledException`, because that is yours to
observe rather than a fault to report.

The limit is applied with a linked `CancellationTokenSource` rather than by
setting `HttpClient.Timeout`, so an `HttpClient` you supply and share with the
rest of your application is left alone.

## Skipping pricing

Every card carries a `pricing` block, and it is the most expensive part of one
to deserialize — **3.86 µs and 2.12 KB of a 24.79 µs, 18.38 KB fetch**. If your
application never reads prices, turn it off:

```csharp
builder.Services.AddTcgDex(options => options.DeserializePricing = false);
```

`Card.Pricing` is then `null` for every card. It defaults to **on** for exactly
that reason: with it off you cannot tell a card the API has no prices for from
one where the option was set, so this is opt out rather than opt in. Against a
network round trip of 20–50 ms the saving is around 0.02% of a request — turn it
off because you do not want the data, not because you want the microseconds.

The API cannot be asked to leave it out; `fields=`, `select=` and friends all
return the same bytes, so this is saved in the parse rather than on the wire.

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

`Image`, `Logo` and `Symbol` are base URLs **without a file extension**, and the
helpers build the right form for each:

```csharp
string? art  = card.GetImageUrl(ImageQuality.High, ImageFormat.Png);
string? logo = card.Set.GetLogoUrl();
string? sym  = card.Set.GetSymbolUrl(ImageFormat.Webp);
```

Worth knowing why these are not one method: **card artwork takes a quality
segment and set assets do not.**

```
https://assets.tcgdex.net/en/swsh/swsh3/136/high.png   card    200
https://assets.tcgdex.net/en/swsh/swsh3/logo.png       logo    200
https://assets.tcgdex.net/en/swsh/swsh3/logo/high.png  logo    404
```

Every one of these returns `null` rather than a broken URL when the asset is
absent — some cards genuinely have no artwork.

## Streaming large result sets

```csharp
await foreach (var card in tcgdex.Cards.StreamAsync(
    new CardQuery().Where(c => c.Category == "Pokemon"), pageSize: 100, ct))
{
    // Pages are fetched as you consume them; breaking out stops the requests.
}
```

The API reports no total count, so the end of the results can only be detected
by receiving a short page. `StreamAsync` handles that once so you do not have to.

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

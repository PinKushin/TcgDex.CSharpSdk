# Querying

Predicates are written in C# and translated to the API's filter syntax:

```csharp
var query = new CardQuery()
    .Where(c => c.Name.Contains("Pikachu"))
    .Where(c => c.Hp > 100)
    .OrderByDescending(c => c.Name)
    .Page(1, 50);

var cards = await tcgdex.Cards.ListAsync(query, cancellationToken);
```

which produces:

```
cards?name=Pikachu&hp=gt:100&sort:field=name&sort:order=DESC&pagination:page=1&pagination:itemsPerPage=50
```

`CardQuery` is immutable — every method returns a new instance, so a
partly-built query can be shared and specialised without one caller's additions
leaking into another's.

## Operators

Every operator the API has, and nothing it does not:

| C# | Query syntax | Meaning |
|---|---|---|
| `c.Name == "Furret"` | `name=eq:Furret` | Exact match |
| `c.Name != "Furret"` | `name=neq:Furret` | Exact exclusion |
| `c.Hp > 100` | `hp=gt:100` | Greater than |
| `c.Hp >= 100` | `hp=gte:100` | Greater or equal |
| `c.Hp < 100` | `hp=lt:100` | Less than |
| `c.Hp <= 100` | `hp=lte:100` | Less or equal |
| `c.Name.Contains("pika")` | `name=pika` | Loose substring match |
| `c.Name.StartsWith("fu")` | `name=fu*` | Prefix match |
| `c.Name.EndsWith("chu")` | `name=*chu` | Suffix match |
| `!c.Name.Contains("pika")` | `name=not:pika` | Loose exclusion |
| `c.Effect == null` | `effect=null:` | Field absent |
| `c.Effect != null` | `effect=notnull:` | Field present |

Operands can appear in either order — `100 < c.Hp` and `c.Hp > 100` both emit
`hp=gt:100`.

## Combining filters

`&&`, and repeated `Where` calls, become separate parameters:

```csharp
new CardQuery()
    .Where(c => c.Category == "Pokemon")
    .Where(c => c.Hp > 250)

// category=eq:Pokemon&hp=gt:250
```

`||` becomes pipe-separated values — but **only within a single field**:

```csharp
new CardQuery().Where(c => c.Name == "Furret" || c.Name == "Sentret")

// name=eq:Furret|Sentret
```

An OR across two fields has no encoding in this API, so it throws rather than
silently dropping half your predicate and returning plausible-looking wrong
data:

```csharp
// NotSupportedException, naming both fields
new CardQuery().Where(c => c.Name == "Furret" || c.Hp > 100);
```

Issue one query per field and combine the results yourself.

## Sorting and paging

```csharp
new CardQuery()
    .OrderByDescending(c => c.Name)   // sort:field=name&sort:order=DESC
    .Page(2, 50)                      // pagination:page=2&pagination:itemsPerPage=50
```

**The API exposes no total count** and sends no pagination headers, so the
number of pages cannot be known up front. Read pages until one comes back
shorter than you asked for:

```csharp
const int PageSize = 100;

for (var page = 1; ; page++)
{
    var batch = await tcgdex.Cards.ListAsync(
        new CardQuery().Where(c => c.Category == "Pokemon").Page(page, PageSize),
        cancellationToken);

    foreach (var card in batch)
    {
        // ...
    }

    if (batch.Count < PageSize)
    {
        break;
    }
}
```

## Full detail in one request

List results are **briefs** — `id`, `localId`, `name`, `image` and nothing else.
Fetching full detail for each costs one call per card.

When you need the detail for many cards, `SearchDetailedAsync` gets it in a
single request over GraphQL:

```csharp
var cards = await tcgdex.Cards.SearchDetailedAsync(
    new CardFilter { Name = "Furret" },
    cancellationToken: ct);

// 12 fully populated cards in one request. Over REST that is 13 round trips.
```

Three limits come with it, all imposed by the GraphQL endpoint rather than by
this SDK:

| | REST (`ListAsync`) | GraphQL (`SearchDetailedAsync`) |
|---|---|---|
| Languages | all 18 | **English only** |
| Filters | all twelve forms above | **equality only** |
| `Pricing` | populated | **never populated** |

Use it for breadth cheaply; stay on REST when you need a language, a range
filter, or prices.

## What is rejected, and why

Anything the API cannot express is refused with a message naming the offending
expression:

```csharp
new CardQuery().Where(c => c.Name.Length > 5);      // no such filter
new CardQuery().Where(c => c.Set.Name == "x");      // no nested field syntax
new CardQuery().Where(c => c.Name.Trim() == "x");   // not a translatable method
```

This is deliberate. Approximating an untranslatable predicate would return the
wrong cards silently, which is worse than failing at the call site.

## Why this is not `IQueryable<Card>`

The API supports exactly the operators listed above. An `IQueryable<Card>` would
have to throw for most of LINQ — `Select` projections, `Join`, `GroupBy`, `Any`,
arbitrary `Where` bodies — which is a partial implementation that fails at
runtime rather than where you wrote it.

A dedicated builder makes the supported surface explicit and keeps every
rejection precise.

It also never calls `Expression.Compile()`, which emits IL at runtime and would
break Native AOT. Expression trees are walked structurally instead, and captured
variables are read from their closure — so this works:

```csharp
var minimumHp = 250;

new CardQuery().Where(c => c.Hp > minimumHp);   // hp=gt:250
```

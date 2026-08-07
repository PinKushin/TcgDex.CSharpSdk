# Compared with the other C# SDK

There is one other public C# SDK for TCGdex: [`TCGdex`](https://www.nuget.org/packages/TCGdex)
by luizaraujodev ([source](https://github.com/luizaraujodev/tcgdex-csharp-sdk)),
MIT licensed, published 2026-03-02, targeting `net10.0`.

This page exists because performance claims are cheap and measurements are not.
**On the two things measured here, this SDK loses.**

---

## Method

Both clients accept an injected `HttpClient`, which is what makes an honest
comparison possible: without it the only option is measuring over the live API,
which reports TCGdex's servers and the local connection rather than either
library.

Rules the harness holds itself to:

- **Same stub transport, same recorded payload** on both sides, so neither is
  doing less work.
- **Caching off on both.** Both libraries have it. Pitting a warm cache against
  a cold fetch would measure a configuration difference and call it speed.
- **Losses reported alongside wins.** There are no wins below.
- **Reproducible.** The harness is `ComparisonBenchmarks.cs` in this repository:

  ```bash
  dotnet run -c Release --project TcgDex.CSharpSdk.Benchmarks -- --filter "*Comparison*"
  ```

The other package is referenced by the benchmark project only, never by the SDK,
so it reaches no consumer.

---

## Results

Fetching and deserializing one card, from an in-memory stub:

| | This SDK | `TCGdex` | Ratio |
|---|---:|---:|---:|
| Time | 25.3 µs | **15.3 µs** | **0.60×** |
| Allocated | 18.6 KB | **12.2 KB** | **0.66×** |

Building a filtered, sorted, paginated query:

| | This SDK | `TCGdex` | Ratio |
|---|---:|---:|---:|
| Time | 3,100 ns | **135 ns** | **0.04×** |
| Allocated | 4,744 B | **416 B** | **0.09×** |

This SDK is still slower on both. It is **1.7× the time** on a fetch and **23×**
on query building.

### What the first measurement changed

The fetch row started worse — 29.1 µs and **43.3 KB** — and the benchmark is
what found out why. Three fixes to `BoundedContent`, none of which gave anything
up:

| Change | Allocated |
|---|---:|
| Original | 43.3 KB |
| Deserialize from UTF-8 bytes rather than a decoded `string` | 34.6 KB |
| Pre-size the buffer from `Content-Length` | 34.6 KB |
| Rent the 16 KB read chunk from `ArrayPool` instead of allocating it | **18.6 KB** |

**57% of the allocations removed**, and the size limit, the AOT safety and every
test are unchanged — 447 unit tests across three frameworks and 149 live
integration tests still pass.

Two things worth keeping from that:

- **The largest cost was scratch space, not the payload.** A fresh 16 KB chunk
  buffer per request was bigger than the ~10 KB body it was reading. Renting it
  was a four-line change and did more than the other two together.
- **Pre-sizing the buffer changed nothing at all**, despite being the obvious
  fix and the one attempted second. Kept because it is correct and free, but it
  is a reminder that the intuitive optimisation and the effective one are often
  different, and only measurement tells them apart.

### Correction: the models are the same size

An earlier version of this page said their `CardModel` exposes 37 properties to
this SDK's 22, and concluded they deserialize more. **Both numbers were wrong.**
Their file declares five classes, so the 37 summed `CardModel` with four nested
model types; the 22 missed eight properties on this side that use a
backing-field pattern and span two lines.

Counted properly it is **about 30 each**. Model size explains nothing, in either
direction.

### Not AOT either

Their `ModelBase.Fill` resolves each property with
`GetType().GetProperty(ToPascalCase(name))` — reflection, per property, per
object. That is not trim- or AOT-safe, and per-property reflection is normally
*slower* than source generation. They are ahead despite that technique, not
because of it.

### Where the difference actually is

Two fields, and it is a design choice rather than an optimisation:

```csharp
// Theirs
public JsonElement? DamageJson { get; set; }
public JsonElement? LevelJson { get; set; }
```

Those are the polymorphic fields, stored raw. `attacks[].damage` really is
polymorphic in the live API — `xy1-1` returns the number `60`, `swsh1-1` returns
the string `"50+"` because printed damage can carry a modifier — and `level`
behaves the same way. Their model keeps the `JsonElement` and hands the problem
to the caller.

This SDK converts instead. `FlexibleStringConverter` normalises both shapes to
`string?` so `attack.Damage` is always usable, and `Attack.BaseDamage` parses the
leading digits to `int?` for numeric comparison.
`TcgPlayerPricingConverter` does the equivalent for pricing, whose printing keys
vary by card — `normal` and `reverse-holofoil` on `swsh3-136`, `holofoil` on
`base1-4` — collecting unrecognised keys into a dictionary so an unanticipated
printing is not silently dropped.

**So part of the measured gap is work that has not been avoided, only moved.**
To get from their `DamageJson` to a number a consumer writes roughly what the
converter does:

```csharp
int? baseDamage = card.Attacks?[0].DamageJson switch
{
    { ValueKind: JsonValueKind.Number } e => e.GetInt32(),
    { ValueKind: JsonValueKind.String } e => ParseLeadingDigits(e.GetString()),
    _ => null,
};
```

— per call site, per field, in every application, untested. The same applies to
pricing, where the caller has to enumerate properties and skip the two metadata
keys themselves.

**This is an explanation, not a defence.** Two things follow from it and only one
is comfortable:

- The comparison is not measuring identical work, and this page should say so
  rather than presenting the ratio bare. Work done once in a library beats the
  same work repeated in every consumer, and it does not show up on their side of
  the table at all.
- It still does not account for the whole gap. Two converters on a card with a
  handful of attacks is not 10 µs of work. Source generation measuring slower
  than reflection for these models covers more of it, and some remains
  unexplained. Being slower is not excused by being more convenient.
## Reading the query result

Deliberately not equivalent work. This SDK translates a **LINQ expression tree**,
which the compiler checks:

```csharp
new CardQuery().Where(c => c.Hp > 100)
```

Theirs takes field names and values as strings, which nothing can check:

```csharp
Query.Create().GreaterThan("hp", 100)
```

A typo in `"hp"` is a runtime surprise in one and a compile error in the other.
So 3.1 µs against 135 ns is **the price of the type-safe form**, not a race to
concatenate strings — and it is charged once per request, against a network
round trip of 20–50 ms. It is 0.01% of a request. Real, measured, and irrelevant
to throughput.

The honest summary of that row: *this SDK trades ~3 µs per query for
compile-time checking.* Whether that is worth it is the reader's call, but the
number should not be hidden.

## Reading the fetch result

The copying identified by the first run has been fixed — see above. What remains
of the gap is mostly deliberate:

1. **Source generation is not the faster path here.** Measured separately in
   [`measuring.md`](measuring.md): the source-generated path is 1.23× the time
   and 1.5× the allocations of reflection for these models. It stays, because it
   is what makes the SDK trim- and AOT-safe. That is a trade this SDK makes on
   purpose and the other one does not.
2. **Custom converters.** `FlexibleStringConverter` handles the polymorphic
   `damage` field and `TcgPlayerPricingConverter` the dynamic printing keys —
   work the other SDK does not do, in exchange for typed access to fields whose
   shape varies.

Neither is a reason to stop looking, and a second correction is due here: an
earlier version of this page called it "a ~10 KB payload". The card fixture is
**2,938 bytes**. Allocating 18.6 KB to handle it is 6.4× the payload, which is
not defensible — the wrong figure made it look better than it is.

### Where the deserialization time goes

Isolated by stripping one block from the same card, at full precision
(StdDev ~0.3 µs):

| Deserializing the full card | Time | Allocated |
|---|---:|---:|
| As shipped | 23.04 µs | 11.17 KB |
| With the `pricing` block removed | 18.35 µs | 8.97 KB |
| Reflection, same model | 17.26 µs | 7.40 KB |

**`TcgPlayerPricingConverter` accounts for 4.7 µs and 2.2 KB — 20% of both.**
It is hand-written code, which makes it the first place to look rather than the
last, and it is paid on every card whether or not the caller reads pricing.

The remaining ~5.8 µs between source generation and reflection is
System.Text.Json internals. That one stays: source generation is what makes the
SDK trim- and AOT-safe, and no amount of it being slower here changes that.

Deserialization is roughly 86% of the whole request path — the transport, the
logging, the activity and the URI construction together account for about
3.5 µs. Anything spent optimising elsewhere is spent in the wrong place.

---

## What this comparison does not cover

- **Network time**, which dominates real usage and is identical for both.
- **Feature coverage** — a different question. This SDK multi-targets
  `netstandard2.0`, so it runs on Unity and .NET Framework where a `net10.0`-only
  package cannot; it has ETag revalidation, `IAsyncEnumerable` pagination,
  `ILogger`/`ActivitySource` observability, and verified Native AOT support.
  None of that makes it faster at the two things measured above.
- **Correctness**, which the test suite covers — 447 unit tests across three
  frameworks and a 90.03% mutation score.

Being slower at deserialization is still a fair criticism of this SDK. It is
less fair than it was a day ago, and it stopped being fair to call it careless.

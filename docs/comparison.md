# Compared with the other C# SDK

There is one other public C# SDK for TCGdex: [`TCGdex`](https://www.nuget.org/packages/TCGdex)
by luizaraujodev ([source](https://github.com/luizaraujodev/tcgdex-csharp-sdk)),
MIT licensed, published 2026-03-02, targeting `net10.0`.

This page exists because performance claims are cheap and measurements are not.
**This SDK is slower on every timing measured here, and allocates less on all
but one.**

> **This page was wrong until 2026-08-07, in its own disfavour.** It claimed
> caching was off on both sides. The other SDK caches *by default*, and the
> harness asked for the same card on every iteration, so its numbers were warm
> cache hits against this SDK's full fetch. Everything below is re-measured with
> that fixed. The correction is written up rather than quietly applied — see
> [The fairness rule that was not true](#the-fairness-rule-that-was-not-true).

---

## Method

Both clients accept an injected `HttpClient`, which is what makes an honest
comparison possible: without it the only option is measuring over the live API,
which reports TCGdex's servers and the local connection rather than either
library.

Rules the harness holds itself to:

- **Same stub transport, same recorded payload** on both sides.
- **Caching off on both**, which now means `CacheTTL = 0` on theirs rather than
  an assumption. Pitting a warm cache against a cold fetch measures a
  configuration difference and calls it speed.
- **Losses reported alongside wins.**
- **Reproducible.** Every benchmark class in this repository now has an arm for
  each SDK, not just the card fetch:

  ```bash
  dotnet run -c Release --project TcgDex.CSharpSdk.Benchmarks -- --filter "*Comparison*"
  dotnet run -c Release --project TcgDex.CSharpSdk.Benchmarks -- --filter "*LargePayload*"
  dotnet run -c Release --project TcgDex.CSharpSdk.Benchmarks -- --filter "*Eviction*"
  dotnet run -c Release --project TcgDex.CSharpSdk.Benchmarks -- --filter "*CachingBenchmarks*"
  ```

### The fairness rule that was not true

The second rule sat on this page for weeks as a claim rather than a check. The
other SDK's client arrives with a `MemoryTCGDexCache` and `CacheTTL = 3600`
already set, so from the second iteration onward it was answering from memory
while this SDK went through its whole transport. Counting requests at the
handler settles it: **three calls, one request.**

`CacheTTL = 0` is what disables it. Assigning `Cache = null` throws
`ArgumentNullException`.

The distortion was modest — their cache stores the response *string*, not the
deserialized model, so a hit re-parses and returns a different instance each
time. It was saving the stub transport, not the deserialization that dominates
both sides. Modest is not the point. **A stated fairness rule nobody verified is
worth less than no rule, because it reads as evidence.**

The other package is referenced by the benchmark project only, never by the SDK,
so it reaches no consumer.

---

## Results

All four workloads, both SDKs, caching genuinely off. Bold marks the winner.

**Fetching and deserializing one card** (2,938 bytes):

| | This SDK | This SDK, `DeserializePricing = false` | `TCGdex` |
|---|---:|---:|---:|
| Time | 24.79 µs | 20.93 µs | **18.57 µs** |
| Allocated | 18.38 KB | **16.26 KB** | 25.12 KB |

**Fetching and deserializing the unpaginated card list** (2,356,046 bytes,
~21,000 entries):

| | This SDK | `TCGdex` |
|---|---:|---:|
| Time | 24.86 ms | **20.83 ms** |
| Allocated | **10.88 MB** | 17.77 MB |

**Building a filtered, sorted, paginated query:**

| | This SDK | `TCGdex` |
|---|---:|---:|
| Time | 2,741 ns | **98 ns** |
| Allocated | 4,664 B | **416 B** |

**Storing into a response cache, replacing an existing entry:**

| | This SDK | `TCGdex` |
|---|---:|---:|
| Time | 96.6 ns | **47.4 ns** |
| Allocated | 48 B | **0 B** |

So: **slower on every timing**, by 1.13–1.34× on a fetch, 1.19× on the list,
2× on a cache store and 28× on query building. **Lighter on allocations
everywhere except query building**, by 27% on a card and 39% on the list.

The allocation reversal is the part that changed. This page previously reported
0.66× — this SDK allocating half again as much as theirs — and that was their
warm cache skipping a transport round trip.

Two caveats on the cache row, both of which matter more than 49 ns. Their cache
has **no bound**: `MemoryTCGDexCache` is a `Dictionary` behind a lock with no
`MaxEntries` and no eviction, so a long-lived process retains every response
body it has ever fetched, as a UTF-16 string at roughly twice the bytes of the
payload. And it is **on by default**, which is how this page came to be wrong.
Ours is bounded at 512 entries and stores bytes — the 48 B and the extra 49 ns
are what a bound costs.

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
  buffer per request was bigger than the 2,938-byte body it was reading. Renting
  it was a four-line change and did more than the other two together.
- **Pre-sizing the buffer changed nothing at all**, despite being the obvious
  fix and the one attempted second — *at this payload size*. On the 2.3 MB list
  it saves **2.24 MB per request**, because that is where a `MemoryStream`'s
  doubling growth starts to cost. The row above is not wrong, it is
  size-specific, and reading it as a general result was the mistake. See
  [`measuring.md`](measuring.md).

### Correction: the models are the same size

An earlier version of this page said their `CardModel` exposes 37 properties to
this SDK's 22, and concluded they deserialize more. **Both numbers were wrong.**
Their file declares five classes, so the 37 summed `CardModel` with four nested
model types; the 22 missed eight properties on this side that use a
backing-field pattern and span two lines.

Counted properly it is **about 30 each**. Model size explains nothing, in either
direction.

**One field does, though, and it is the one that is absent.** Their `CardModel`
has no `Pricing` property — and there is no pricing type anywhere in their
assembly. The block arrives on the wire and is discarded. On this side it is the
single most expensive part of a card to parse, at 3.86 µs and 2.12 KB of a
24.79 µs, 18.38 KB fetch.

That is why the results table carries a `DeserializePricing = false` column. It
is not there to flatter the ratio — parsing pricing is a feature, and a consumer
who wants prices from the other SDK writes that code themselves. It is there
because a reader comparing deserialization speed should be able to see the two
numbers side by side and decide which one answers their question.

### Not AOT either

Their `ModelBase.Fill` resolves each property with
`GetType().GetProperty(ToPascalCase(name))` — reflection, per property, per
object. That is not trim- or AOT-safe, and per-property reflection is normally
*slower* than source generation.

At one card they are ahead despite that. **At 21,000 objects it starts to
show**: on the 2.3 MB list their advantage narrows to 1.19× on time and they
allocate 63% more than this SDK does. A fixed cost per property per object is
cheap until there are enough objects.

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
- It still does not account for the whole gap. The gap is now 6.2 µs rather than
  the 10 µs this paragraph was written against, and `TcgPlayerPricingConverter`
  is measurably 3.86 µs of it — so the converters explain most of what is left,
  with source generation losing to reflection covering the rest. Being slower is
  not excused by being more convenient.
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
So 2.7 µs against 98 ns is **the price of the type-safe form**, not a race to
concatenate strings — and it is charged once per request, against a network
round trip of 20–50 ms. It is 0.01% of a request. Real, measured, and irrelevant
to throughput.

The honest summary of that row: *this SDK trades ~2.6 µs per query for
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
last, and it was paid on every card whether or not the caller read pricing.

**`TcgDexOptions.DeserializePricing = false` now makes that optional**, worth
3.86 µs and 2.12 KB end to end. Slightly less than the 4.7 µs above, because
that row deleted the block from the JSON while the option only skips building
from it — `Utf8JsonReader.Skip` still walks the tokens. The API offers no way to
close that last gap: `fields=`, `select=`, `pricing=false` and `include=` all
return the identical 2,940 bytes from the live service.

It defaults to **on**. A `null` pricing must go on meaning "the API sent none"
rather than "it was switched off", and 4 µs against a 20–50 ms round trip does
not buy a silently wrong answer.

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
- **Correctness**, which the test suite covers — 456 unit tests across three
  frameworks and a mutation score around 88%.
- **Anything the other SDK does not have.** ETag revalidation, a bounded cache,
  `netstandard2.0`, `IAsyncEnumerable` pagination, Native AOT — there is no row
  to lose because there is nothing to compare against.

Being slower at deserialization is still a fair criticism of this SDK, and it is
the one to make: every timing row above is a loss. What is no longer fair is the
allocation claim, which this page had backwards, and the suggestion that the
SDK is careless with memory — it allocates 27% less on a card and 39% less on
the list.

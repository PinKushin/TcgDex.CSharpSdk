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

### The obvious excuse does not apply

A leaner model would explain the fetch result — less to populate, less to
allocate. It is not the explanation: their `CardModel` exposes **37** properties
to this SDK's **22**. If anything they deserialize more.

Nor is it AOT. Their `ModelBase.Fill` resolves each property with
`GetType().GetProperty(...)` — reflection, per property, per object, which is not
trim- or AOT-safe and is normally *slower* than source generation. They are ahead
despite that, not because of it.
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

Neither is a reason to stop looking. 18.6 KB for a ~10 KB payload is defensible;
it is not obviously optimal.

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

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
| Time | 29.1 µs | **16.8 µs** | **0.58×** |
| Allocated | 43.3 KB | **12.2 KB** | **0.28×** |

Building a filtered, sorted, paginated query:

| | This SDK | `TCGdex` | Ratio |
|---|---:|---:|---:|
| Time | 3,100 ns | **135 ns** | **0.04×** |
| Allocated | 4,744 B | **416 B** | **0.09×** |

Theirs is roughly **1.7× faster and 3.5× lighter** on a card fetch, and **23×
faster** at building a query.

### The obvious excuse does not apply

A leaner model would explain the fetch result — less to populate, less to
allocate. It is not the explanation: their `CardModel` exposes **37** properties
to this SDK's **22**. If anything they deserialize more.

---

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

This one is not so easily explained away, and is **worth fixing rather than
justifying**. 43 KB to deserialize a ~10 KB payload is more copying than the job
needs. The likely contributors, in order of suspicion:

1. **`BoundedContent` reads the body twice.** Enforcing `MaxResponseBytes`
   copies the stream into a `MemoryStream`, calls `ToArray()`, then
   `Encoding.UTF8.GetString`, and hands a `string` to the deserializer — at
   least two full copies of the body before parsing starts. Deserializing from
   the buffered bytes directly, or from a size-limited stream, would remove
   both.
2. **Custom converters.** `FlexibleStringConverter` and
   `TcgPlayerPricingConverter` do work the other SDK does not.
3. **Source generation is not the faster path here.** Measured separately in
   [`measuring.md`](measuring.md): the source-generated path is 1.23× the time
   and 1.5× the allocations of reflection for these models. It is kept because
   it is what makes the SDK trim- and AOT-safe, not because it is quick.

None of that is a defence of the number. It is a list of where to look.

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

Being slower at deserialization is a fair criticism of this SDK today, and the
first item on the performance list.

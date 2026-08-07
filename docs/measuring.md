# Benchmarks and mutation testing

Two things the rest of the test suite cannot tell you: **how fast the code is**,
and **whether the tests would notice if it were wrong**. Both tools are free and
MIT/Apache-2.0, and neither ships in the package — BenchmarkDotNet lives in its
own non-packable project, Stryker is a `dotnet tool`.

---

## Benchmarks

```bash
dotnet run -c Release --project TcgDex.CSharpSdk.Benchmarks -- --filter "*Query*"
dotnet run -c Release --project TcgDex.CSharpSdk.Benchmarks -- --filter "*Serialization*"
```

Release only — BenchmarkDotNet refuses a Debug build, and it is right to: a Debug
measurement is noise presented as data. Add `--job short` for a quick pass while
iterating; omit it for numbers worth quoting.

### What they measure, and why those things

Both benchmark sets exist to check claims that were previously **arguments
rather than evidence**.

`QueryBenchmarks` covers the expression-tree translator, which never calls
`Expression.Compile()`. The AOT smoke test proves that choice is *safe*; this
shows what it costs. Read the numbers as a regression baseline rather than a
headline: a query is built once per request against a network round trip of tens
of milliseconds, so the reason to measure is to notice if it ever moves by an
order of magnitude — which would mean something started allocating or compiling.

`SerializationBenchmarks` compares the shipped source-generated path against
reflection-based `System.Text.Json`, with the same naming policy, the same
case-insensitivity and the same converters on both sides.

### The result corrected a claim

Measured on the recorded `card-pokemon-full.json`, relative to the
source-generated baseline:

| Path | Time | Allocated |
|---|---|---|
| Source-generated (as shipped) | 1.00 | 1.00 |
| Source-generated, type info hoisted | 0.99 | 1.00 |
| **Reflection-based** | **0.81** | **0.66** |

Reflection is **faster and lighter** for this workload.

Precisely what this does and does not overturn. The claim in the SDK's own
source — that source generation "avoids the reflection cost on the first call
for each type" — is about **warm-up**, and remains true. The claim it does not
support is the stronger one made while planning this project: that source
generation is faster *on every call*, at steady state, and not merely at warm-up.
That is now measured, and it is wrong for these models.

**The design does not change, because speed was never the real reason.**
Source generation is what makes the SDK trim- and AOT-safe; reflection-based
serialization breaks Native AOT outright, and that is not a trade available at
any speed. What changed is the justification, which had drifted into claiming a
benefit the code does not deliver.

The hoisting row is the other useful outcome. The SDK resolves `JsonTypeInfo`
through `Options.GetTypeInfo(typeof(T))` on **every** request, which looked like
an obvious optimisation. At 0.99 it is free, so that optimisation was not made —
a negative result that prevented a pointless change.

---

## Mutation testing

```bash
dotnet stryker                                        # full run, slow
dotnet stryker --mutate "**/Querying/QueryFilter.cs"  # one file, fast
```

Configuration is in `stryker-config.json`.

### Why, when coverage is already gated

They answer different questions, and only one of them is about test quality:

| | Question |
|---|---|
| Line coverage | Did this line run? |
| Branch coverage | Were both outcomes of this condition exercised? |
| **Mutation score** | **If this code were wrong, would any test fail?** |

Coverage can be 100% with assertions that check nothing. Mutation testing
changes the code — flips a comparison, removes a call, alters a constant — and
reports whether the suite noticed. A *surviving* mutant is a line the tests
execute but do not actually verify.

This is the systematic version of the manual mutation checks used throughout
this repo: reverting `EscapeDataString` to prove the traversal tests could fail,
narrowing the GraphQL escape range, making `Where` drop its filters. Those were
done by hand on a dozen specific claims. Stryker does it everywhere.

### Where it stands

Full run, ~11 minutes:

| Outcome | Baseline | Now |
|---|---:|---:|
| Killed | 497 | **575** |
| **Survived** | **140** | **63** |
| Timeout | 11 | 12 |
| No coverage | 4 | 0 |
| **Mutation score** | **77.91%** | **90.03%** |

Line coverage did not move across any of that work — 99.77% before and after.
Every one of those 78 newly-killed mutants was in code the suite already
executed. Coverage said the lines ran; mutation testing said whether running
them proved anything.

Per file:

| File | Baseline | Now |
|---|---:|---:|
| `Querying/CardFilter.cs` | 67% | **100%** |
| `Caching/MemoryTcgDexResponseCache.cs` | 71% | **97%** |
| `Caching/TcgDexCacheOptions.cs` | 83% | **97%** |
| `TcgDexClient.cs` | 53% | **93%** |
| `Models/CardImage.cs` | 93% | **93%** |
| `GraphQlTransport.cs` | 79% | **92%** |
| `Querying/ExpressionTranslator.cs` | 84% | **88%** |
| `Resources/Resources.cs` | 82% | **85%** |
| `Serialization/TcgPlayerPricingConverter.cs` | 77% | **85%** |
| `Http/BoundedContent.cs` | 63% | **83%** |
| `TcgDexTransport.cs` | 64% | **81%** |
| `TcgDexServiceCollectionExtensions.cs` | 60% | **80%** |
| `Caching/TcgDexCachingHandler.cs` | 65% | **70%** |

### The single most common real gap

Across every file, the same shape kept appearing: **tests asserted the exception
type and never its message.**

`TcgDexApiException` is the SDK's only error contract, so its text is all that
separates "the network died" from "the body was not JSON" from "that resource is
missing" for someone reading a log. The query translator's rejections are worse
still — half of each message is the *remedy*, and asserting only the field name
let the actionable half be deleted silently. One test was even named
`OrWithMismatchedOperators_NamesBothOperators` and asserted neither operator.

If you write one kind of test after reading this, assert the message.

### What the remaining 63 are

Mostly not gaps. In rough order of frequency:

- **`.ConfigureAwait(false)` flipped to `true`.** No observable difference
  without a synchronization context. Unkillable, and the single largest group —
  around eleven of the caching handler's sixteen.
- **Guards the public API validates first.** `TcgDexClient` checks its arguments
  before the transport or handler sees them. Note the contrast: the same shape
  in `MemoryTcgDexResponseCache` and `TcgDexCacheOptions` *was* a real gap,
  because those types are public and their interface is an extension point.
- **Ternary and catch collapses** where the mutated branch throws into a `catch`
  that produces the same result anyway.
- **Non-deterministic tie-breaks**, such as the LRU eviction comparison, which
  depends on dictionary ordering.

`TcgDexCachingHandler` at 70% is the floor and is dominated by the first
category; its realistic ceiling is around 75%. Pushing past that means writing
tests for the metric rather than for behaviour, which is where this stops.

### Thresholds

```json
"thresholds": { "high": 95, "low": 90, "break": 85 }
```

Ratcheted 60 → 80 → 85 as the score cleared each with headroom, the same way the
coverage gate moved. Verified in the failing direction rather than assumed:
running one file with `--break-at` above its score exits with code 2 and "Final
mutation score is below threshold break. Crashing...".

Unlike the coverage gate this is **not enforced in CI** — a full run takes
minutes to tens of minutes against roughly two seconds for the unit tests, so it
stays a deliberate periodic and pre-release check.
### Worked example: `TcgDexTransport.cs`, 64% to 85%

Twenty-four survivors, triaged rather than blindly tested. Fourteen were real
gaps and are now killed by `TransportDetailTests` plus three additions to
`LoggingTests`. The pattern in every one of them: **the old tests asserted the
exception type but never its message.**

`TcgDexApiException` is the single error contract for the whole SDK, so its text
is the only thing distinguishing "the network died" from "the body was not JSON"
from "that resource is missing" for someone reading a log. A mutant that blanked
a message left every test passing.

Two findings worth keeping:

- **`ReasonPhrase = null` does not stick** on a known status code — .NET
  substitutes the standard phrase, so the final `?? "no detail supplied"`
  fallback is unreachable that way. It needs a non-standard status. Not
  contrived: HTTP/2 removed reason phrases from the protocol, so a real HTTP/2
  response reaches that branch for any status.
- **The activity-failure calls have two call sites**, and the existing test only
  covered one. A 502 reaches the failure path through a *response*; a dropped
  connection reaches it through an *exception*. Removing `RecordFailure` from
  the exception path went unnoticed.

### The ten that remain are equivalent, and that is the ceiling

Not laziness — none can be killed by any test:

| Mutation | Why unkillable |
|---|---|
| `.ConfigureAwait(false)` to `true` (×4) | No observable difference without a synchronization context |
| `Guard.NotNull(...)` removed (×3) | `TcgDexClient` validates first; no public path reaches the transport's own guard |
| Ternary and catch-block collapses (×3) | Forcing `Deserialize("   ")` throws `JsonException`, which the next `catch` turns back into `null` — identical behaviour |

This is why the break threshold sits at 60 rather than near the coverage gate. A
file can be thoroughly tested and still not reach 100%, and pretending otherwise
produces tests written for the metric instead of for the behaviour.

### Thresholds, and why they are below the coverage gate

```json
"thresholds": { "high": 85, "low": 70, "break": 60 }
```

A mutation score is not comparable to a coverage percentage and will always be
lower — some mutants are *equivalent*, meaning the mutated code behaves
identically and no test could possibly kill them. Setting the break threshold
near the 99.5% line-coverage gate would guarantee a red run that teaches nothing.

`Compatibility/CompilerFeatureShims.cs` is excluded: those types have no
behaviour to mutate, so every mutant there is trivially equivalent noise.

### Not a per-push gate

The suite runs once per mutant, so a full pass takes minutes to tens of minutes
— against roughly 2 seconds for the unit tests. It is a periodic and
pre-release check, run deliberately, not something to put in front of every
commit.

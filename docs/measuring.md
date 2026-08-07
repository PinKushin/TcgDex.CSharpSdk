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

| Outcome | First run | Now |
|---|---:|---:|
| Killed | 497 | **561** |
| **Survived** | **140** | **83** |
| Timeout | 11 | 5 |
| No coverage | 4 | 3 |
| **Mutation score** | **77.91%** | **86.81%** |

Line coverage did not move (99.77%): every one of those 57 newly-killed mutants
was in code the suite already executed. That is the entire point of the exercise
— coverage said the lines ran, and mutation testing said whether running them
proved anything.

Per file, after the sweep:

| File | Before | After |
|---|---:|---:|
| `Querying/CardFilter.cs` | 67% | **100%** |
| `Caching/MemoryTcgDexResponseCache.cs` | 71% | **97%** |
| `TcgDexClient.cs` | 53% | **93%** |
| `GraphQlTransport.cs` | 79% | **92%** |
| `TcgDexTransport.cs` | 64% | **76%**\* |
| `Http/BoundedContent.cs` | 63% | **79%** |
| `Caching/TcgDexCachingHandler.cs` | 65% | **70%** |

\* The transport reads lower here than the 85% measured in isolation. Scoring a
single file runs only the mutants in it; a full run includes mutants elsewhere
that the same tests cover, which shifts the denominator. Compare like with like.

### What the remaining 83 are

Mostly not gaps. The recurring shapes, in rough order of frequency:

- **`.ConfigureAwait(false)` flipped to `true`.** No observable difference
  without a synchronization context. Unkillable, and the single largest group —
  ten of the caching handler's sixteen.
- **Guards the public API validates first.** `TcgDexClient` checks its arguments
  before the transport or handler sees them, so the inner `Guard.NotNull` calls
  cannot be reached with null through any public path.
- **Ternary and catch collapses** where the mutated branch throws into a `catch`
  that produces the same result anyway.
- **Non-deterministic tie-breaks**, such as the LRU eviction comparison, which
  depends on dictionary ordering and so cannot be pinned by any test.

The two files still at 70–76% are dominated by the first two categories. Their
realistic ceilings are around 75% and 85%; pushing past that would mean writing
tests for the metric rather than for behaviour.

### Thresholds

```json
"thresholds": { "high": 90, "low": 85, "break": 80 }
```

Raised from `60` once the score cleared 80 with headroom, the same ratchet the
coverage gate uses. Verified in the failing direction rather than assumed:
running one file with `--break-at 85` against its 79.17% exits with code 2 and
"Final mutation score is below threshold break. Crashing...".

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

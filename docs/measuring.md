# Benchmarks, properties, mutation testing and fuzzing

Four things the rest of the test suite cannot tell you: **how fast the code is**,
**whether a stated invariant holds for inputs nobody wrote down**, **whether the
tests would notice if the code were wrong**, and **what happens on input nobody
thought of**.

All four tools are free and MIT/Apache-2.0, and none ships in the package —
BenchmarkDotNet and SharpFuzz live in their own non-packable projects, Stryker is
a `dotnet tool`, and CsCheck is a test-only reference.

The four overlap less than they look. A benchmark says a thing is fast; a
property says it is right for a thousand inputs; mutation testing says the tests
would have noticed had it not been; a fuzzer says it does not fall over on input
the SDK never produced. Each has caught a defect in this repository that the
others did not.

---

## Benchmarks

```bash
# everything
dotnet run -c Release --project TcgDex.CSharpSdk.Benchmarks -- --filter "*"

# or one set at a time
dotnet run -c Release --project TcgDex.CSharpSdk.Benchmarks -- --filter "*Query*"
dotnet run -c Release --project TcgDex.CSharpSdk.Benchmarks -- --filter "*Serialization*"
dotnet run -c Release --project TcgDex.CSharpSdk.Benchmarks -- --filter "*LargePayload*"
dotnet run -c Release --project TcgDex.CSharpSdk.Benchmarks -- --filter "*Caching*"
dotnet run -c Release --project TcgDex.CSharpSdk.Benchmarks -- --filter "*Eviction*"
dotnet run -c Release --project TcgDex.CSharpSdk.Benchmarks -- --filter "*Comparison*"
```

Release only — BenchmarkDotNet refuses a Debug build, and it is right to: a Debug
measurement is noise presented as data. Add `--job short` for a quick pass while
iterating; omit it for numbers worth quoting.

### What they measure, and why those things

**Six sets, 29 benchmarks.** Each exists to check a claim that was previously an
**argument rather than evidence** — several of them claims this repository had
already written down and had wrong.

| Set | Benchmarks | The claim it tests |
|---|---|---|
| `QueryBenchmarks` | 4 | The expression translator is cheap despite never calling `Expression.Compile()`. |
| `SerializationBenchmarks` | 6 | Source generation beats reflection. **It does not**, on time — see below. |
| `LargePayloadBenchmarks` | 5 | Conclusions drawn from an 18 KB card still hold at 2.3 MB. |
| `CachingBenchmarks` | 5 | A cache hit is worth having, and a warm hit skips the parse. |
| `EvictionBenchmarks` | 5 | Storing into a full cache costs about what storing into an empty one does. |
| `ComparisonBenchmarks` | 5 | This SDK is competitive with the other C# TCGdex client. |

`QueryBenchmarks` covers the expression-tree translator, which never calls
`Expression.Compile()`. The AOT smoke test proves that choice is *safe*; this
shows what it costs. Read the numbers as a regression baseline rather than a
headline: a query is built once per request against a network round trip of tens
of milliseconds, so the reason to measure is to notice if it ever moves by an
order of magnitude — which would mean something started allocating or compiling.

`SerializationBenchmarks` compares the shipped source-generated path against
reflection-based `System.Text.Json`, with the same naming policy, the same
case-insensitivity and the same converters on both sides. It also includes a
Newtonsoft.Json leg, and legs with pricing and with attacks removed, which is
where the cost of the `pricing` block was quantified.

`CachingBenchmarks` and `EvictionBenchmarks` cover the two halves of the cache:
what a hit saves, and what a store costs once the store is full. The second
found a real defect — see [Where a cache store spends its time](#where-a-cache-store-spends-its-time).

`ComparisonBenchmarks` runs both SDKs through the same harness. Its first version
was wrong in this SDK's favour by disabling caching on one side only; the numbers
here are from after that was fixed.

### The result corrected a claim

Measured on the recorded `card-pokemon-full.json`, relative to the
source-generated baseline:

| Path | Time | Allocated |
|---|---|---|
| Source-generated (as shipped) | 1.00 | 1.00 |
| Source-generated, type info hoisted | 0.99 | 1.00 |
| **Reflection-based** | **0.81** | **0.66** |

Reflection is **faster and lighter** for this workload. Newtonsoft.Json, measured
as a control, is slower than both — 26.18 µs and 16.62 KB against source
generation's 22.12 µs and 10.95 KB. It is not a candidate (not trim- or AOT-safe)
and it is not a wrapper over System.Text.Json but a wholly separate
implementation, which is what makes it useful here: it rules out the model shape
as the explanation, since an independent serializer on the identical type is
slower still, and needs no custom converters to do it.

The anomaly is therefore narrow. Source generation beats Newtonsoft and loses
only to System.Text.Json's own reflection path on these models.

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

### The same measurement at 800× the size

Every figure above comes from one card of **2,938 bytes**. `GET /v2/en/cards`
returns **2,356,046 bytes** — measured against the live API on 2026-08-07, with
an accurate `Content-Length` and no compression — and applications hit it on
startup to build an index. `LargePayloadBenchmarks` runs the same comparison
there, against a body synthesized to that size from the recorded brief-list
shape.

| | Time | Allocated |
|---|---:|---:|
| Source-generated (as shipped) | 26.3 ms | 8.63 MB |
| Reflection, same model | 22.4 ms | 7.48 MB |
| Fetch + deserialize, `Content-Length` declared | 25.6 ms | 10.88 MB |
| Fetch + deserialize, length unknown | 26.2 ms | 13.12 MB |

Two things follow, one of which contradicts an earlier conclusion.

**The source-generation penalty is proportional, not fixed.** At 2.9 KB it was
0.81× time and 0.66× allocations against reflection; here it is 0.85× and 0.87×.
Had the cost been a fixed per-call overhead it would have vanished into a
payload 800× larger. It does not, so the AOT guarantee costs roughly 15–20% of
deserialization time at any size — about 4 ms on this endpoint, against a live
fetch of it that took 703 ms.

**The `Content-Length` capacity hint measured as doing nothing, and that was a
size artefact.** `BoundedContent` pre-sizes its buffer from the declared length,
which changed neither time nor allocations on a 2.9 KB card. At 2.3 MB it saves
**2.24 MB per request** — the doubling `MemoryStream` growth it avoids — while
still not measurably changing the time. The allocation figures are identical
across repeat runs; the times are not, so read the ratio and not the millisecond.

The uncomfortable number is the last column of the first row: **8.63 MB
allocated to parse 2.25 MB of JSON**, and 10.88 MB for the whole request. That
is 3.8× and 4.8× the payload.

### Where a cache store spends its time

`EvictionBenchmarks` stores into a cache already at its bound, sweeping
`MaxEntries`, because that is the only state in which eviction runs — and once
a cache is full, it runs on *every* write.

| `MaxEntries` | Store, before | Store, after | `ConcurrentDictionary.Count` alone |
|---:|---:|---:|---:|
| 64 | 1,827 ns | 924 ns | 299 ns |
| 512 (default) | 13,995 ns | 1,072 ns | 4,701 ns |
| 4096 | 49,279 ns | 1,077 ns | 18,925 ns |

The last column is the finding, and it was not the one being looked for. The
suspected cost was the eviction scan, which really did look at every entry on
every store. Batching that scan to `MaxEntries/8` bought 1.3× — which meant the
scan was not the cost.

`ConcurrentDictionary.Count` reads like a field access and is not: it acquires
every lock in the dictionary, and the lock array grows with the table. `SetAsync`
called it once per store to check the bound, and at 4096 entries that single
check cost **17× the entire operation containing it**. It is now maintained
incrementally and re-derived only inside eviction.

The store is flat across the sweep afterwards, where before it grew with a bound
the caller chooses. This also corrects a claim made from `CachingBenchmarks`:
the caching layer's overhead of ~0.8 µs was measured on a cache that had never
filled, and a store into a full one was never anywhere near that.

---

### What is not measured, and why

Listed so the gaps are deliberate rather than accidental. Anything here is a
claim the project currently makes on reasoning alone.

| Not measured | The unbacked claim | Worth doing? |
|---|---|---|
| **GraphQL nested fetch vs REST N+1** | "`set(id){cards{…}}` in one round trip beats REST's one call per card." | **Yes — this is the biggest gap.** It is stated as fact in [api-info.md](api-info.md) and [architecture.md](architecture.md) and justifies the entire GraphQL layer, and nothing has timed it. Needs a live-network benchmark, which is why it has not been written: BenchmarkDotNet against someone else's free API is a poor citizen. A recorded-response harness would measure the wrong thing, since the whole claim is about round trips. |
| **`StreamAsync` per-page overhead** | Auto-pagination costs no more than the manual loop it replaces. | Low value. It is a `foreach` over the same requests; the cost is the requests. |
| **Concurrent request coalescing** | Twelve concurrent readers collapse to one fetch. | Already proven by a unit test that counts requests. A benchmark would measure the test harness's thread scheduling more than the SDK. |
| **`BoundedLru` under contention** | The cache is correct with concurrent writers. | Correctness here is checked by property tests rather than timed. A throughput number would be real, but nothing in the SDK's own use is contended enough to act on it. |
| **Image URL construction** | — | String concatenation. Measuring it would be theatre. |

The pattern worth keeping: **measure the claims that decide a design, not the
code that happens to be easy to measure.** Every entry above that says "yes" is
there because a design decision rests on it.

---

## Property-based testing

```bash
dotnet test TcgDex.CSharpSdk.Tests --filter "FullyQualifiedName~Properties"
dotnet test TcgDex.CSharpSdk.IntegrationTests --filter "FullyQualifiedName~Properties"
```

[CsCheck](https://github.com/AnthonyLloyd/CsCheck) (Apache-2.0, test-only)
generates inputs, and shrinks any failure to the smallest case that still
reproduces it.

### Why, when there are already fuzzers

They answer different questions. The fuzzers ask *does anything crash on input
the SDK did not produce*. Properties ask *does a stated invariant hold across
valid input* — a bug that returns a wrong answer without throwing is invisible to
a fuzzer and is exactly what a property catches.

### It was added because of a bug that example tests missed for months

`JsonShape` builds a union when one path holds two kinds across array elements —
`attacks[].damage` is genuinely a number on one card and a string on another. It
built that union by appending in encounter order, so the same document described
as `Number|String` or `String|Number` depending on which element came first, and
the comparison read the difference as a retype. A spurious breaking failure, in
the unattended weekly job.

Stated as a property it is one line — *the description of a document does not
depend on the order of its array elements* — and generation finds it without
anyone having thought of the case. Reintroducing the bug fails
`Describe_DoesNotDependOnElementOrder` with a counter-example shrunk in **3
shrinks out of 100 cases**, and a seed to replay it:

```
CsCheck.CsCheckException : Set seed: "7x8k2qc_O0e3" to reproduce (3 shrinks, 65 skipped, 100 total).
```

### What has properties

| Type | Properties | What they pin |
|---|---|---|
| `BoundedLru` | 4 | The bound is never exceeded; the entry just written is still there; a replace is not growth; remove-then-write leaves the count consistent. |
| `JsonShape` | 3 | Description is order-independent; comparison is reflexive; both directions notice the same drift. |

`TheEntryJustWrittenIsStillThere` is the one worth reading. Eviction once ordered
entries by a wall clock, whose resolution is coarse enough that a batch of writes
shared a timestamp — so the sort could place the entry just inserted inside the
evicted prefix, and a `Set` immediately followed by a `TryGet` missed. It
surfaced only on the fast net472 leg, where writes landed inside one tick. The
property holds it closed for every sequence rather than for the one that happened
to expose it.

### These were verified by breaking the code, not by watching them pass

A property that has never been red proves nothing, and one of these proved
exactly that. `ReplacingAnExistingKeyDoesNotEvict` passed with a replace
deliberately counted as growth — because `Count` reads the dictionary rather than
the tracked counter, so the drift is invisible until a *later* insert acts on it,
and the sequence never inserted afterwards.

That is the "wrong condition" failure: an input for which correct and broken
predict the same observation. The fix was to the input — stay under the bound,
rewrite heavily, then add one key — not to the assertion. With that, the same
manipulation fails it.

### Cost

Deliberately small. Each property runs CsCheck's default 100 cases and the whole
set adds well under a second, so it stays in the normal test run rather than
becoming a nightly job. Fuzzing is where the long budgets belong.

CsCheck ships a **net8.0 asset only**, so the properties are excluded from the
`net472` leg of the unit suite — the same shape as `PublicApiGenerator`. They are
statements about pure logic and hold identically on every target; the net472 leg
exists to execute the netstandard2.0 assembly, which they do not exercise
differently.

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

Full run, ~10 minutes:

| Outcome | Baseline | After the sweep | Now |
|---|---:|---:|---:|
| Killed + timeout | 508 | 587 | **642** |
| **Survived** | **140** | **63** | **84** |
| No coverage | 4 | 0 | 2 |
| Total mutants | 652 | 650 | 728 |
| **Mutation score** | **77.91%** | **90.03%** | **88.19%** |

### The score went down twice, for two different reasons

90.03% was the high-water mark when the mutation campaign ended. It is **88.19%**
now, across 728 mutants rather than 650, and both drops are worth separating:

- **New code arrives faster than tests for it.** The caching, pricing and
  benchmark work added 78 mutants. Optimising or extending code without
  revisiting its tests lowers the verification, and nothing announces it — which
  is why this gets re-run after any such pass, not on a schedule.
- **Part of the earlier number was never real.** See below.

The run that prompted this write-up found three survivors in new code that were
*not* equivalent: both halves of a validation message, and the default
`MaxResponseBytes`, which is a security control that nothing pinned. Those are
fixed. `TcgDexOptions` went 83.3% to 91.7%, and the total from 87.09% to 88.19%.

### The score is not deterministic, so do not quote decimals

Three consecutive full runs on identical code and identical tests returned
**89.21%**, **88.05%** and, after the fixes above, numbers that move by a point
run to run.

The cause is that **a timeout counts as killed**. Six mutants — the ones that
make `BoundedContent` size a buffer absurdly, plus a removed guard — flipped
between `Timeout` and `Survived` depending on how loaded the machine was. A
*busier* machine therefore scores *higher*, which is the opposite of any useful
signal.

So the honest figure is "around 88%", the gate is set well below at 85, and the
90.03% high-water mark recorded earlier was partly this same flattery. Quoting
this metric to two decimal places, as the rows above did, was false precision.

Line coverage did not move across any of that work — 99.77% before and after.
Every one of those newly-killed mutants was in code the suite already executed.
Coverage said the lines ran; mutation testing said whether running them proved
anything.

Per file:

| File | Baseline | Now |
|---|---:|---:|
| `Querying/CardFilter.cs` | 67% | **100%** |
| `Caching/MemoryTcgDexResponseCache.cs` | 71% | **91%** |
| `Caching/TcgDexCacheOptions.cs` | 83% | **97%** |
| `TcgDexClient.cs` | 53% | **93%** |
| `Models/CardImage.cs` | 93% | **93%** |
| `GraphQlTransport.cs` | 79% | **92%** |
| `Querying/ExpressionTranslator.cs` | 84% | **88%** |
| `Resources/Resources.cs` | 82% | **85%** |
| `Serialization/TcgPlayerPricingConverter.cs` | 77% | **81%** |
| `TcgDexOptions.cs` | — | **92%** |
| `Caching/BoundedLru.cs` | — | **96%** |
| `Caching/DeserializedResponseCache.cs` | — | **91%** |
| `Serialization/TcgDexJsonContracts.cs` | — | **73%** |
| `TcgDexTransport.cs` | 64% | **77%** |
| `TcgDexServiceCollectionExtensions.cs` | 60% | **80%** |
| `Http/BoundedContent.cs` | 63% | **74%** |
| `Caching/TcgDexCachingHandler.cs` | 65% | **70%** |

### The score went down, and that is the point of having it

90.03% was recorded when the mutation campaign ended. The run above is the first
full one since the performance work, and it is **89.21%** — 36 more mutants, 9
more survivors. Optimising code without touching its tests lowered the
verification, and nothing announced it.

Most of the new survivors are near-equivalent: `ConfigureAwait(false)` flips in
the added `await`s, and the `Content-Length` capacity hint in `BoundedContent`,
whose whole purpose is to change an allocation count rather than a result — a
mutation there is invisible by construction.

**One was not equivalent, and it was in the security guard.** `BoundedContent`
enforces `MaxResponseBytes` while reading by checking `buffered.Length + read >
maxBytes` before each write. Stryker turned that addition into a subtraction and
every one of the 450 tests passed.

Checking it by hand rather than filing it as equivalent is what made it useful.
The existing test sends 68 KB against a 32 KB limit, and under the mutation the
*final partial chunk* is small enough that `length - read` clears the ceiling
anyway — so it still throws, and still says "exceeded", for the wrong reason.
The mutant only survives for a body modestly over the limit: **40,000 bytes
sails past a 32,768-byte ceiling untouched**. Which is the size that matters,
because a decompression bomb does not have to be enormous to be over budget.
`Rest_AnUndeclaredLengthOneByteOver_IsRejected` now covers it.

The lesson is about when to run this. A mutation score is not a certificate
earned once. It decays exactly when code changes and tests do not — which is
precisely what an optimisation pass is.

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

### What the remaining 84 are

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
- **Optimisations whose only effect is an allocation count.** The
  `Content-Length` capacity hint decides how large a buffer starts, not what
  ends up in it, so no assertion about a *result* can ever see the difference.
  Four of `BoundedContent`'s eight survivors are this.

The LRU eviction tie-break used to be listed here as a non-deterministic
survivor that depended on dictionary ordering. It is gone: the eviction was
rewritten to take a consistent snapshot, and that file is now at 100%. Worth
noting because it is the good outcome — an "equivalent" mutant that stopped
being one after the design around it changed.

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

---

## Fuzzing

The SDK consumes untrusted input in exactly one place: a response body from a
server it does not control, over a `BaseAddress` the caller is explicitly
allowed to repoint at a mirror. Two layers cover it, because they answer the
same question at different depths and only one can afford to run per push.

### Every push: `MalformedResponseTests`

Corrupts the recorded fixtures mechanically — truncation at every tenth,
single-bit flips, injected structural bytes, 2000-deep nesting, invalid UTF-8
inside a string — and asserts one property:

> The SDK returns a value or throws `TcgDexApiException`. Never anything else.

That is the contract a consumer wraps in a single `catch`. An
`IndexOutOfRangeException` arriving from the network is something they cannot
defend against, because it comes from someone else's server rather than their
own code.

Seeded rather than random, so a failure names a reproducible case instead of
being a one-off nobody can re-run. Verified in the failing direction before
being trusted: rewriting the transport's wrapping throw to leak an
`InvalidOperationException` fails it on 300+ named cases.

### Weekly: coverage-guided fuzzing across seven modes

The harness multiplexes on the first byte of the input, so one process covers
every path that consumes input the SDK did not produce. libFuzzer prefers a
narrow target and seven executables would be the textbook answer — it would also
mean seven projects, seven corpora, and a fixed budget divided seven ways. The
selector is just another input byte, and coverage feedback teaches the fuzzer to
exercise each branch.

| Mode | Reaches |
|---|---|
| Card | The richest model, and the only path through both hand-written converters |
| Card list | Collection handling and the coalescing backing fields |
| Set | A different model graph: card counts, abbreviations, boosters |
| Enumeration | Bare JSON arrays of strings and integers |
| Problem details | The error path, which runs when something has already gone wrong |
| GraphQL | A separate transport with its own envelope |
| Query building | Not a response at all — caller-supplied text on its way into a URL |


```bash
gh workflow run fuzz.yml -f seconds=300
```

SharpFuzz instruments the SDK assembly and libFuzzer explores from a corpus
seeded with the recorded responses. Seeding is what makes it work — given random
bytes, a fuzzer spends its entire budget rediscovering that JSON starts with a
brace.

Widening the harness and fixing the seeding are worth 2.3x the coverage,
measured over 120 seconds locally:

| Harness | Features at init | Features at end |
|---|---:|---:|
| One mode (card fetch only) | 641 | 1,757 |
| Seven modes, fixtures seeded raw | 1,196 | 2,705 |
| **Seven modes, seeded per mode** | **3,085** | **3,990** |

**The seeding bug is the interesting one.** Every recorded response starts with
`{` or `[`, and `123 % 7 = 4` while `91 % 7 = 0` — so the raw fixtures only ever
seeded two of the seven modes, and the fuzzer had to discover the rest by
mutating the selector byte. Prefixing each fixture with each mode byte starts
the run *above where the mis-seeded one finished*.

No crashes in any run.

The most recent CI run, over 180 seconds, and the first to restore a cached
corpus rather than start from seeds alone:

| | |
|---|---|
| Corpus restored | **397** from cache, seeded up to 516 |
| Executions | 1,819,735 at ~10,050/s |
| Features | 3,111 → **4,162** |
| Minimised to | **396** inputs, no coverage lost |
| Crashes | **none** |

**Read `cov: 8` in the libFuzzer output as normal, not broken.** That counts
edges in the tiny native `libfuzzer-dotnet` shim. The .NET signal arrives as
`ft:` — features from the shared-memory bitmap SharpFuzz fills — and the proof
the instrumentation is live is that **`ft:` climbs and the corpus grows**.
Without coverage feedback neither moves.

A crash is written to `findings/` as the exact bytes that caused it, which makes
it a regression fixture rather than a bug report.

### Running it locally

The toolchain is Linux-first, so on Windows this needs WSL. **Everything below
has been run end to end on Ubuntu 26.04 under WSL2** — around 8,800 executions
per second against CI's 10,000, close enough that a local run is a real check
rather than a smoke test.

Setup, once:

```bash
# clang is the only step needing root. Run apt-get update first: a stale index
# reports a candidate version that cannot then be fetched, which reads as a
# broken mirror and is not one.
sudo apt-get update && sudo apt-get install --yes clang

# Export DOTNET_ROOT when .NET came from dotnet-install.sh rather than a
# package, or `sharpfuzz` fails with "Download the .NET runtime" — its apphost
# looks for a system install and does not find ~/.dotnet.
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"

curl -sSL -o libfuzzer-dotnet.cc \
  https://raw.githubusercontent.com/Metalnem/libfuzzer-dotnet/master/libfuzzer-dotnet.cc
clang -fsanitize=fuzzer libfuzzer-dotnet.cc -o libfuzzer-dotnet

dotnet tool install --global SharpFuzz.CommandLine
```

Then per run:

```bash
dotnet publish TcgDex.CSharpSdk.Fuzz -c Release -o ~/fz/out
sharpfuzz ~/fz/out/TcgDex.CSharpSdk.dll

# One seed per fixture per mode, for the reason above. Without this most of the
# harness goes unreached.
mkdir -p ~/fz/corpus ~/fz/findings
for fixture in ~/fz/out/corpus/*.json; do
  name=$(basename "$fixture" .json)
  for mode in 0 1 2 3 4 5 6; do
    printf "$(printf '\\%03o' "$mode")" | cat - "$fixture" > ~/fz/corpus/"$name-m$mode.bin"
  done
done

cd ~/fz && ~/libfuzzer-dotnet --target_path=$HOME/fz/out/TcgDex.CSharpSdk.Fuzz \
  -max_total_time=300 -artifact_prefix=findings/ -print_final_stats=1 corpus
```

Three things that cost time here:

- **`sharpfuzz` rewrites the assembly in place**, so successful instrumentation
  is visible as size growth — **377,856 to 582,144 bytes**. It is also *per
  build*: a fresh `dotnet publish` silently un-instruments the assembly, and the
  fuzzer will then run at full speed and find nothing. Re-run `sharpfuzz` after
  every publish.
- **Working directory does not matter.** An earlier version of this page claimed
  running from the WSL filesystem rather than `/mnt/c` would roughly double
  throughput. Measured, it does nothing: 7,321 exec/s from `/tmp` against 7,751
  and 7,254 from `~`. The fuzz loop never touches `/mnt/c` — only the build
  does. The remaining gap to CI is not filesystem, and has not been diagnosed.
- **`/tmp` does not survive.** WSL shuts down when idle and clears it, so a
  setup left there will be gone by the next session. Use `~`.

`libfuzzer-dotnet` is built from source rather than downloaded prebuilt, which
is the same supply-chain position this repository takes for its dependencies.

### The corpus is cached, not committed

The corpus is the fuzzer's memory: an input is kept only because it reached code
no earlier input reached. Restarting from the 17 recorded responses every week
would mean spending most of each budget rediscovering what the last run already
found — the first run climbed 641 features to 1,757 and grew 17 inputs to 496.

So the workflow restores it from `actions/cache` and saves it again, and runs
`-merge=1` afterwards to keep the smallest set that preserves the same coverage.
Committing it instead would put 1.3 MB in the repository that grows every week,
for a file nobody reads. Minimisation is skipped when the fuzz step failed: a
crash means there is evidence to collect, and rewriting the corpus first would
be tidying it away.

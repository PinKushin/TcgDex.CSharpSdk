# Code Coverage

**Goal: reach and hold ~100% line coverage of hand-written SDK code.**

**Current (2026-10-15, all three target frameworks merged): 99.81% line (1546/1549) and 96.53%
branch (584/605).** Both are gated in CI across every target framework since 0.6.3. The earlier
figures below were measured on net10.0 alone. The 3 uncovered lines on this run, as
`Check-Coverage.ps1` names them: `ExpressionTranslator.cs` 293–294 (provably unreachable — see
[Why not 100%](#why-not-100) below) and `TcgDexCachingHandler.cs` 272 (the coalesced waiter's
cancellation on the `netstandard2.0` build). `BoundedLru.cs` 175 (a lost-race retry) is
timing-dependent: covered on this run, uncovered on some others.

| | Line | Branch | Unit tests | Integration tests |
|---|---|---|---|---|
| Baseline (`f45e496`) | 83.2% | — | 113 | 22 |
| Now | **99.74%** | **96.36%** | **562** (net10.0) | **149** |

The unit tests run on `net472`, `net8.0` and `net10.0` — 1679 executions (562,
561 and 556 per framework). Coverage is collected from all three runs and
merged: the `net472` run produces its own cobertura file, so code compiled
solely for `netstandard2.0` is measured by the gate too.

---

## Measure it

```bash
dotnet test TcgDex.CSharpSdk.Tests/TcgDex.CSharpSdk.Tests.csproj \
  --collect:"XPlat Code Coverage" \
  --settings coverlet.runsettings \
  --results-directory ./TestResults
```

Output lands at `TestResults/<guid>/coverage.cobertura.xml`.

For a readable report:

```bash
dotnet tool install --global dotnet-reportgenerator-globaltool
reportgenerator -reports:"TestResults/**/coverage.cobertura.xml" -targetdir:"TestResults/report" -reporttypes:Html
```

## Always pass `--settings coverlet.runsettings`

Without it the number is meaningless. `System.Text.Json`'s source generator
emits several thousand lines of `*.g.cs`, which swamp the ~550 lines of
hand-written SDK code:

| Measured | Result |
|---|---|
| Everything, including `*.g.cs` | **78.8%** |
| Hand-written code only | **83.2%** |

The generated lines belong to Microsoft's generator, are exercised indirectly by
every model test, and cannot be deliberately driven to 100%. Counting them puts
the target permanently out of reach for a reason that has nothing to do with
test quality. `coverlet.runsettings` excludes them by attribute and by path.

---

## Why not 100%

Two lines: the `ReadMember` fallback in `ExpressionTranslator.cs` (293–294),
which throws when a `MemberExpression` carries a member that is neither a field
nor a property.

That is **provably** unreachable rather than merely hard to reach.
`Expression.MakeMemberAccess` rejects any other member kind with an
`ArgumentException`, so no expression tree — whether written in C# or built by
hand — can carry one. Verified directly rather than assumed.

The fallback stays because removing it makes the switch non-exhaustive, and the
compiler then requires *some* default anyway. A clear exception beats whatever
the alternative would be.

### The other defensive branches are tested, not excused

Six lines that were previously written off as unreachable turned out not to be.
They were unreachable only through `CardQuery`, whose model happens to have no
boolean property and no custom methods — an accident of the current model, not a
property of the translator.

`TranslatorDefensiveTests` drives the internal `ExpressionTranslator` with a
synthetic model that has those shapes, covering:

- an `||` operand that is neither a comparison nor a method call
- bitwise `&` and `^` where a comparison was expected
- an unmapped one-argument method on a property, which looks exactly like
  `Contains` to a shape check
- a relational comparison against `null`, built by hand because C# rejects it as
  always-false
- a method call or array index used as the compared value

This matters beyond the number: those paths are what a future `SetQuery` or
`SerieQuery` would hit, and they now have asserted behaviour rather than an
assumption.

**Two things were deleted rather than tested**, because they turned out to be
genuinely dead:

- The `JsonTokenType.Null` branches in both converters. `JsonConverter<T>.HandleNull`
  defaults to `false`, so System.Text.Json handles null itself and never invokes
  a converter for one. Tests passed while the lines stayed dark, which is what
  exposed it.
- The non-`PropertyName` guard in `TcgPlayerPricingConverter`. `Utf8JsonReader`
  guarantees a property name there; malformed JSON fails inside the reader first.

That is the useful part of chasing coverage: it does not just add tests, it
finds code that cannot run.

---

## What closing the gap actually covered

For reference, since these are the areas worth keeping covered as the SDK grows:

- **Transport failures** — network errors, timeouts, 5xx, unparseable error
  bodies, empty bodies, caller cancellation, on both REST and GraphQL. These
  went from mostly dark to fully covered, and they are the paths a user hits
  when something breaks.
- **Query rejections** — every `NotSupportedException`, asserting the message
  names something actionable rather than just failing.
- **Serialization** — round-tripping cards, sets, series and the dynamic
  TCGplayer printing keys, which also made writing a supported feature rather
  than an accident.
- **Every resource method** — all 13 catalog endpoints and all 3 random
  endpoints, each asserting its exact request URI. The hyphenated paths
  (`energy-types`, `regulation-marks`, `dex-ids`) are trivially mistyped as
  camelCase and would only fail at runtime.

## How it is held

Coverage that is measured but not enforced drifts, so CI gates on it:

```yaml
- name: Coverage threshold
  shell: pwsh
  run: ./scripts/Check-Coverage.ps1 -ResultsDirectory ./TestResults -Threshold 99.5 -BranchThreshold 95
```

Run the identical check locally:

```bash
dotnet test TcgDex.CSharpSdk.Tests/TcgDex.CSharpSdk.Tests.csproj   --collect:"XPlat Code Coverage" --settings coverlet.runsettings   --results-directory ./TestResults

pwsh ./scripts/Check-Coverage.ps1
```

### Why a script rather than a build property

`coverlet.msbuild` can enforce a threshold with `/p:Threshold=…`, but this repo
uses the **XPlat collector**, which produces the report and cannot gate on it.
Switching packages to get the gate would mean giving up the collector's cleaner
integration with `dotnet test`. A separate pass over the Cobertura output keeps
both, and prints a per-file breakdown so a failure names the file that regressed
rather than only reporting that the total moved.

The script excludes generated files exactly as the runsettings does, so the gate
measures the same thing the report does.

### Line and branch are gated separately

They answer different questions:

| Metric | Question |
|---|---|
| **Line** | Did this line run? |
| **Block** (what Visual Studio reports) | Did this straight-line chunk run? |
| **Branch** | Did we test **both outcomes** of this condition? |

The first two ask "did it execute". Branch asks something categorically
stronger. A line holding `flipped ? a : b` is fully line-covered and fully
block-covered the first time it runs — while half its behaviour has never been
exercised.

That is not hypothetical here. Line coverage sat at 99.76% while branch coverage
was 91.90%, and the gap included the operand-flipping in
`ExpressionTranslator`: `100 <= c.HitPoints` could have emitted the wrong operator with
the whole suite green. Closing that gap is what took branch coverage to 96.06%.

The branch gate is **95%**. The remaining 17 partial branches are the low-value
kind — `?? new TcgDexOptions()` defaults, `?? throw` guards made unreachable by
an earlier check, and `TryParse` failure paths on data the SDK itself stored in
a valid form. Testing those proves the `??` operator works.

### Why 99.5 and not 100

The ceiling is **99.87%** — the two provably unreachable lines are 0.13% of the
1482-line total. A gate at 100 could never pass.

99.5 sits just under the ceiling, leaving those two lines of headroom and
essentially nothing else. That is deliberate: it was 98 while the unreachable set
was larger, and it moved up once tests closed the gap. The gate is a ratchet, not
a target.

**Do not lower it to make a build pass.** If a line is genuinely unreachable,
prove it and record why here — as the two above are — rather than moving the
number.

**Do not lower the gate to make a build pass.** If a line is genuinely
unreachable, record why here — as the ones below are — rather than moving the
number.

## Verifying the integration without hitting the API

Nearly all of it already is offline. The unit suite does not test mocks of the
SDK's own code — it deserializes **recorded live responses** through the SDK's
own serializer context, and asserts the exact request URI of every call. So
"does the SDK integrate correctly" is answered without a network call, which is
why the offline suite is far larger than the live one.

The one thing offline tests structurally cannot do is notice when **TCGdex**
changes. A frozen recording never disagrees with itself, so a renamed field
would leave every offline test green while the SDK silently broke.

`FixtureDriftTests` closes that. It re-fetches each recording and compares the
response **shape** — key paths and types, not values — failing with a precise
message such as `removed: 'set.cardCount.official' was Number, now absent`.

Shape rather than bytes, deliberately: refreshing the fixtures today changes 7
of 16 files byte-for-byte and **none** of them in shape, because prices and
`updated` timestamps move constantly. A byte-diff check would fail daily and
teach everyone to ignore it.

Removals and type changes fail the run; new fields are reported without failing,
since the API growing a field is worth knowing but is not a breakage.

`scripts/Update-Fixtures.ps1` refreshes the recordings — to be run *after*
adjusting the SDK to a reported change, never before, since a refresh makes the
check pass whether or not the models were updated.

## Surviving mutants written off

DECISIONS #19 item 2 accepts a surviving mutant only if it is written down with a reason. This is
that list for the four weakest files, which were at 77% (`TcgDexCachingHandler.cs`), 74%
(`TcgDexTransport.cs`), 62% (`BoundedContent.cs`) and 60% (`RequestBudget.cs`) in GitHub Actions
run 39020678854 (commit `ddde9bd`). Every other survivor in that run now has a killing test. Each
test was checked by hand: apply that exact mutant, watch the test fail, then restore the code.
If every write-off below is correct, the scores rise to 94%, 89%, 90% and 100%.
This is a projection from the per-mutant checks. No Stryker run measured it, because
local runs are limited to an 8000-character span.

Line numbers are those of `ddde9bd`, as printed in the report, except the `TcgDexTransport.cs`
rows. Those are renumbered to `8ffde05`, where run 39177696453 reported the same eight mutants.

| File:line | Mutator | Why it is not killed |
|---|---|---|
| `TcgDexCachingHandler.cs:110` | Statement (guard removed) | Unreachable. The handler is sealed, and `HttpMessageInvoker` and `DelegatingHandler` both reject a null request before this override runs. |
| `TcgDexCachingHandler.cs:162` | Boolean (`ConfigureAwait(true)`) | Untestable without a seam: not equivalent. Usually the earlier `ConfigureAwait(false)` in `WaitForLeaderAsync` has already left the caller's context. But a waiter that calls `GetOrAdd` after the leader's `SetResult` and before its `TryRemove` gets a completed task, nothing suspends, and this await captures the context. That window is a race no deterministic test can hold open. |
| `TcgDexCachingHandler.cs:210` | Conditional (false) | Equivalent. `FetchResult.FromCache(null)` and `FetchResult.None` are the same `default` value. |
| `TcgDexCachingHandler.cs:288` | Logical (`\|\|` to `&&`) | Equivalent. An empty tag on either side still fails `EntityTagHeaderValue.TryParse` below, so the result is `false` either way. |
| `TcgDexCachingHandler.cs:320` | Logical (`\|\|` to `&&`) | Equivalent. When nothing was added, `Remove(null)` does nothing. When the added tag has already left the header, the loop removes the caller's equal tags and adds them back. The only change is their order within `If-None-Match`, which is a set. All 109 caching tests pass against it. |
| `TcgDexCachingHandler.cs:466` | Conditional (false) | Equivalent. Copying the segment every time stores the same bytes and only costs an allocation. |
| `BoundedContent.cs:133` | Equality (`hint >= 0`) | Equivalent. A hint of 0 gives `Math.Min(0, cap)`, which is 0, the same capacity as the original. |
| `BoundedContent.cs:145` | Conditional (true) | Equivalent. When capacity is 0, `new MemoryStream(0)` behaves the same as `new MemoryStream()`. |
| `BoundedContent.cs:145` | Equality (`capacity >= 0`) | Equivalent, for the same reason: it differs from the original only at 0. |
| `BoundedContent.cs:198` | Statement (`ArrayPool.Return` removed) | Not observable. The pool allocates a fresh array when one is not returned, so the only cost is allocation. `ArrayPool.Shared` caches per thread and per core, so asserting that an array is reused would be nondeterministic. |
| `TcgDexTransport.cs:420` | Arithmetic (`*` to `/`) | Untestable without a clock seam: not equivalent. On Linux, where Stryker runs on CI, `Stopwatch.Frequency` (10⁹) makes the factor 10⁷/10⁹ = 0.01, so `/` divides by it instead of multiplying and logs a duration 10,000 times too large. Only on Windows is the factor 1. The value is logged only, and no test can bound it exactly without injecting the clock. `TheLoggedDuration_IsAPlausibleNumberOfMilliseconds` still catches a gross scale error. |
| `TcgDexTransport.cs:439` | Conditional (false) | Equivalent. A blank body sent to the deserializer throws `JsonException`. That is caught at line 443 and returns the same `null`, so `IsBlank` is only a fast path. |
| `TcgDexTransport.cs:444` | Block removal | Equivalent. Removing the block makes it return `default`, which is the same `null`. |
| `TcgDexTransport.cs:452` | Block removal | Equivalent, for the same reason: it returns `default`, which is `null`. |
| `TcgDexTransport.cs:466` | Logical (three variants) | Equivalent. When fewer whitespace bytes count as blank, those bodies go to the deserializer instead. It throws `JsonException`, which becomes the same `null`. |
| `TcgDexTransport.cs:472` | Boolean (`true` to `false`) | Equivalent, for the same reason as line 466. |

`RequestBudget.cs` has no write-offs. Both of its survivors are now killed.

### Second round

GitHub Actions run 39057672321 (branch `test/mutation-weak-files`, merged as `63f02f8`) left 27
undetected mutants in eight files. Thirteen now have a killing test, checked by hand the same way.
`TcgDexTransport.cs` still had the eight survivors written off above, then at the same line numbers
(renumbered since), so it gets no new rows. The five rows below are the rest. Projected scores, if they are correct:
`ITcgDexResponseCache.cs` 100% (from 50%), `FlexibleStringConverter.cs` 100% (80%),
`TcgDexJsonContracts.cs` 100% (81%), `TcgDexServiceCollectionExtensions.cs` 100% (81%),
`TcgDexOptions.cs` 100% (86%), `TcgPlayerPricingConverter.cs` 89% (33 of 37, from 81%),
`TcgDexUserAgent.cs` 83% (unchanged, 5 of 6) and `TcgDexTransport.cs` 89% (unchanged, 67 of 75).

Line numbers are those of `63f02f8`.

| File:line | Mutator | Why it is not killed |
|---|---|---|
| `TcgPlayerPricingConverter.cs:62` | Conditional (false) | Equivalent. It also decodes the name for `unit` and `updated`, but both branches `continue` before `name` is read, and `GetString` cannot fail on a name `ValueTextEquals` has just matched. The only cost is an allocation. |
| `TcgPlayerPricingConverter.cs:62` | Logical (`\|\|` to `&&`) | Equivalent. `isUpdated` is computed as `!isUnit && …`, so the two are never both true. `&&` is always false, which is the conditional-false mutant above. |
| `TcgPlayerPricingConverter.cs:68` | Conditional (false) | Equivalent. `GetString()` returns `null` on a `Null` token, the same value the branch supplies. |
| `TcgPlayerPricingConverter.cs:84` | Statement (`continue` removed) | Equivalent. `JsonSerializer.Deserialize` on a `Null` token returns `null` for the reference type `TcgPlayerPrice`, and the `price is not null` check below then skips it, as `continue` did. |
| `TcgDexUserAgent.cs:71` | String (`"unknown"` to `""`), NoCoverage | Untestable without a seam: not equivalent. If reached, the header would carry no version. It is reached only when `Assembly.GetName().Version` is `null`, which happens for a dynamically emitted assembly and never for this compiled one, and `BuildProduct` is a private static initialiser. |

### Third round: files below their best score

GitHub Actions run 39177696453 on `8ffde05` (0.8.0) was compared, file by file, with the best score
each file had ever reached, counting only kills by the hermetic unit tests. Kills by the live-API
integration tests do not count, because those tests are not run on every push. Six files were below
their best. Every survivor in them now has a killing test, checked by hand as above, or a row below.
The exception is `TcgDexTransport.cs`, whose eight remaining survivors are the ones already written
off in the first table, renumbered there.

Line numbers are those of `8ffde05`.

| File:line | Mutator | Why it is not killed |
|---|---|---|
| `CardImage.cs:63` | Logical (`\|\|` to `&&`) | Equivalent. Under the mutant a blank non-null base URL goes on to build `"{blank}/high.png"`, and `Absolute` rejects it because it has no http(s) scheme, so the result is the same `null`. The `is null` test cannot be removed instead: the netstandard2.0 reference assembly does not annotate `string.IsNullOrWhiteSpace`, and without the test that target fails to build with CS8602. `ImageUrl_WithAWhitespaceBase_IsNull` runs the whitespace input and pins the `null`. |
| `CardImage.cs:91` | Logical (`\|\|` to `&&`) | Equivalent, for the same reason: `"{blank}.png"` has no http(s) scheme, so `Absolute` returns `null`. |
| `ExpressionTranslator.cs:340` | Statement (`throw;` removed), NoCoverage | Unreachable. `ExceptionDispatchInfo.Capture(...).Throw()` on the line before always throws. The `throw;` exists only because the compiler's definite-return analysis cannot see that, and `[DoesNotReturn]` affects nullable flow analysis, not reachability. |
| `ExpressionTranslator.cs:386` | String (message to `$""`), NoCoverage | Unreachable. `ReadMember` is only called with a `MemberExpression.Member`. `MemberExpression` has no public constructor, and `Expression.Field`, `Expression.Property` and `Expression.MakeMemberAccess` reject any member that is not a field or a property, so the fallback arm has no input. |
| `MemoryTcgDexResponseCache.cs:39` | Statement (guard removed) | Equivalent. The `BoundedLru` constructor, called two lines later, runs the same `Guard.NotLessThan(maxEntries, 1)`. Its parameter is also named `maxEntries`, so the exception has the same type, message and `ParamName`. The guard stays because this constructor is public, and its contract should not depend on a private field's constructor. |
| `MemoryTcgDexResponseCache.cs:101` | Equality (`>` to `>=`) | Equivalent. The two differ only when the time-to-live is exactly `TimeSpan.MaxValue.Ticks / 12`, and then by 7 ticks. Both lifetimes are about 9.2×10¹⁸ ticks, which is longer than the whole `DateTimeOffset` range (about 3.2×10¹⁸), so line 107 clamps both to `DateTimeOffset.MaxValue`. |
| `MemoryTcgDexResponseCache.cs:107` | Equality (`>` to `>=`) | Equivalent. At the boundary `now + absoluteLifetime` is exactly `DateTimeOffset.MaxValue`, the value the clamp returns. |

### Fourth round: the rest of the survivors, read one by one

Mutation run 39189935172 (branch `fix/mutation-regressions`) left 51 survivors. Those in files that
were not below their best had not been examined, so each was read. Fourteen now have a killing test,
checked by hand: the two writer guards in `ReleaseDateConverter` and `TimestampConverter`, the null
`variants_detailed` coalescing in `Card`, the `pageSize` guard and the cancellation check in
`StreamAsync`, the `BoundedLru` bound, the tagless-entry slot in `DeserializedResponseCache`, the
GraphQL error log, the `'9'` boundary in `Attack.BaseDamage`, and the five `ConfigureAwait` calls in
`GraphQLTransport` and `StreamAsync`. The four below cannot be killed.

| File:line | Mutator | Why it is not killed |
|---|---|---|
| `CardQuery.cs:178` | Statement (guard removed) | Equivalent. `ExpressionTranslator.SortFieldName`, called on the next line, runs the same `Guard.NotNull(selector)` and the parameter has the same name, so the exception has the same type, message and `ParamName`. `SortFieldName_ANullSelector_ThrowsNamingTheParameter` pins it. The guard stays because `OrderBy` is public and its contract should not depend on a helper. |
| `BoundedLru.cs:167` | Statement (`return;` removed) | Equivalent. After a successful `TryAdd` the code falls through to `TryGetValue` and `TryUpdate` of the entry it just added, which succeed with the same value and return. It would differ only if `Evict` removed that very key in between, and `Evict` removes the oldest entry, never the one just stored. |
| `TcgDexCacheOptions.cs:135` | Equality (`cards >= 0` to `cards > 0`) | Equivalent. The two differ only when the path begins with `/cards/` at index 0. Every request path starts with the language segment (`/v2/en/cards/...`), and an empty language is rejected, so index 0 has no input. |
| `CardDiagnostics.cs:44` | Statement (`return;` removed) | Equivalent. With warnings disabled the walk would run, and every log call it makes is filtered by the generated logger's own `IsEnabled` check. The early return only saves the walk, which nothing observable depends on. |

## What coverage does not tell you

100% line coverage means every line ran, not that behaviour is correct. A suite
that exercises every line of an HTTP client while never asserting a single
request URL will sit at 100% and still let a wrong endpoint ship.

The tests that caught real defects here were not the ones chasing lines — they
were the ones asserting exact URLs, deserializing recorded payloads with
irregular shapes, and mutation-checking that a test could actually fail. Chase
the gap, but do not mistake the number for the goal.

See [`learnings.md`](learnings.md) for the specific defects that motivated each
of those practices.

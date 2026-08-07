# Code Coverage

**Goal: reach and hold ~100% line coverage of hand-written SDK code.**

**Current: 99.77% line (882/884) and 96.60% branch (455/471).** Both are gated in
CI. The 2 uncovered lines are provably unreachable — see [Why not 100%](#why-not-100) below.

| | Line | Branch | Unit tests | Integration tests |
|---|---|---|---|---|
| Baseline (`f45e496`) | 83.2% | — | 113 | 22 |
| Now | **99.77%** | **96.60%** | **422** | **149** |

The unit tests run on `net472`, `net8.0` and `net10.0` — 1266 executions of 422
tests. Coverage is collected from the `net8.0` and `net10.0` runs only: coverlet
does not instrument the .NET Framework run, so **code compiled solely for
`netstandard2.0` is invisible to the gate**. That is why such code is kept to a
minimum and why `HttpContentExtensions` is scoped with `#if NETSTANDARD2_0`
rather than shipped on every target, where it would be unreachable and would
read as an uncoverable hole.

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
`ExpressionTranslator`: `100 <= c.Hp` could have emitted the wrong operator with
the whole suite green. Closing that gap is what took branch coverage to 96.06%.

The branch gate is **95%**. The remaining 17 partial branches are the low-value
kind — `?? new TcgDexOptions()` defaults, `?? throw` guards made unreachable by
an earlier check, and `TryParse` failure paths on data the SDK itself stored in
a valid form. Testing those proves the `??` operator works.

### Why 99.5 and not 100

The ceiling is **99.76%** — the two provably unreachable lines are 0.24% of the
total. A gate at 100 could never pass.

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

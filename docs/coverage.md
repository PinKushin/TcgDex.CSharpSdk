# Code Coverage

**Goal: reach and hold ~100% line coverage of hand-written SDK code.**

**Current: 98.9% (699/707 lines).** 8 uncovered lines remain, all in one file and
all unreachable by construction — see [Why not 100%](#why-not-100) below.

| | Coverage | Uncovered | Unit tests | Integration tests |
|---|---|---|---|---|
| Baseline (`f45e496`) | 83.2% | 93 lines / 10 files | 113 | 22 |
| Now | **98.9%** | 8 lines / 1 file | **249** | **120** |

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

The 8 remaining lines are all `_ => throw Unsupported(node)` switch defaults in
`ExpressionTranslator.cs` (lines 90, 137, 149, 203, 284, 293, 294, 321).

They are unreachable **by construction**: the C# compiler cannot produce those
node shapes inside a valid `Expression<Func<Card, bool>>`. A predicate that
would reach them does not compile in the first place.

They are kept rather than deleted because removing them breaks switch
exhaustiveness and would turn a future unhandled node type into a silent wrong
answer instead of a clear exception. That trade — 8 defensive lines against a
class of silent misbehaviour — is the right one.

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
  run: ./scripts/Check-Coverage.ps1 -ResultsDirectory ./TestResults -Threshold 98
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

### Why 98 and not 100

The ceiling is **99.0%** — the 8 unreachable lines below are 0.98% of the total,
and no test can reach them. A gate at 100 could never pass; a gate at 99.0 would
sit exactly on the current value and break the moment a single defensive branch
is added anywhere.

98 leaves room for that while still failing on any real regression: dropping a
genuinely tested file to zero would cost far more than two points. Raise it if
the unreachable set ever shrinks.

**Do not lower the gate to make a build pass.** If a line is genuinely
unreachable, record why here — as the ones below are — rather than moving the
number.

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

# Code Coverage

**Goal: reach and hold ~100% line coverage of hand-written SDK code.**

Baseline measured 2026-08-05 at commit `f45e496`: **83.2% (460/553 lines)**, with
**93 uncovered lines across 10 files**. Eleven files are already at 100%.

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

## The remaining gap

93 lines. Roughly two thirds are error and edge paths that need a test to
*provoke* the condition rather than assert a happy path.

| File | Covered | Uncovered lines | What is missing |
|---|---|---|---|
| `FlexibleStringConverter.cs` | 44.4% | 25, 28–31, 42, 44, 46, 50, 52 | The `Write` path is never exercised (nothing serializes a Card yet), plus `true`/`false`/unexpected-token reads |
| `Resources.cs` | 59.5% | 68, 79, 90, 93, 109–142 | Most `Catalog` methods and `Random.SetAsync`/`SerieAsync` have no unit test — only the few asserted in `ClientTests` |
| `TcgDexApiException.cs` | 63.6% | 20, 22, 35, 37 | The parameterless and message-only constructors are never constructed |
| `TcgPlayerPricingConverter.cs` | 64.3% | 27, 32, 43, 91–113 | The whole `Write` path, plus null-pricing and malformed-token branches |
| `Variants.cs` | 66.7% | 52 | `DetailedVariant.Stamp` null-coalescing guard |
| `TcgDexOptions.cs` | 70.0% | 51–53 | The non-absolute `BaseAddress` validation branch |
| `ExpressionTranslator.cs` | 80.0% | 48–49, 81, 90, 116–118, 137, 149, 183, 191–215, 278–303 | Rejection paths: mismatched OR operators, unsupported method calls, non-property members, static member reads, unreadable members |
| `GraphQlTransport.cs` | 81.8% | 98, 167–185 | Non-success status, `HttpRequestException`, `JsonException`, timeout; and the empty-`data` return |
| `CardFilter.cs` | 90.5% | 144–147 | The `\n`, `\r`, `\t` and backslash escape branches |
| `TcgDexTransport.cs` | 91.4% | 111, 113, 119, 167, 171 | `GetRequiredAsync` null path, timeout branch, unparseable problem body |

Already at 100%: `Card`, `CardBrief`, `CardSet`, `Serie`, `Pricing`, `Attack`,
`Ability`, `Booster`, `Legality`, `WeaknessOrResistance`, `TcgDexProblem`,
`CardQuery`, `QueryFilter`, `TcgDexClient`, `TcgDexLanguages`, `GraphQlMessages`.

---

## How to close it

Ordered by value, not by line count.

1. **Error and cancellation paths on both transports.** `RecordingHandler`
   already supports a response factory, so a handler that throws
   `HttpRequestException` or `TaskCanceledException` covers most of
   `GraphQlTransport` and `TcgDexTransport` in a handful of tests. These are the
   paths users hit when something goes wrong, so they are the worst ones to
   leave untested.
2. **`ExpressionTranslator` rejection paths.** Each is a documented
   `NotSupportedException` with a specific message. Every one deserves a test
   asserting *which* message comes back — an unhelpful rejection message is a
   real defect for a query builder.
3. **The `Write` paths on both converters.** Nothing in the SDK serializes a
   Card today, which is why they are dark. Either cover them with round-trip
   tests, or decide serialization is out of scope and delete them — untested
   code that no caller reaches is worse than absent code.
4. **The remaining `Catalog` and `Random` methods.** Mechanical: each is one
   test asserting the request URI, following the pattern already in
   `ClientTests`.
5. **Small guards** — `TcgDexOptions` absolute-URI branch, `CardFilter` escape
   branches, `DetailedVariant.Stamp`.

## How to hold it

Coverage that is measured but not enforced drifts. Once the gap is closed:

- Add a threshold to the CI unit-test step so a drop fails the build:
  `/p:ThresholdType=line /p:Threshold=100 /p:ThresholdStat=total`.
- Keep the threshold on **hand-written code only**, via the runsettings above.
- Treat a deliberate exclusion as a code review decision: `[ExcludeFromCodeCoverage]`
  with a comment explaining why, not a silent drop in the number.

## What coverage does not tell you

100% line coverage means every line ran, not that behaviour is correct. The
previous version of this SDK had a passing suite that never once asserted a
request URL, which is how it shipped a `?q=` parameter the API does not have.

The tests that caught real defects here were not the ones chasing lines — they
were the ones asserting exact URLs, deserializing recorded payloads with
irregular shapes, and mutation-checking that a test could actually fail. Chase
the gap, but do not mistake the number for the goal.

See [`learnings.md`](learnings.md) for the specific defects that motivated each
of those practices.

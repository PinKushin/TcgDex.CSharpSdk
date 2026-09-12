# Contributing

This is the practical, day-to-day guide: environment, the gate to run before opening a PR, and
the mechanics of getting a change merged. **For how the SDK is designed and how to extend it**
(adding an endpoint, a model, a filter operator), see [`docs/architecture.md`](docs/architecture.md)
— that page owns the design conventions; this one owns the workflow around them, and neither
repeats the other.

## Before you start

**Open an issue for anything beyond a small fix.** This repo has an unusually opinionated design
history — a full from-scratch rewrite, a shipped feature removed five days later once its
condition changed, several rounds of analyzer adoption — and every one of those is recorded in
[`docs/DECISIONS.md`](docs/DECISIONS.md) with the reasoning that led there. A PR proposing
something that reasoning already rejected (bringing back a mirror-selection list, say) will be
pointed at the relevant entry rather than reviewed on its own; check there first if a change feels
like it might be revisiting settled ground.

## Environment

- .NET SDK matching `TargetFrameworks` in `TcgDex.CSharpSdk/TcgDex.CSharpSdk.csproj` — currently
  `net10.0`, `net8.0`, `netstandard2.0`.
- The `net472` test lane needs the .NET Framework 4.6.1+ targeting pack (or Visual Studio, which
  installs it). This is the only lane that actually **executes** the `netstandard2.0` asset rather
  than merely compiling it — see [`docs/learnings.md`](docs/learnings.md) for why that distinction
  matters and has bitten this repo before.
- Native AOT verification needs the Visual Studio Installer's tools directory on `PATH` — see
  [`docs/architecture.md`](docs/architecture.md) if you touch `TcgDex.CSharpSdk.AotSmokeTest`.

```bash
git clone https://github.com/PinKushin/TcgDex.CSharpSdk
cd TcgDex.CSharpSdk
dotnet restore
```

## The gate — run this before opening a PR

Four checks, in the order that fails fastest. This is the same sequence
[`.claude/skills/tcgdex-verify/SKILL.md`](.claude/skills/tcgdex-verify/SKILL.md) encodes for an AI
agent working in this repo — one description, not two that can drift apart.

```bash
# 1. Build, warnings as errors. Zero warnings is the standard, not an aspiration.
dotnet build --configuration Release -warnaserror
```

```bash
# 2. Unit tests, all three target frameworks.
dotnet test TcgDex.CSharpSdk.Tests/TcgDex.CSharpSdk.Tests.csproj
```

Read the totals, not the word "Passed!" — a crashed test host prints `Passed!` with a truncated
count, and a `--filter` matching nothing exits 0 with no summary at all. Compare against the known
suite size if anything looks off.

```bash
# 3. Coverage — the one gate CI actually enforces.
dotnet test TcgDex.CSharpSdk.Tests/TcgDex.CSharpSdk.Tests.csproj --collect:"XPlat Code Coverage" --settings coverlet.runsettings --results-directory ./TestResults
pwsh ./scripts/Check-Coverage.ps1 -ResultsDirectory ./TestResults -Threshold 99.5 -BranchThreshold 95
```

```bash
# 4. Docs. docfx.json is at the repo root, not in docs/.
dotnet docfx build docfx.json --warningsAsErrors
```

**Never `--no-build` or `--no-restore`.** They exist for an IDE that has already built and knows
the binaries are current; from a terminal nothing guarantees that, and a compile error stops
neither the test run nor the false-green result it produces.

## If your change touches the public API

`TcgDex.CSharpSdk.Tests/PublicApi.approved.cs` pins the shipped surface. An intentional addition
or change means regenerating it from the `.received.cs` the failing test writes — **with LF line
endings**, or the diff is the line endings instead of the surface:

```bash
tr -d '\r' < TcgDex.CSharpSdk.Tests/PublicApi.received.cs > TcgDex.CSharpSdk.Tests/PublicApi.approved.cs
```

Every public member needs an XML doc comment — this isn't a style preference, `CS1591` is a build
error on the shipped library (though not on test/benchmark projects, where it would be noise), so
the build itself won't let one through undocumented.

## Testing discipline

The short version, in [`docs/architecture.md`](docs/architecture.md#testing-conventions) and
worked through at length in [`docs/learnings.md`](docs/learnings.md):

- Unit tests are **hermetic** — no network, not even on a path taken only when the code is
  broken. A guard that stops a live call is not hermeticity, because a mutant that removes the
  guard reaches the network anyway.
- A test that has never been red proves nothing. Break the thing on purpose, watch the *right*
  test fail, restore with a precise inverse edit. `scripts/Test-Manipulation.ps1` automates this
  and reports a verdict rather than raw output.
- Predict an exact value. `ShouldNotBeNull()` detects that *something* happened; it says nothing
  about whether it's the *right* something.
- A new model is tested against a **recorded live response**, not hand-written JSON — fixtures
  live in `TcgDex.CSharpSdk.Tests/Fixtures` and are refreshed with `scripts/Update-Fixtures.ps1`.

## Committing and recording

- Every commit body says **what was learned**, not just what changed — root cause, a platform
  quirk, a non-obvious constraint. One-line commits are for genuinely trivial changes only.
- A correction, a reversal, or a project-level decision goes in
  [`docs/DECISIONS.md`](docs/DECISIONS.md) as a numbered entry, in the same commit as the work it
  governs — not reconstructed afterward from memory.
- `CHANGELOG.md` is **consumer-scoped**: something an application can observe. Refactors, style
  sweeps, test additions and CI work don't appear there — see the file's own header for the exact
  line.

## Opening the PR

- `main` is protected — everything goes through a pull request, and there's no admin bypass.
- CI must be green **and carry zero annotations** — a passing check only means no step exited
  non-zero, not that the run was clean. Check annotations explicitly if a run looks green but
  something feels off.
- Auto-merge is off. A merge is an explicit step taken once CI is actually clean.
- Releases are git-tagged (`v*`) and published by `release.yml` — merging to `main` does not
  publish a new version on its own.

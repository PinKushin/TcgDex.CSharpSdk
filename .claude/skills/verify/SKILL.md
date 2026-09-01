---
name: verify
description: Run the full local gate for TcgDex.CSharpSdk — build, unit tests on all three TFMs, the coverage gate, and the docs build. Use before pushing, before opening a PR, or to confirm the repo is green.
---

# Verify

The four checks CI enforces, in the order that fails fastest. Run all of them
before pushing; the coverage gate in particular is the one CI blocks on.

**Never pass the build-skipping flags** (`--no-build`, `--no-restore`, or the
MSBuild `NoBuild` property). They exist for Visual Studio, which has already
built and knows the binaries are current; from a terminal nothing guarantees
that. With no compile step to fail, a compile error does not stop the run — the
runner loads whatever DLL was last written and reports green for code that no
longer exists. A machine-wide `PreToolUse` hook refuses these, so an attempt is
blocked rather than silently ruining the measurement.

## 1. Build, warnings as errors

```bash
dotnet build --configuration Release -warnaserror
```

Zero warnings is the standard, not an aspiration. The shipped library runs
`AnalysisMode=All` plus SonarAnalyzer; the test projects deliberately do not.

## 2. Unit tests — all three target frameworks

```bash
dotnet test TcgDex.CSharpSdk.Tests/TcgDex.CSharpSdk.Tests.csproj
```

Runs `net10.0`, `net8.0` and `net472`. **`net472` is not redundant** — it is the
only one that actually executes the `netstandard2.0` asset rather than merely
compiling it.

**Read the totals, not the word "Passed!".** A crashed test host prints
`Passed!` with a truncated count, and a `--filter` matching nothing exits 0 with
no summary at all. Compare the total against the known suite size, and be
suspicious of a total that moved when the suite did not.

## 3. Coverage gate — the one CI enforces

```bash
dotnet test TcgDex.CSharpSdk.Tests/TcgDex.CSharpSdk.Tests.csproj --collect:"XPlat Code Coverage" --settings coverlet.runsettings --results-directory ./TestResults
```

```bash
pwsh ./scripts/Check-Coverage.ps1 -ResultsDirectory ./TestResults -Threshold 99.5 -BranchThreshold 95
```

The thresholds are arguments rather than defaults, so **keep them in step with
`.github/workflows/ci.yml`** — if that file changes them, change them here. The
script prints the uncovered branches per file, which is the useful output even
on a pass.

## 4. Docs

```bash
dotnet docfx build docfx.json --warningsAsErrors
```

`docfx.json` is at the **repository root**, not in `docs/`. Relative links
pointing outside the docs tree fail here as `InvalidFileLink` — use a full
GitHub URL for anything under `.github/` or at the repo root.

## If the public API surface changed

`TcgDex.CSharpSdk.Tests/PublicApi.approved.cs` pins the surface. An intentional
change means regenerating it from the `.received.cs` that the failing test
writes — **with LF line endings**, or the comparison fails on the endings rather
than on the surface:

```bash
tr -d '\r' < TcgDex.CSharpSdk.Tests/PublicApi.received.cs > TcgDex.CSharpSdk.Tests/PublicApi.approved.cs
```

## Not part of this gate

- **Live-API tests** need the network and are excluded from CI's unit job:

  ```bash
  dotnet test TcgDex.CSharpSdk.IntegrationTests/TcgDex.CSharpSdk.IntegrationTests.csproj --filter "TestCategory=Integration"
  ```

- **Mutation testing** takes the machine-wide lock and runs ~20 minutes:

  ```bash
  C:\Users\pinku\source\repos\PinKushin\run-exclusive.ps1 -TimeoutMinutes 45 dotnet stryker
  ```

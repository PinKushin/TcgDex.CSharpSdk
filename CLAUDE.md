# TcgDex.CSharpSdk

.NET SDK for the TCGdex Pokémon TCG API. Published on NuGet via Trusted Publishing.

Full documentation is in `docs/`. **`docs/api-info.md` is the verified API ground truth** —
built from live responses, and the one to trust when the upstream docs and the live service
disagree (they do).

## Commands

```bash
dotnet build --configuration Release -warnaserror
dotnet test TcgDex.CSharpSdk.Tests/TcgDex.CSharpSdk.Tests.csproj
```

Coverage gate — **run this before pushing; it is the one CI enforces**:

```bash
dotnet test TcgDex.CSharpSdk.Tests/TcgDex.CSharpSdk.Tests.csproj --collect:"XPlat Code Coverage" --settings coverlet.runsettings --results-directory ./TestResults
```

```bash
pwsh ./scripts/Check-Coverage.ps1 -ResultsDirectory ./TestResults -Threshold 99.5 -BranchThreshold 95
```

Live-API tests (network; excluded from CI's unit job):

```bash
dotnet test TcgDex.CSharpSdk.IntegrationTests/TcgDex.CSharpSdk.IntegrationTests.csproj --filter "TestCategory=Integration"
```

Mutation testing — **takes the machine-wide lock**, because Stryker rebuilds continuously into
the same `obj/` a neighbouring test run needs:

```bash
C:\Users\pinku\source\repos\PinKushin\run-exclusive.ps1 -TimeoutMinutes 45 dotnet stryker
```

Other scripts: `scripts/Update-Fixtures.ps1` (re-record fixtures from the live API),
`scripts/New-ReleaseAnnouncement.ps1` (Discord post from the CHANGELOG section).

## Layout

| Project | Role |
|---|---|
| `TcgDex.CSharpSdk` | The shipped library. `net10.0;net8.0;netstandard2.0`. The strict analyzer posture (`AnalysisMode=All` + SonarAnalyzer) is scoped **here only**. |
| `TcgDex.CSharpSdk.Tests` | Unit suite. **Hermetic — never touches the network.** The only Stryker target (`net10.0`). |
| `TcgDex.CSharpSdk.IntegrationTests` | Live-API tests, plus `JsonShape` — the engine every fixture-drift verdict comes from. `TestCategory=Integration` marks the networked ones; `JsonShape`'s own tests are offline and run in CI. |
| `TcgDex.CSharpSdk.Benchmarks` | BenchmarkDotNet, including an arm for the other public C# SDK (see `docs/comparison.md`). |
| `TcgDex.CSharpSdk.Fuzz` | SharpFuzz harness. |
| `TcgDex.CSharpSdk.AotSmokeTest` | Proves the SDK still publishes under Native AOT. |

## Gotchas

- **A unit test must never reach the network — not even on a path taken only when the code is
  broken.** `Create_DisposesItsOwnHttpClient` asserted disposal by awaiting a real request and
  expecting `ObjectDisposedException`, which is hermetic only while the code is correct. Under
  Stryker, every mutant that defeated the disposal dialled the live API; on a night the API was
  down, each one hit the 30-second timeout and the run went from ~20 minutes to 2h38m, silently
  starving a neighbouring job on the shared measurement box. Assert the observable state directly.
  (`docs/learnings.md` — "A test can be hermetic only while the code is correct".)

- **The query builder must never call `Expression.Compile()`.** Runtime codegen is not AOT-safe and
  would break Unity and Native AOT consumers. Expression trees are walked and translated to query
  parameters, never compiled.

- **The System.Text.Json source generator discards collection initializers.** A property written
  `= []` deserializes to `null`; collections need a coalescing backing field to stay non-null.

- **The public API surface is pinned** by `TcgDex.CSharpSdk.Tests/PublicApi.approved.cs`
  (PublicApiGenerator). An intentional change means regenerating that baseline — with **LF** line
  endings, or the comparison fails on the endings rather than on the surface.

- **The strict analyzers are deliberately not solution-wide.** On test code `AnalysisMode=All` plus
  Sonar is near-total noise — S2699 fires on every CsCheck property, CA2000 on every undisposed test
  `HttpClient` — with no real finding behind it.

- **Native AOT publish needs the VS Installer directory on `PATH`**, or the native link step fails
  with a misleading error.

- **`main` is protected: you cannot push to it.** Work goes through a PR, and auto-merge is off, so
  a merge is an explicit step after CI is green. Releases are git-tagged and published by workflow.

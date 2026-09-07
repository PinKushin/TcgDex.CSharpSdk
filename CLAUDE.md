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

## Non-negotiable constraints

See `docs/DECISIONS.md` (sections 2–8) for the full reasoning behind these rules:

- **Unit tests never reach the network.** Not on error paths either. Assert observable state directly.
- **Query builder never calls `Expression.Compile()`.** AOT-unsafe; breaks Unity and Native AOT.
- **Collection initializers require coalescing backing fields.** System.Text.Json source generator discards `= []`.
- **Public API pinned by `PublicApi.approved.cs`.** Regenerate with **LF** line endings only.
- **Strict analyzers scoped to the library only.** Test code noise is noise; accept it on tests.
- **Native AOT needs VS Installer on `PATH`.** Or native link step fails with a misleading error.
- **`main` is protected.** Work through PR; auto-merge is off; releases are git-tagged.

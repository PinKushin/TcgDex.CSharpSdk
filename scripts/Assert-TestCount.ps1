#Requires -Version 7
<#
.SYNOPSIS
Fails unless a named .trx reports at least the expected number of executed tests, and none failed.

.DESCRIPTION
"Passed!" is not the result. The count is. Two failures produce a green run and no visible
complaint:

  - A test host that dies partway prints a pass line with a truncated total.
  - `dotnet test --filter` matching nothing exits 0 and prints no summary at all, so a renamed
    fixture silently tests nothing.

A floor per NAMED LANE, not a union total across all of them. The union form this replaced
(Verify-TestCoverage.ps1) asserted one number against the combined unique test-name set across
every .trx, which has a real blind spot in this repo specifically: net10.0 and net472 run the
identical suite by name, so a test silently dropped from just the net472 lane while it still runs
on net10.0 is invisible to a union check - the name still appears in the set from the other lane.
Checking each lane's own .trx independently cannot have that blind spot, because it never merges
names across files in the first place. See PinKushin/C3-COVERAGE-LOG.md (2026-09-09) for the
cross-repo finding this came from, and Tf2DemoSalvage/build/assert-test-count.sh for the pattern
this mirrors.

A FLOOR rather than an exact count, but NOT a floor that is set once and forgotten. What the floor
buys over an exact match is that it does not go red on a legitimate run-to-run wobble (this repo
hand-bumped its old union total twice in one week chasing exactly that kind of noise - 9499f9e,
c879724); it still catches a host dying mid-run or a filter matching nothing, which are the
failures that hide.

That is NOT license to leave the number alone as tests are added. Owner's correction, 2026-09-11:
"no floors get bumped when you add tests too or they are worthlessx" - a floor that never rises
drifts further from the true count with every addition, and the gap it leaves IS the blind spot
this check exists to close: if the true count grows to 600 while the floor stays at today's
number, up to that whole difference could silently stop running and this would still pass.

So: RAISE the floor every time tests are added, in the same commit as the tests - it costs one
number. LOWER it only when tests were deliberately removed, and say why in the same commit. The
floor's only real exemption from exact tracking is that it need not be bumped for a transient dip
that resolves itself (a flaky skip, a timing-dependent count) - anything that changes the suite on
purpose changes this number in the same breath.

.PARAMETER ResultsDirectory
Directory to search recursively for the .trx.

.PARAMETER FileName
The .trx file's own name (e.g. 'unit-tests.trx'). Matched by basename only, the same way the
bash version does it, so this works regardless of which subdirectory download-artifact extracted
into.

.PARAMETER Floor
Minimum tests that must have executed.

.PARAMETER Label
Shown in output, so a failure names which lane broke without the reader having to infer it from
the file name.
#>
param(
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [Parameter(Mandatory)][string]$FileName,
    [Parameter(Mandatory)][int]$Floor,
    [Parameter(Mandatory)][string]$Label
)

if (-not (Test-Path -LiteralPath $ResultsDirectory)) {
    Write-Error "${Label}: results directory not found: $ResultsDirectory"
    exit 1
}

$trx = Get-ChildItem -Path $ResultsDirectory -Filter $FileName -Recurse -File | Select-Object -First 1

if ($null -eq $trx) {
    Write-Error "${Label}: no .trx named '$FileName' under $ResultsDirectory - the run produced no results file at all."
    exit 1
}

# The Counters element carries the authoritative totals. Parsing the console
# "Passed!" line instead would reintroduce the truncation problem this exists to catch.
[xml]$results = Get-Content -LiteralPath $trx.FullName -Raw
$counters = $results.TestRun.ResultSummary.Counters

if ($null -eq $counters) {
    Write-Error "${Label}: '$($trx.Name)' has no ResultSummary/Counters - not a valid TRX, or the run crashed before writing one."
    exit 1
}

$executed = [int]$counters.total
$failed = [int]$counters.failed

Write-Host "${Label}: $executed executed, $failed failed (floor $Floor)"

if ($failed -gt 0) {
    Write-Error "${Label}: $failed test(s) failed."
    exit 1
}

if ($executed -lt $Floor) {
    Write-Error "${Label}: only $executed tests executed, expected at least $Floor. Either the test host died partway, a filter matched fewer tests than before, or tests were deliberately removed - if removed, lower the floor and say why in the same commit."
    exit 1
}

exit 0

<#
.SYNOPSIS
    Runs everything CI enforces, and reports one verdict.

.DESCRIPTION
    Five commands live in four places — the solution build, the unit suite across
    three frameworks, the coverage collection and its threshold script, the
    offline half of the integration project, and the docs build. Reconstructing
    that list from ci.yml each time is how a threshold drifts or a step quietly
    stops being run.

    Two things it does that a shell alias would not:

    - It COMPARES TEST TOTALS against what the run reports, because "Passed!" is
      printed by a crashed host with a truncated count, and a --filter matching
      nothing exits 0 with no summary at all. A green line is not a result; the
      count is.
    - It keeps going after a failure and reports every step, so one run tells you
      the whole story rather than the first thing that broke.

    Never passes the build-skipping flags. With no compile step to fail, a
    compile error does not stop the run and the DLL from last time is measured
    instead.

.PARAMETER Quick
    Unit tests on net10.0 only, skipping coverage, docs and the integration
    project. For the inner loop; not a substitute for the full gate before a
    push.

.EXAMPLE
    pwsh ./scripts/Invoke-Gate.ps1
    pwsh ./scripts/Invoke-Gate.ps1 -Quick
#>
[CmdletBinding()]
param([switch]$Quick)

$ErrorActionPreference = 'Continue'

$results = [System.Collections.Generic.List[object]]::new()

function Add-Result([string]$Step, [bool]$Ok, [string]$Detail) {
    $results.Add([pscustomobject]@{ Step = $Step; Ok = $Ok; Detail = $Detail })

    $mark = if ($Ok) { 'ok  ' } else { 'FAIL' }
    $colour = if ($Ok) { 'Green' } else { 'Red' }

    Write-Host ("  {0}  {1,-34} {2}" -f $mark, $Step, $Detail) -ForegroundColor $colour
}

Write-Host "==> build" -ForegroundColor Cyan
$build = & dotnet build --configuration Release -warnaserror --nologo 2>&1 | Out-String
$buildOk = $build -match 'Build succeeded'
$issues = ([regex]::Matches($build, '(?m)^.*(error|warning) [A-Z]+\d+')).Count
Add-Result 'build (warnings as errors)' $buildOk "$issues issue(s)"

Write-Host "==> unit tests" -ForegroundColor Cyan
$frameworks = if ($Quick) { @('net10.0') } else { @() }

$testArgs = @('test', 'TcgDex.CSharpSdk.Tests/TcgDex.CSharpSdk.Tests.csproj', '--nologo')
if ($Quick) { $testArgs += @('-f', 'net10.0') }

$unit = & dotnet @testArgs 2>&1 | Out-String

# One summary line per framework. Reading each rather than the exit code is
# what catches a host that crashed partway with a truncated total.
$summaries = [regex]::Matches($unit, '(Passed|Failed)!\s+-\s+Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+\d+,\s+Total:\s+(\d+).*?\((?<tfm>[^)]+)\)')

if ($summaries.Count -eq 0) {
    Add-Result 'unit tests' $false 'no summary line - the run did not complete'
}
else {
    $expected = if ($Quick) { 1 } else { 3 }

    foreach ($s in $summaries) {
        $failed = [int]$s.Groups[2].Value
        $total = [int]$s.Groups[4].Value
        Add-Result "unit tests $($s.Groups['tfm'].Value)" ($failed -eq 0 -and $total -gt 0) "$total tests, $failed failed"
    }

    if ($summaries.Count -ne $expected) {
        Add-Result 'unit test frameworks' $false "$($summaries.Count) of $expected frameworks reported - one did not run"
    }
}

if (-not $Quick) {
    Write-Host "==> coverage" -ForegroundColor Cyan

    if (Test-Path ./TestResults) { Remove-Item ./TestResults -Recurse -Force }

    $null = & dotnet test TcgDex.CSharpSdk.Tests/TcgDex.CSharpSdk.Tests.csproj `
        --collect:"XPlat Code Coverage" --settings coverlet.runsettings `
        --results-directory ./TestResults --nologo 2>&1

    # Thresholds passed as arguments, and they must match ci.yml. If that file
    # changes them, change them here.
    $coverage = & pwsh ./scripts/Check-Coverage.ps1 -ResultsDirectory ./TestResults `
        -Threshold 99.5 -BranchThreshold 95 2>&1 | Out-String

    $line = if ($coverage -match 'Line\s+coverage:\s+\S+\s+=\s+(\S+)') { $Matches[1] } else { '?' }
    $branch = if ($coverage -match 'Branch coverage:\s+\S+\s+=\s+(\S+)') { $Matches[1] } else { '?' }
    Add-Result 'coverage gate' ($coverage -match 'thresholds met') "line $line, branch $branch"

    Write-Host "==> offline integration tests" -ForegroundColor Cyan
    $integration = & dotnet test TcgDex.CSharpSdk.IntegrationTests/TcgDex.CSharpSdk.IntegrationTests.csproj `
        --filter "TestCategory!=Integration" --nologo 2>&1 | Out-String

    if ($integration -match '(Passed|Failed)!\s+-\s+Failed:\s+(\d+),\s+Passed:\s+\d+,\s+Skipped:\s+\d+,\s+Total:\s+(\d+)') {
        Add-Result 'offline integration' ([int]$Matches[2] -eq 0 -and [int]$Matches[3] -gt 0) "$($Matches[3]) tests, $($Matches[2]) failed"
    }
    else {
        Add-Result 'offline integration' $false 'no summary line'
    }

    Write-Host "==> docs" -ForegroundColor Cyan

    # docfx.json is at the REPOSITORY ROOT, not in docs/.
    $docs = & dotnet docfx build docfx.json --warningsAsErrors 2>&1 | Out-String
    $warnings = if ($docs -match '(\d+) warning\(s\)') { $Matches[1] } else { '?' }
    Add-Result 'docs (warnings as errors)' ($docs -match 'Build succeeded' -and $warnings -eq '0') "$warnings warning(s)"
}

Write-Host ""

$failures = @($results | Where-Object { -not $_.Ok })

if ($failures.Count -eq 0) {
    $scope = if ($Quick) { 'QUICK gate' } else { 'FULL gate' }
    Write-Host "$scope PASSED - $($results.Count) step(s)." -ForegroundColor Green

    if ($Quick) {
        Write-Host "Quick skips coverage, docs, the other two frameworks and the integration"
        Write-Host "project. Run the full gate before pushing; the coverage one is what CI blocks on."
    }

    exit 0
}

Write-Host "GATE FAILED - $($failures.Count) of $($results.Count) step(s):" -ForegroundColor Red
foreach ($f in $failures) { Write-Host "  - $($f.Step): $($f.Detail)" -ForegroundColor Red }
exit 1

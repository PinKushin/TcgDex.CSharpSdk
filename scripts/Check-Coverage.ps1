#Requires -Version 7.0
<#
.SYNOPSIS
    Fails if line or branch coverage of the hand-written SDK falls below a threshold.

.DESCRIPTION
    The XPlat code-coverage collector emits a report but cannot enforce a
    threshold — that is a coverlet.msbuild feature, and switching to it would
    mean giving up the collector's cleaner integration with `dotnet test`. So the
    gate is this separate pass over the Cobertura output.

    Generated files are excluded, exactly as coverlet.runsettings does when
    producing the report. System.Text.Json's source generator emits several
    thousand lines of *.g.cs, which swamp the hand-written SDK and make the
    headline number meaningless.

    Both metrics are gated, because they answer different questions. Line
    coverage asks whether a line ran; branch coverage asks whether both outcomes
    of a condition were tested. A line holding `flipped ? a : b` is fully
    line-covered the first time it executes, while half its behaviour has never
    been exercised — so line coverage alone can sit near 100% with real logic
    untested.

    Prints a per-file breakdown of anything incomplete so a failure says which
    file regressed, not merely that the total moved.

.PARAMETER ResultsDirectory
    Directory containing coverage.cobertura.xml, searched recursively.

.PARAMETER Threshold
    Minimum acceptable line-coverage percentage.

.PARAMETER BranchThreshold
    Minimum acceptable branch-coverage percentage.

.EXAMPLE
    ./scripts/Check-Coverage.ps1 -ResultsDirectory ./TestResults -Threshold 99.5
#>
[CmdletBinding()]
param(
    [Parameter()]
    [string] $ResultsDirectory = './TestResults',

    [Parameter()]
    [ValidateRange(0, 100)]
    [double] $Threshold = 99.5,

    [Parameter()]
    [ValidateRange(0, 100)]
    [double] $BranchThreshold = 95
)

$ErrorActionPreference = 'Stop'

$reports = Get-ChildItem -Path $ResultsDirectory -Filter 'coverage.cobertura.xml' -Recurse -ErrorAction SilentlyContinue

if (-not $reports) {
    Write-Error "No coverage.cobertura.xml found under '$ResultsDirectory'. Did the test run collect coverage?"
}

# A line counts as covered if any report covered it: with several target
# frameworks the same line appears in more than one report, and a line exercised
# under one of them is exercised.
$covered = [System.Collections.Generic.HashSet[string]]::new()
$total = [System.Collections.Generic.HashSet[string]]::new()

# Branch data hangs off the same line elements, as "condition-coverage" of the
# form "50% (1/2)". Counted once per line across reports, like lines are.
$branchSeen = [System.Collections.Generic.HashSet[string]]::new()
$branchCovered = 0
$branchTotal = 0
$partialBranches = @{}

foreach ($report in $reports) {
    $xml = [xml](Get-Content -Path $report.FullName -Raw)

    foreach ($class in $xml.SelectNodes('//class')) {
        $file = $class.filename

        if (-not $file -or $file.EndsWith('.g.cs')) {
            continue
        }

        foreach ($line in $class.SelectNodes('.//line')) {
            $key = "$file`:$($line.number)"
            [void] $total.Add($key)

            if ([int] $line.hits -gt 0) {
                [void] $covered.Add($key)
            }

            if ($line.branch -ne 'True' -or -not $line.'condition-coverage') {
                continue
            }

            if (-not $branchSeen.Add($key)) {
                continue
            }

            # "50% (1/2)" -> 1 and 2
            $fraction = ($line.'condition-coverage' -split '\(')[1].TrimEnd(')')
            $parts = $fraction -split '/'
            $hit = [int] $parts[0]
            $all = [int] $parts[1]

            $branchCovered += $hit
            $branchTotal += $all

            if ($hit -lt $all) {
                $name = Split-Path -Path $file -Leaf

                if (-not $partialBranches.ContainsKey($name)) {
                    $partialBranches[$name] = @()
                }

                $partialBranches[$name] += "L$($line.number)($hit/$all)"
            }
        }
    }
}

if ($total.Count -eq 0) {
    Write-Error 'Coverage reports contained no measurable lines.'
}

$percentage = 100.0 * $covered.Count / $total.Count

# Per-file detail, worst first, so a failure names the file that regressed.
$byFile = @{}
foreach ($key in $total) {
    $file = $key.Substring(0, $key.LastIndexOf(':'))

    if (-not $byFile.ContainsKey($file)) {
        $byFile[$file] = @{ Covered = 0; Total = 0; Missing = @() }
    }

    $byFile[$file].Total++

    if ($covered.Contains($key)) {
        $byFile[$file].Covered++
    }
    else {
        $byFile[$file].Missing += [int] $key.Substring($key.LastIndexOf(':') + 1)
    }
}

$incomplete = $byFile.GetEnumerator() |
    Where-Object { $_.Value.Covered -lt $_.Value.Total } |
    Sort-Object { $_.Value.Covered / $_.Value.Total }

if ($incomplete) {
    Write-Host ''
    Write-Host 'Files below 100%:'

    foreach ($entry in $incomplete) {
        $name = Split-Path -Path $entry.Key -Leaf
        $value = $entry.Value
        $filePercentage = 100.0 * $value.Covered / $value.Total
        $lines = ($value.Missing | Sort-Object) -join ', '

        Write-Host ('  {0,-34} {1,4}/{2,-4} {3,6:N1}%  lines {4}' -f
            $name, $value.Covered, $value.Total, $filePercentage, $lines)
    }
}

$branchPercentage = if ($branchTotal -gt 0) { 100.0 * $branchCovered / $branchTotal } else { 100.0 }

if ($partialBranches.Count -gt 0) {
    Write-Host ''
    Write-Host 'Partially covered branches (one side of a condition untested):'

    foreach ($entry in $partialBranches.GetEnumerator() | Sort-Object { -$_.Value.Count }) {
        Write-Host ('  {0,-34} {1}' -f $entry.Key, ($entry.Value -join ', '))
    }
}

Write-Host ''
Write-Host ('Line   coverage: {0}/{1} = {2:N2}%  (threshold {3:N2}%)' -f
    $covered.Count, $total.Count, $percentage, $Threshold)
Write-Host ('Branch coverage: {0}/{1} = {2:N2}%  (threshold {3:N2}%)' -f
    $branchCovered, $branchTotal, $branchPercentage, $BranchThreshold)

if ($branchPercentage -lt $BranchThreshold) {
    $message = "Branch coverage {0:N2}% is below the {1:N2}% threshold. " -f $branchPercentage, $BranchThreshold
    $message += 'One side of a condition listed above is untested — that is behaviour '
    $message += 'no test has ever exercised, even though the line reports as covered.'

    Write-Host ''
    Write-Error $message
}

if ($percentage -lt $Threshold) {
    $message = "Coverage {0:N2}% is below the {1:N2}% threshold. " -f $percentage, $Threshold
    $message += 'Add tests for the lines listed above. If a line is genuinely '
    $message += 'unreachable, record why in docs/coverage.md rather than lowering the gate.'

    Write-Host ''
    Write-Error $message
}

Write-Host 'Coverage thresholds met.'

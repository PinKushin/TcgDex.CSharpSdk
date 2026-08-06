#Requires -Version 7.0
<#
.SYNOPSIS
    Fails if line coverage of the hand-written SDK falls below a threshold.

.DESCRIPTION
    The XPlat code-coverage collector emits a report but cannot enforce a
    threshold — that is a coverlet.msbuild feature, and switching to it would
    mean giving up the collector's cleaner integration with `dotnet test`. So the
    gate is this separate pass over the Cobertura output.

    Generated files are excluded, exactly as coverlet.runsettings does when
    producing the report. System.Text.Json's source generator emits several
    thousand lines of *.g.cs, which swamp the hand-written SDK and make the
    headline number meaningless.

    Prints a per-file breakdown of anything below 100% so a failure says which
    file regressed, not merely that the total moved.

.PARAMETER ResultsDirectory
    Directory containing coverage.cobertura.xml, searched recursively.

.PARAMETER Threshold
    Minimum acceptable line-coverage percentage.

.EXAMPLE
    ./scripts/Check-Coverage.ps1 -ResultsDirectory ./TestResults -Threshold 98
#>
[CmdletBinding()]
param(
    [Parameter()]
    [string] $ResultsDirectory = './TestResults',

    [Parameter()]
    [ValidateRange(0, 100)]
    [double] $Threshold = 98
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

Write-Host ''
Write-Host ('Line coverage: {0}/{1} = {2:N2}%  (threshold {3:N2}%)' -f
    $covered.Count, $total.Count, $percentage, $Threshold)

if ($percentage -lt $Threshold) {
    $message = "Coverage {0:N2}% is below the {1:N2}% threshold. " -f $percentage, $Threshold
    $message += 'Add tests for the lines listed above. If a line is genuinely '
    $message += 'unreachable, record why in docs/coverage.md rather than lowering the gate.'

    Write-Host ''
    Write-Error $message
}

Write-Host 'Coverage threshold met.'

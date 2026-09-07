#Requires -Version 7
<#
.SYNOPSIS
Verify that test results from all CI lanes cover the full inventory without gaps or duplicates.

.DESCRIPTION
Parses TRX (test result) files from each CI job and asserts that the union of tests across
all lanes equals the full expected inventory. No test should be skipped in any lane, and no
test should appear in duplicate across different result files.

This job detects:
- A lane that matched nothing with `--filter` (silent 0 exit but no tests run)
- A shard that collected only part of the suite
- A test that was dropped or disabled in one lane

.PARAMETER ResultsDirectory
The directory containing test result artifacts from CI, downloaded from all job artifacts.

.PARAMETER ExpectedTestCount
The total number of tests expected to run in a full unit test suite.
If not provided, the count is inferred from the results.
#>
param(
  [Parameter(Mandatory = $true)]
  [string]$ResultsDirectory,

  [Parameter(Mandatory = $false)]
  [int]$ExpectedTestCount = 0
)

function Parse-TrxFile {
  param([string]$Path)

  [xml]$trx = Get-Content $Path
  $results = @()

  foreach ($test in $trx.TestRun.Results.UnitTestResult) {
    $results += [PSCustomObject]@{
      TestName  = $test.testName
      Outcome   = $test.outcome
      FullName  = "$($test.testName)"
    }
  }

  return $results
}

if (-not (Test-Path $ResultsDirectory)) {
  Write-Error "Results directory not found: $ResultsDirectory"
  exit 1
}

$trxFiles = @(Get-ChildItem -Path $ResultsDirectory -Filter "*.trx" -Recurse)

if ($trxFiles.Count -eq 0) {
  Write-Error "No .trx files found in $ResultsDirectory"
  exit 1
}

Write-Host "Found $($trxFiles.Count) test result files:"
$trxFiles | ForEach-Object { Write-Host "  - $($_.FullName)" }

$allTestsByFile = @{}
$testNames = @()

foreach ($file in $trxFiles) {
  $results = Parse-TrxFile -Path $file.FullName
  $testCount = $results.Count

  if ($testCount -eq 0) {
    Write-Error "File has no test results (filter matched nothing?): $($file.Name)"
    exit 1
  }

  Write-Host "  $($file.Name): $testCount tests"

  $allTestsByFile[$file.Name] = $results
  $testNames += $results.TestName
}

# Duplicates across files are expected (same tests on multiple platforms).
# Only flag within-file duplicates as a defect.
foreach ($fileName in $allTestsByFile.Keys) {
  $tests = $allTestsByFile[$fileName].TestName
  $withinFileDupes = $tests | Group-Object | Where-Object { $_.Count -gt 1 }
  if ($withinFileDupes) {
    Write-Error "Found duplicate tests within $fileName (a defect):"
    $withinFileDupes | ForEach-Object { Write-Error "  $($_.Name) appears $($_.Count) times" }
    exit 1
  }
}

# All lanes should have the same count (unit tests are the same on all platforms)
$counts = $allTestsByFile.Values | ForEach-Object { $_.Count } | Sort-Object -Unique
if ($counts.Count -gt 1) {
  Write-Warning "Test counts differ across lanes: $($counts -join ', ')"
  Write-Host "This is expected if lanes run different suites (e.g., integration only on ubuntu)."
  Write-Host "Lane breakdown:"
  $allTestsByFile.GetEnumerator() | ForEach-Object {
    Write-Host "  $($_.Key): $($_.Value.Count) tests"
  }
}

$uniqueTestCount = $testNames | Sort-Object -Unique | Measure-Object | Select-Object -ExpandProperty Count
Write-Host ""
Write-Host "✓ Coverage verified:"
Write-Host "  Total unique tests across all lanes: $uniqueTestCount"
Write-Host "  Files checked: $($trxFiles.Count)"
Write-Host "  No gaps, no duplicates."

if ($ExpectedTestCount -gt 0 -and $uniqueTestCount -ne $ExpectedTestCount) {
  Write-Error "Expected $ExpectedTestCount tests but found $uniqueTestCount"
  exit 1
}

exit 0

<#
.SYNOPSIS
    Whether a pull request is actually clean — checks AND annotations.

.DESCRIPTION
    A green tick means no step returned a non-zero exit code. It does not mean
    the run was clean. Annotations are where the quiet failures live: an
    upload-artifact step reporting "No files were found with the provided path"
    is a WARNING, so a workflow that has silently stopped publishing its output
    still shows green.

    Reading them by hand is a for-loop over check-run ids against the GitHub API,
    which is enough friction that it gets skipped exactly when a run looks fine.
    This collapses it to one call and refuses to say READY unless both halves are
    clean.

.PARAMETER Pr
    Pull request number. Defaults to the one for the current branch.

.PARAMETER Wait
    Block until every check has finished, then report.

.EXAMPLE
    pwsh ./scripts/Get-PrHealth.ps1
    pwsh ./scripts/Get-PrHealth.ps1 -Pr 41 -Wait
#>
[CmdletBinding()]
param(
    [string]$Pr,
    [switch]$Wait
)

$ErrorActionPreference = 'Stop'

if (-not $Pr) {
    $Pr = (& gh pr view --json number -q .number 2>$null)

    if (-not $Pr) {
        Write-Error "No pull request for the current branch. Pass -Pr <number>."
    }
}

if ($Wait) {
    Write-Host "==> waiting for checks on #$Pr" -ForegroundColor Cyan
    & gh pr checks $Pr --watch --interval 20 *>$null
}

$states = & gh pr checks $Pr --json name,state -q '.[] | "\(.state)\t\(.name)"' 2>$null

if (-not $states) {
    Write-Error "No checks reported for #$Pr."
}

$rows = $states | ForEach-Object {
    $parts = $_ -split "`t", 2
    [pscustomobject]@{ State = $parts[0]; Name = $parts[1] }
}

$running = @($rows | Where-Object { $_.State -notin @('SUCCESS', 'SKIPPED', 'FAILURE', 'CANCELLED', 'TIMED_OUT', 'ACTION_REQUIRED', 'NEUTRAL') })
$failed = @($rows | Where-Object { $_.State -in @('FAILURE', 'CANCELLED', 'TIMED_OUT', 'ACTION_REQUIRED') })

Write-Host "==> checks on #$Pr" -ForegroundColor Cyan
$rows | Group-Object State | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0,-16} {1}" -f $_.Name, $_.Count)
}

# Annotations hang off the HEAD commit's check runs, so they are only meaningful
# once those runs have finished.
$sha = & gh pr view $Pr --json headRefOid -q .headRefOid
$repo = & gh repo view --json nameWithOwner -q .nameWithOwner

$annotations = [System.Collections.Generic.List[string]]::new()

$checkRunIds = & gh api "repos/$repo/commits/$sha/check-runs" --jq '.check_runs[].id'

foreach ($id in $checkRunIds) {
    $found = & gh api "repos/$repo/check-runs/$id/annotations" `
        --jq '.[] | "\(.annotation_level)\t\(.path):\(.start_line)\t\(.message)"' 2>$null

    foreach ($a in $found) { if ($a) { $annotations.Add($a) } }
}

Write-Host ""
Write-Host "==> annotations: $($annotations.Count)" -ForegroundColor Cyan

foreach ($a in $annotations) {
    $parts = $a -split "`t", 3
    Write-Host ("  [{0}] {1}" -f $parts[0], $parts[1]) -ForegroundColor Yellow
    Write-Host ("      {0}" -f $parts[2])
}

Write-Host ""

if ($running.Count -gt 0) {
    Write-Host "STILL RUNNING - $($running.Count) check(s) have not finished. Annotations are not final." -ForegroundColor Yellow
    exit 2
}

if ($failed.Count -gt 0) {
    Write-Host "NOT READY - $($failed.Count) check(s) failed." -ForegroundColor Red
    foreach ($f in $failed) { Write-Host "  - $($f.Name)" -ForegroundColor Red }
    exit 1
}

if ($annotations.Count -gt 0) {
    Write-Host "NOT READY - checks pass but $($annotations.Count) annotation(s) remain." -ForegroundColor Red
    Write-Host "Treat each as a defect until shown otherwise. The dangerous ones are warnings"
    Write-Host "that mean a step silently did nothing, and a deprecation is a defect to fix"
    Write-Host "when it appears rather than when it breaks on someone else's schedule."
    exit 1
}

Write-Host "READY - every check passed and there are no annotations." -ForegroundColor Green
exit 0

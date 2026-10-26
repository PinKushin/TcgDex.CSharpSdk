<#
.SYNOPSIS
    Fails unless the CI workflow succeeded on a commit; waits while it is still running.

.DESCRIPTION
    The release workflow runs on a tag, and nothing about a tag says the commit it points at passed CI. The merge
    to main was gated by required checks, but a tag can be pushed to any commit, and the merge's own CI run can still
    be going when the tag is pushed. This is the gate (DECISIONS #42).

    Looks at runs of the CI workflow for the commit. For each triggering event it takes only the newest run, so a
    newer run of the same event supersedes an older one (a run re-run with "Re-run failed jobs" keeps its id and
    reports its latest attempt, so it is already handled). A commit that was pushed and also opened as a pull request
    has a run for each event, and both must be green. It then:
      - exits 0 when there is at least one such run and every one concluded `success`;
      - exits 1 as soon as any concluded as anything else (failure, cancelled, timed out);
      - waits (polling) while any is queued or in progress, up to -TimeoutMinutes, then exits 1;
      - exits 1 if no run exists after -NoRunGraceSeconds (CI starts within seconds of a push, so none means CI
        did not run for this commit, for example a tag on a commit that was never pushed to a branch);
      - treats an error from `gh` (a 5xx, a rate limit) as "unknown" and tries again at the next poll, failing only
        when the deadline passes, so one blip does not abort a release.

    Failures print a single `::error::` line (a GitHub Actions annotation, plain text anywhere else) and `exit 1`.
    `Write-Error` is not used: with `$ErrorActionPreference = 'Stop'` it throws, which would make the `exit` dead code
    and print a stack trace.

    Needs `gh` authenticated, and the `actions: read` permission when run in a workflow.

.PARAMETER Sha
    The full commit SHA to check.

.PARAMETER Repository
    owner/name. Defaults to $env:GITHUB_REPOSITORY.

.PARAMETER Workflow
    The workflow file name to look at. Default ci.yml.

.PARAMETER TimeoutMinutes
    How long to wait for a run that is still in progress. Default 30.

.PARAMETER PollSeconds
    Seconds between polls. Default 20.

.PARAMETER NoRunGraceSeconds
    How long to wait for a run to appear at all before giving up. Default 120.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$Sha,

    [string]$Repository = $env:GITHUB_REPOSITORY,

    [string]$Workflow = 'ci.yml',

    [ValidateRange(1, 360)]
    [int]$TimeoutMinutes = 30,

    [ValidateRange(1, 600)]
    [int]$PollSeconds = 20,

    [ValidateRange(0, 3600)]
    [int]$NoRunGraceSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Repository)) {
    throw 'No repository: pass -Repository owner/name or run where GITHUB_REPOSITORY is set.'
}

function Get-LatestRunPerEvent {
    # Returns Ok = $false (and the reason) when the answer is unknown, so the caller can try again; Ok = $true with an
    # empty list means GitHub answered and there is no run.
    $output = gh run list --repo $Repository --workflow $Workflow --commit $Sha --limit 50 `
        --json databaseId,event,status,conclusion,url 2>&1
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject]@{ Ok = $false; Runs = @(); Reason = "gh run list exited $LASTEXITCODE`: $output" }
    }

    try {
        $runs = @($output | ConvertFrom-Json)
    }
    catch [System.ArgumentException] {
        return [pscustomobject]@{ Ok = $false; Runs = @(); Reason = "gh run list did not return JSON: $output" }
    }

    # Newest run per triggering event: a newer run of the same event supersedes an older one.
    $latest = @($runs | Group-Object event | ForEach-Object {
            $_.Group | Sort-Object databaseId -Descending | Select-Object -First 1
        })

    return [pscustomobject]@{ Ok = $true; Runs = $latest; Reason = '' }
}

$started = Get-Date
$deadline = $started.AddMinutes($TimeoutMinutes)

while ($true) {
    $answer = Get-LatestRunPerEvent
    $latest = @($answer.Runs)

    if (-not $answer.Ok) {
        Write-Host "Could not read the CI runs for $Sha, will try again: $($answer.Reason)"
    }
    elseif ($latest.Count -eq 0) {
        $waited = [int]((Get-Date) - $started).TotalSeconds
        if ($waited -ge $NoRunGraceSeconds) {
            Write-Host "::error::No $Workflow run exists for $Sha after ${waited}s. CI did not run for this commit, so it is not releasable."
            exit 1
        }

        Write-Host "No $Workflow run for $Sha yet (${waited}s); waiting."
    }
    else {
        foreach ($run in $latest) {
            Write-Host ("{0}: {1} {2} {3}" -f $run.event, $run.status, $run.conclusion, $run.url)
        }

        $bad = @($latest | Where-Object { $_.status -eq 'completed' -and $_.conclusion -ne 'success' })
        if ($bad.Count -gt 0) {
            Write-Host "::error::$Workflow did not succeed on ${Sha}: $(($bad | ForEach-Object { "$($_.event)=$($_.conclusion)" }) -join ', ')"
            exit 1
        }

        $pending = @($latest | Where-Object { $_.status -ne 'completed' })
        if ($pending.Count -eq 0) {
            Write-Host "$Workflow succeeded on $Sha."
            exit 0
        }

        Write-Host "$($pending.Count) run(s) still in progress; waiting."
    }

    if ((Get-Date) -ge $deadline) {
        Write-Host "::error::Timed out after $TimeoutMinutes minute(s) waiting for $Workflow on $Sha."
        exit 1
    }

    Start-Sleep -Seconds $PollSeconds
}

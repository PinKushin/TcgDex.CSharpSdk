#Requires -Version 7.0
<#
.SYNOPSIS
    Re-downloads the recorded API fixtures the offline test suite runs against.

.DESCRIPTION
    Every unit test in this repository is written against the recordings in
    TcgDex.CSharpSdk.Tests/Fixtures. Refreshing them is how you respond to
    FixtureDriftTests reporting that the live API has changed shape.

    Run this AFTER adjusting the SDK to the new shape, not before — a refresh
    makes the drift check pass again whether or not the models were updated, so
    doing it first hides the very change you were told about.

    Review the diff before committing. A field appearing or vanishing is exactly
    what the drift check exists to surface, and it should be reflected in the
    models and in docs/api-info.md.

.PARAMETER Fixture
    Refresh only this fixture. Refreshes all of them when omitted.

.PARAMETER WhatIf
    Report what would change without writing anything.

.EXAMPLE
    ./scripts/Update-Fixtures.ps1

.EXAMPLE
    ./scripts/Update-Fixtures.ps1 -Fixture card-pokemon-full.json -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter()]
    [string] $Fixture
)

$ErrorActionPreference = 'Stop'

$apiRoot = 'https://api.tcgdex.net/v2/'
$fixtureDirectory = Join-Path $PSScriptRoot '..' 'TcgDex.CSharpSdk.Tests' 'Fixtures' | Resolve-Path
$manifestPath = Join-Path $fixtureDirectory 'manifest.json'

if (-not (Test-Path $manifestPath)) {
    Write-Error "No manifest at '$manifestPath'."
}

$manifest = (Get-Content -Path $manifestPath -Raw | ConvertFrom-Json).fixtures

$entries = $manifest.PSObject.Properties |
    Where-Object { -not $Fixture -or $_.Name -eq $Fixture }

if (-not $entries) {
    Write-Error "No manifest entry matches '$Fixture'."
}

$changed = @()
$unchanged = 0

foreach ($entry in $entries) {
    $name = $entry.Name
    $source = $entry.Value
    $path = Join-Path $fixtureDirectory $name
    $url = "$apiRoot$source"

    try {
        # -SkipHttpErrorCheck: two fixtures are deliberately recorded error
        # responses, and those are as much a part of the contract as the
        # successful ones.
        $response = Invoke-WebRequest -Uri $url -SkipHttpErrorCheck
        $fresh = $response.Content
    }
    catch {
        Write-Error "Failed to fetch '$url': $_"
    }

    $existing = if (Test-Path $path) { Get-Content -Path $path -Raw } else { $null }

    if ($existing -eq $fresh) {
        $unchanged++
        continue
    }

    $changed += $name

    if ($PSCmdlet.ShouldProcess($name, "refresh from $url")) {
        Set-Content -Path $path -Value $fresh -NoNewline
        Write-Host "  updated  $name"
    }
    else {
        Write-Host "  would update  $name"
    }
}

Write-Host ''
Write-Host "$($changed.Count) changed, $unchanged unchanged."

if ($changed.Count -gt 0) {
    Write-Host ''
    Write-Host 'Review the diff before committing. Most changes are just prices and'
    Write-Host 'timestamps; a field appearing or disappearing means the models and'
    Write-Host 'docs/api-info.md need attention too.'
}

#Requires -Version 7.0
<#
.SYNOPSIS
    Turns a CHANGELOG.md version section into two release outputs: GitHub Release
    notes (the raw section markdown) and a Discord-ready announcement.

.DESCRIPTION
    Consumer-scoped by construction: it reads CHANGELOG.md, which already excludes
    refactors, CI and test-only work, so whatever ends up in the announcement is by
    definition something a consumer can observe. Called by
    .github/workflows/release.yml on each tag, but runnable by hand to preview the
    post before a release.

    Writes 'release-notes.md' (verbatim section body) and 'discord-post.txt'
    (heading emoji, unwrapped bullets, capped at Discord's 2000-character limit)
    into OutputDir, and echoes the Discord post to stdout so the workflow can drop
    it straight into the job summary.

.EXAMPLE
    ./scripts/New-ReleaseAnnouncement.ps1 -Version 0.2.1
    Previews the announcement for 0.2.1 using ./CHANGELOG.md, writing outputs to
    the current directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [string]$ChangelogPath = 'CHANGELOG.md',

    [string]$OutputDir = '.'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ChangelogPath)) {
    throw "Changelog not found at '$ChangelogPath'."
}

$lines = @(Get-Content -LiteralPath $ChangelogPath)

# Locate the section header for this version, e.g. "## [0.2.1] - 2026-08-22".
$headerPattern = "^##\s+\[$([regex]::Escape($Version))\]"
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match $headerPattern) { $start = $i; break }
}
if ($start -lt 0) {
    throw "No '## [$Version]' section found in $ChangelogPath."
}

# The section runs until the next top-level version header or the end of file.
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^##\s+\[') { $end = $i; break }
}

# Body is everything after the header line, trimmed of surrounding blank lines.
$body = @($lines[($start + 1)..($end - 1)])
while ($body.Count -gt 0 -and [string]::IsNullOrWhiteSpace($body[0]))  { $body = @($body[1..($body.Count - 1)]) }
while ($body.Count -gt 0 -and [string]::IsNullOrWhiteSpace($body[-1])) { $body = @($body[0..($body.Count - 2)]) }
if ($body.Count -eq 0) {
    throw "The '## [$Version]' section in $ChangelogPath is empty."
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# --- GitHub Release notes: the raw section body, verbatim. GitHub renders the
#     markdown, and the release title already carries the version. ---
$notesPath = Join-Path $OutputDir 'release-notes.md'
Set-Content -LiteralPath $notesPath -Value $body -Encoding utf8

# --- Discord post: a Keep a Changelog heading becomes an emoji + label, each
#     hard-wrapped bullet is unwrapped to one line, and the whole thing is capped
#     at Discord's 2000-character message limit. ---
$emoji = @{
    'Added'      = "`u{2728}"   # sparkles
    'Changed'    = "`u{1F527}"  # wrench
    'Deprecated' = "`u{26A0}"   # warning
    'Removed'    = "`u{1F5D1}"  # wastebasket
    'Fixed'      = "`u{1F41B}"  # bug
    'Security'   = "`u{1F512}"  # lock
}

$out = [System.Collections.Generic.List[string]]::new()
$out.Add("**TcgDex.CSharpSdk ``$Version``** $([char]0x2014) now on NuGet `u{1F0CF}")  # em dash, playing card
$pending = ''

foreach ($line in $body) {
    if ($line -match '^###\s+(.+?)\s*$') {
        if ($pending) { $out.Add($pending); $pending = '' }
        $name = $Matches[1].Trim()
        $mark = $emoji[$name]
        $out.Add('')
        if ($mark) { $out.Add("$mark **$name**") } else { $out.Add("**$name**") }
    }
    elseif ($line -match '^\s*-\s+(.*)$') {
        if ($pending) { $out.Add($pending) }
        $pending = '- ' + $Matches[1].Trim()
    }
    elseif ([string]::IsNullOrWhiteSpace($line)) {
        if ($pending) { $out.Add($pending); $pending = '' }
    }
    else {
        # A hard-wrapped continuation of the current bullet.
        if ($pending) { $pending = "$pending $($line.Trim())" }
        else          { $out.Add($line.Trim()) }
    }
}
if ($pending) { $out.Add($pending) }

$post = ($out -join "`n").Trim()

$limit = 2000
if ($post.Length -gt $limit) {
    $post = $post.Substring(0, $limit - 1).TrimEnd() + "`u{2026}"  # ellipsis
}

$postPath = Join-Path $OutputDir 'discord-post.txt'
Set-Content -LiteralPath $postPath -Value $post -Encoding utf8

# Echo the Discord post so the workflow can append it to the job summary.
$post

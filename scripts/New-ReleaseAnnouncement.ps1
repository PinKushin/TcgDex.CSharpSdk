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

    Writes 'release-notes.md' (verbatim section body) and 'discord-post.txt' into
    OutputDir, and echoes the Discord post to stdout so the workflow can drop it
    straight into the job summary.

    The Discord post is a SUMMARY, not the section. Three rules make it one, and
    each is here because the naive version produced something unusable on a real
    release:

    - Only each bullet's LEAD PARAGRAPH is carried over. The entries in this
      changelog run to several paragraphs of reasoning, which is right for
      someone reading the file and wrong for a chat message — 0.4.0's first
      bullet alone was longer than Discord's entire message limit.
    - Over the limit, WHOLE BULLETS are dropped from the end. Cutting at a
      character boundary produced a post ending "and only t...", which reads as
      a corrupted message rather than an abbreviated one.
    - The post always ends with a link to the release, so what was dropped is
      still reachable. That is what makes trimming honest rather than lossy.

    Keep a Changelog's section order matters here: dropping from the end means
    'Added' survives and 'Fixed' goes first, which is the right way round for an
    announcement.

.NOTES
    Emoji are written to the file correctly but may render as '?' when the post
    is echoed to a Windows console. That is the terminal's encoding, not the
    output — check discord-post.txt rather than the preview.

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

    [string]$OutputDir = '.',

    [string]$RepositoryUrl = 'https://github.com/PinKushin/TcgDex.CSharpSdk'
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

# The section runs until the next top-level version header, the link-reference
# block, or the end of file.
#
# The link references matter for the OLDEST section specifically: it has no
# header after it, so without this it swallowed the whole
# "[Unreleased]: https://..." block at the bottom of the file and put it in the
# announcement. Only the last release in the changelog is affected, which is why
# it survived until someone regenerated an old one.
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^##\s+\[' -or $lines[$i] -match '^\[[^\]]+\]:\s+\S+') { $end = $i; break }
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
$inLead = $false

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
        $inLead = $true
    }
    elseif ([string]::IsNullOrWhiteSpace($line)) {
        # A blank line ends the bullet's LEAD PARAGRAPH but not the bullet: the
        # entries here run to several paragraphs, and the ones after the first
        # are detail for someone reading the file rather than a chat message.
        $inLead = $false
    }
    else {
        # Hard-wrapped continuation. Only the lead paragraph is carried into the
        # post — everything after it is left to the release notes, which the
        # trailing link points at.
        #
        # Without this the post was either a wall of ragged hard-wrapped lines,
        # or — once entries grew — a single bullet longer than Discord's entire
        # message limit, which no amount of dropping later bullets could fix.
        if ($inLead -and $pending) { $pending = "$pending $($line.Trim())" }
        elseif (-not $pending)     { $out.Add($line.Trim()) }
    }
}
if ($pending) { $out.Add($pending) }

# Always end with somewhere to read the rest. That line is also what makes
# trimming honest rather than lossy: a reader who wants the entries that did not
# fit has a link to them.
$moreUrl = "$RepositoryUrl/releases/tag/v$Version"
$more = "$([char]0x2192) full notes: $moreUrl"   # rightwards arrow

$out.Add('')
$out.Add($more)

$post = ($out -join "`n").Trim()

# --- Fit Discord's limit by dropping WHOLE BULLETS from the end, not by cutting
#     mid-character. Substring-truncation produced posts ending "and only t...",
#     which reads as a corrupted message rather than an abbreviated one — and the
#     changelog entries here run long, so it fired on a real release. ---
$limit = 2000

if ($post.Length -gt $limit) {
    $kept = [System.Collections.Generic.List[string]]::new($out)

    # Drop from the end, skipping the trailing link and its blank line, until it
    # fits or nothing droppable is left.
    while ($post.Length -gt $limit) {
        $dropIndex = -1

        for ($i = $kept.Count - 1; $i -ge 0; $i--) {
            if ($kept[$i] -eq $more -or [string]::IsNullOrWhiteSpace($kept[$i])) { continue }

            $dropIndex = $i
            break
        }

        if ($dropIndex -lt 0) { break }

        $kept.RemoveAt($dropIndex)
        $post = (($kept -join "`n") -replace '\n{3,}', "`n`n").Trim()
    }

    # A heading left with no bullets under it is noise; drop those too.
    for ($i = $kept.Count - 1; $i -ge 0; $i--) {
        if ($kept[$i] -notmatch '^\S*\s*\*\*[A-Za-z]+\*\*$') { continue }

        $hasContent = $false

        for ($j = $i + 1; $j -lt $kept.Count; $j++) {
            if ($kept[$j] -eq $more -or [string]::IsNullOrWhiteSpace($kept[$j])) { continue }
            if ($kept[$j] -match '^\S*\s*\*\*[A-Za-z]+\*\*$') { break }

            $hasContent = $true
            break
        }

        if (-not $hasContent) { $kept.RemoveAt($i) }
    }

    $post = (($kept -join "`n") -replace '\n{3,}', "`n`n").Trim()

    # Last resort. Only reachable if a single bullet exceeds the limit on its
    # own, in which case there is nothing to drop and a hard cut is the only
    # option left.
    if ($post.Length -gt $limit) {
        $post = $post.Substring(0, $limit - 1).TrimEnd() + "`u{2026}"
    }
}

$postPath = Join-Path $OutputDir 'discord-post.txt'
Set-Content -LiteralPath $postPath -Value $post -Encoding utf8

# Echo the Discord post so the workflow can append it to the job summary.
$post

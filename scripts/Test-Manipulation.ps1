<#
.SYNOPSIS
    Breaks the code on purpose, checks that the right test notices, and puts it
    back.

.DESCRIPTION
    A test that has never been red proves nothing. The way to find out is to
    sabotage the code and watch a specific test fail — but done by hand that is
    six steps (back up, edit, build, test, restore, confirm the restore), and the
    dangerous step is the last one. A manipulation left behind looks EXACTLY like
    a real defect. That is not hypothetical: on 2026-09-01 a reviewer reading
    this repository caught a working tree mid-sabotage and had to spend its own
    time proving the committed file was intact.

    So this script exists for one reason above convenience: the restore happens
    in a finally block, and is verified by comparing content afterwards. It
    cannot be forgotten, and it says so if it ever fails.

    It reports a VERDICT rather than test output, because "the test failed" and
    "the test is sensitive to this defect" are different statements and only the
    second one is the answer:

      SENSITIVE    the named test failed while the code was broken. It works.
      INSENSITIVE  the code was broken and the test still passed. It proves
                   nothing about this defect — the assertion, the input, or a
                   missing control needs fixing, not the code.
      REJECTED     the build refused the manipulation. Common here, because the
                   analyzers run at AnalysisMode=All: a no-op stub leaves fields
                   unread (S4487) and a deleted branch becomes a redundant jump
                   (S3626). Not a result either way — pick a sabotage the
                   compiler accepts, such as changing a value rather than
                   removing the code that reads it.

.PARAMETER File
    Source file to sabotage.

.PARAMETER Find
    Exact text to replace. Must appear exactly once; anything else is an error
    rather than a guess.

.PARAMETER Replace
    What to put there instead.

.PARAMETER Test
    NUnit filter for the test that MUST fail, e.g.
    'FullyQualifiedName~ANotFound_IsNotRetried'.

.PARAMETER Project
    Test project. Defaults to the unit suite.

.PARAMETER Framework
    Target framework. Defaults to net10.0 — the fastest of the three, and the
    one Stryker uses.

.EXAMPLE
    ./scripts/Test-Manipulation.ps1 `
        -File TcgDex.CSharpSdk/Http/TcgDexFailoverHandler.cs `
        -Find 'status is HttpStatusCode.BadGateway' `
        -Replace 'status is HttpStatusCode.NotFound or HttpStatusCode.BadGateway' `
        -Test 'FullyQualifiedName~ANotFound_IsNotRetried'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$File,
    [Parameter(Mandatory)][string]$Find,
    [Parameter(Mandatory)][string]$Replace,
    [Parameter(Mandatory)][string]$Test,
    [string]$Project = 'TcgDex.CSharpSdk.Tests/TcgDex.CSharpSdk.Tests.csproj',
    [string]$Framework = 'net10.0'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $File)) {
    Write-Error "No such file: $File"
}

# Read as one string and write back with the same encoding. The repository is
# LF everywhere (.gitattributes says `* text=auto eol=lf`), and a restore that
# quietly rewrote every line ending would show up as a diff on a file this
# script promised not to change.
$original = [System.IO.File]::ReadAllText($File)

$occurrences = ([regex]::Matches($original, [regex]::Escape($Find))).Count

if ($occurrences -ne 1) {
    Write-Error "The Find text appears $occurrences times in $File; it must appear exactly once. A scripted edit that matches nothing reports success and changes nothing, which is indistinguishable from a wrong fix."
}

$verdict = 'UNKNOWN'
$detail = ''

try {
    [System.IO.File]::WriteAllText($File, $original.Replace($Find, $Replace))

    # Assert the write landed rather than trusting it.
    if ([System.IO.File]::ReadAllText($File) -eq $original) {
        Write-Error "The manipulation did not change $File."
    }

    Write-Host "==> sabotaged $File" -ForegroundColor Yellow
    Write-Host "    - $Find"
    Write-Host "    + $Replace"

    # No --no-build, ever: with no compile step to fail, the runner loads
    # whatever DLL was written last and measures code that no longer exists.
    $output = & dotnet test $Project -f $Framework --filter $Test --nologo 2>&1 | Out-String

    if ($output -match 'error (CS|CA|S|IDE)\d+') {
        $verdict = 'REJECTED'
        $detail = ($output -split "`n" | Where-Object { $_ -match 'error (CS|CA|S|IDE)\d+' } | Select-Object -First 1).Trim()
    }
    elseif ($output -match 'Failed!\s+-\s+Failed:\s+(\d+),\s+Passed:\s+(\d+)') {
        $verdict = 'SENSITIVE'
        $detail = "$($Matches[1]) failed, $($Matches[2]) passed"
    }
    elseif ($output -match 'Passed!\s+-\s+Failed:\s+\d+,\s+Passed:\s+(\d+),\s+Skipped:\s+\d+,\s+Total:\s+(\d+)') {
        # A filter that matches nothing exits 0 with no summary, so a renamed
        # test would otherwise read as a passing manipulation — which is the
        # worst possible misreading, since it looks like an insensitive test.
        if ([int]$Matches[2] -eq 0) {
            $verdict = 'REJECTED'
            $detail = "the filter '$Test' matched no tests"
        }
        else {
            $verdict = 'INSENSITIVE'
            $detail = "$($Matches[2]) test(s) ran, none failed"
        }
    }
    else {
        $verdict = 'REJECTED'
        $detail = 'no test summary in the output; the run did not complete'
    }
}
finally {
    # THE POINT OF THE SCRIPT. Restores on every path, including Ctrl-C and a
    # failure above, and proves it rather than assuming.
    [System.IO.File]::WriteAllText($File, $original)

    if ([System.IO.File]::ReadAllText($File) -ne $original) {
        Write-Host ""
        Write-Error "RESTORE FAILED for $File. The working tree still holds a deliberate defect. Fix this before anything else — a left-behind manipulation is indistinguishable from a real bug."
    }

    Write-Host "==> restored $File" -ForegroundColor DarkGray
}

Write-Host ""

switch ($verdict) {
    'SENSITIVE' {
        Write-Host "SENSITIVE - the test failed while the code was broken ($detail)." -ForegroundColor Green
        Write-Host "The test is a real experiment: it detects this defect."
        exit 0
    }
    'INSENSITIVE' {
        Write-Host "INSENSITIVE - the code was broken and the test still passed ($detail)." -ForegroundColor Red
        Write-Host ""
        Write-Host "This test proves nothing about this defect. Before strengthening the"
        Write-Host "assertion, check which of these it is:"
        Write-Host "  wrong instrument  - measuring a proxy that cannot see the variable"
        Write-Host "  wrong condition   - an input where correct and broken predict the SAME result"
        Write-Host "  no control        - nothing that must stay unaffected"
        Write-Host "  effect too small  - the condition is below the resolution of the measurement"
        Write-Host "Strengthening the assertion is usually the wrong fix for the middle two."
        exit 1
    }
    'REJECTED' {
        Write-Host "REJECTED - the build would not accept the manipulation." -ForegroundColor Yellow
        Write-Host "  $detail"
        Write-Host ""
        Write-Host "Not a result. Pick a sabotage the compiler accepts - change a VALUE"
        Write-Host "rather than deleting the code that reads it, since the analyzers reject"
        Write-Host "unread fields and redundant jumps."
        exit 2
    }
    default {
        Write-Error "Could not determine a verdict."
    }
}

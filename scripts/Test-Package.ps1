#Requires -Version 7.0
<#
.SYNOPSIS
    Installs the PACKED TcgDex.CSharpSdk into a fresh project, the way a consumer does, and runs it.

.DESCRIPTION
    Everything else in this repository reaches the library through a ProjectReference, which can
    not see a package that is missing a target framework, the README or the icon, that ships
    repo-relative links, or that is really a stale copy of itself. This does.

    1. Packs the library, or takes the .nupkg/.snupkg in -PackageDirectory (CI's artifact, or
       the release workflow's ./artifacts, so what is tested is what ships).
    2. Reads the package: lib/ assets and XML docs for every target, README, icon, nuspec
       metadata (licence, icon, readme, the commit it was built from), the dependency groups, no
       .pdb in the .nupkg, a matching .snupkg, and no repo-relative links in the packaged README.
    3. Restores TcgDex.CSharpSdk.PackageSmokeTest from a nuget.config that sends
       TcgDex.CSharpSdk to the local feed ONLY, into an empty packages folder, and runs it on
       every target framework below. The program checks that the assembly it loaded carries the
       version and the commit that were just packed. Without that, a restore that quietly took
       nuget.org's copy of the same version would pass.

    The frameworks are listed here, not read from the project, so a framework that silently
    disappears from the project fails the run instead of shrinking it. net472 runs the
    netstandard2.0 asset and exists only on Windows.

.PARAMETER PackageDirectory
    A directory holding the packages to test. When omitted the repository is packed.

.EXAMPLE
    pwsh ./scripts/Test-Package.ps1
    Packs the working tree and tests that.

.EXAMPLE
    pwsh ./scripts/Test-Package.ps1 -PackageDirectory ./artifacts
    Tests the packages the release workflow just built.
#>
[CmdletBinding()]
param(
    [string]$PackageDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$failures = [System.Collections.Generic.List[string]]::new()

function Pass([string]$Message) { Write-Host "  ok    $Message" }

function Fail([string]$Message) {
    $script:failures.Add($Message)
    Write-Host "  FAIL  $Message"
}

function Assert-That([bool]$Condition, [string]$Passed, [string]$Failed) {
    if ($Condition) { Pass $Passed } else { Fail $Failed }
}

function Read-ZipText([System.IO.Compression.ZipArchive]$Zip, [string]$EntryName) {
    $entry = $Zip.GetEntry($EntryName)
    if ($null -eq $entry) { return $null }

    $reader = [System.IO.StreamReader]::new($entry.Open())
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

$revision = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[0-9a-f]{40}$') {
    throw 'Could not read the git revision. The package is identified by the commit it was built from.'
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) "tcgdex-package-smoke-$([guid]::NewGuid().ToString('N'))"
$feed = Join-Path $work 'feed'
$packages = Join-Path $work 'packages'
$config = Join-Path $work 'nuget.config'
New-Item -ItemType Directory -Force -Path $feed | Out-Null

try {
    # ---- 1. the package under test ----
    if ($PackageDirectory) {
        Write-Host "Testing the packages in $PackageDirectory"
        Copy-Item -Path (Join-Path $PackageDirectory '*') -Include '*.nupkg', '*.snupkg' -Destination $feed
    }
    else {
        Write-Host 'Packing the working tree'
        & dotnet pack (Join-Path $repo 'TcgDex.CSharpSdk/TcgDex.CSharpSdk.csproj') --configuration Release --output $feed --nologo
        if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed.' }
    }

    $nupkgs = @(Get-ChildItem -Path $feed -Filter 'TcgDex.CSharpSdk.*.nupkg' | Where-Object { $_.Name -notlike '*.symbols.nupkg' })
    if ($nupkgs.Count -ne 1) { throw "Expected exactly one TcgDex.CSharpSdk .nupkg, found $($nupkgs.Count)." }

    $snupkgs = @(Get-ChildItem -Path $feed -Filter '*.snupkg')

    # ---- 2. what is in it ----
    Write-Host "`nThe package: $($nupkgs[0].Name)"
    Add-Type -AssemblyName System.IO.Compression
    $zip = [System.IO.Compression.ZipFile]::OpenRead($nupkgs[0].FullName)
    try {
        $entries = @($zip.Entries | ForEach-Object { $_.FullName })

        foreach ($lib in 'netstandard2.0', 'net8.0', 'net10.0') {
            foreach ($file in 'TcgDex.CSharpSdk.dll', 'TcgDex.CSharpSdk.xml') {
                $name = "lib/$lib/$file"
                Assert-That ($entries -contains $name) $name "$name is missing from the package"
            }
        }

        foreach ($name in 'README.md', 'icon.png', 'TcgDex.CSharpSdk.nuspec') {
            Assert-That ($entries -contains $name) $name "$name is missing from the package"
        }

        $pdbs = @($entries | Where-Object { $_ -like '*.pdb' })
        Assert-That ($pdbs.Count -eq 0) 'no .pdb in the .nupkg' "the .nupkg contains symbols: $($pdbs -join ', ')"

        [xml]$nuspec = Read-ZipText $zip 'TcgDex.CSharpSdk.nuspec'
        $meta = $nuspec.package.metadata
        $version = [string]$meta.version

        Assert-That ($meta.id -eq 'TcgDex.CSharpSdk') 'id TcgDex.CSharpSdk' "unexpected id '$($meta.id)'"
        Assert-That (($meta.license.type -eq 'expression') -and ($meta.license.'#text' -eq 'MIT')) 'licence MIT' 'the licence expression is not MIT'
        Assert-That ($meta.icon -eq 'icon.png') 'icon declared' 'the nuspec does not declare icon.png'
        Assert-That ($meta.readme -eq 'README.md') 'readme declared' 'the nuspec does not declare README.md'
        Assert-That ($meta.repository.commit -eq $revision) "built from commit $($revision.Substring(0, 9))" "the nuspec says commit '$($meta.repository.commit)' but this checkout is $revision"

        $groups = @($meta.dependencies.group | ForEach-Object { $_.targetFramework } | Sort-Object)
        $expectedGroups = @('.NETStandard2.0', 'net10.0', 'net8.0')
        Assert-That (($groups -join ',') -eq ($expectedGroups -join ',')) 'dependency groups for every target' "dependency groups were '$($groups -join ', ')', expected '$($expectedGroups -join ', ')'"

        # The 0.7.0 package shipped README links that only worked on GitHub.
        $readme = Read-ZipText $zip 'README.md'
        $relative = @([regex]::Matches($readme, '\]\((?!https?://|#|mailto:)[^)\s]+\)') | ForEach-Object { $_.Value })
        Assert-That ($relative.Count -eq 0) 'no repo-relative links in the packaged README' "the packaged README has links that go nowhere on nuget.org: $(($relative | Select-Object -First 5) -join ' ')"
    }
    finally {
        $zip.Dispose()
    }

    Assert-That ($snupkgs.Count -eq 1) "symbols package $($snupkgs.Name)" "expected exactly one .snupkg, found $($snupkgs.Count)"
    if ($snupkgs.Count -eq 1) {
        $symbols = [System.IO.Compression.ZipFile]::OpenRead($snupkgs[0].FullName)
        try {
            $symbolEntries = @($symbols.Entries | ForEach-Object { $_.FullName })
            foreach ($lib in 'netstandard2.0', 'net8.0', 'net10.0') {
                $name = "lib/$lib/TcgDex.CSharpSdk.pdb"
                Assert-That ($symbolEntries -contains $name) $name "$name is missing from the .snupkg"
            }
        }
        finally {
            $symbols.Dispose()
        }
    }

    # ---- 3. install it and run it ----
    # TcgDex.CSharpSdk maps to the local feed alone. nuget.org already holds this version.
    $configXml = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-feed" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local-feed"><package pattern="TcgDex.CSharpSdk" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@
    Set-Content -Path $config -Value $configXml -Encoding utf8

    $expectedTfms = if ($IsWindows) { @('net472', 'net8.0', 'net10.0') } else { @('net8.0', 'net10.0') }
    $projectDirectory = Join-Path $repo 'TcgDex.CSharpSdk.PackageSmokeTest'
    $project = Join-Path $projectDirectory 'TcgDex.CSharpSdk.PackageSmokeTest.csproj'

    # A leftover restore from an earlier run could answer for this one.
    Remove-Item -Path (Join-Path $projectDirectory 'obj'), (Join-Path $projectDirectory 'bin') -Recurse -Force -ErrorAction SilentlyContinue

    foreach ($tfm in $expectedTfms) {
        Write-Host "`n== $tfm, package $version"
        # An array, so the `--` that separates dotnet's arguments from the program's reaches
        # dotnet instead of being consumed by PowerShell.
        $runArguments = @(
            'run', '--project', $project, '--configuration', 'Release', '--framework', $tfm,
            "-p:SmokeSdkVersion=$version", "-p:RestoreConfigFile=$config", "-p:RestorePackagesPath=$packages",
            '--', $version, $revision
        )
        & dotnet @runArguments
        Assert-That ($LASTEXITCODE -eq 0) "$tfm ran against the packed package" "${tfm}: the consumer program failed (exit $LASTEXITCODE)"
    }
}
finally {
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host "`nFAILED: $($failures.Count) problem(s):"
    $failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}

Write-Host "`nThe package installs and runs on every target."

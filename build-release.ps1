<#
.SYNOPSIS
    Builds a ZipPack release: tests, self-contained publish, Velopack installer and update packages.

.DESCRIPTION
    1. Reads <Version> from ZipPackNet\ZipPackNet.csproj (the single source of truth).
    2. Runs the test suite (skip with -SkipTests).
    3. Cleans and re-creates ZipPackNet\bin\Release\net11.0-windows\publish\win-x64.
    4. Publishes ZipPackNet self-contained for win-x64 and checks that the native x64\7z.dll is included.
    5. Runs "vpk pack" into .\Releases:
         ZipPackNet-win-Setup.exe            the installer (Velopack's fixed name; installed copies update from the feed)
         ZipPackNet-<Version>-win-Setup.exe  a copy with the version in the name, for handing out
         ZipPackNet-<Version>-full.nupkg     the update package (plus -delta.nupkg when a previous release is present)
         releases.win.json                   the update feed
         ZipPackNet-win-Portable.zip         a portable copy

.PARAMETER UpdateFeedUrl
    Where installed copies look for updates (compiled into the app). A GitHub repository URL such as
    https://github.com/<owner>/ZipPack, a web folder, or a network share. Empty: updates are disabled.

.PARAMETER SkipTests
    Do not run the test suite before publishing.

.PARAMETER OutputDir
    Where the Velopack output goes. Default: .\Releases next to this script.

.EXAMPLE
    .\build-release.ps1 -UpdateFeedUrl https://github.com/OWNER/ZipPack

.NOTES
    Requires the .NET 11 SDK and the Velopack CLI with the same version as the Velopack NuGet package:
        dotnet tool install -g vpk --version <version>
    Keep earlier releases in the output folder (or download them with "vpk download github") so vpk can
    build small delta updates.
#>
[CmdletBinding()]
param(
    [string] $UpdateFeedUrl = '',
    [switch] $SkipTests,
    [string] $OutputDir = '',
    [string] $PackId = 'ZipPackNet',
    [string] $Runtime = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root       = $PSScriptRoot
# Windows PowerShell 5.1 has no $PSScriptRoot while it evaluates parameter defaults, so resolve it here.
if (-not $OutputDir) { $OutputDir = Join-Path $root 'Releases' }
$project    = Join-Path $root 'ZipPackNet\ZipPackNet.csproj'
$testProj   = Join-Path $root 'ZipPackTest\ZipPackTest.csproj'
$icon       = Join-Path $root 'ZipPackNet\Assets\app.ico'
$publishDir = Join-Path $root "ZipPackNet\bin\Release\net11.0-windows\publish\$Runtime"

function Write-Step([string] $text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

# Runs a native command and stops the script when it fails (native exit codes do not raise errors).
function Invoke-Native([string] $file, [string[]] $arguments) {
    Write-Host "    $file $($arguments -join ' ')" -ForegroundColor DarkGray
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "'$file' failed with exit code $LASTEXITCODE." }
}

function Get-ProjectProperty([xml] $xml, [string] $name) {
    foreach ($group in $xml.Project.PropertyGroup) {
        $node = $group.SelectSingleNode($name)
        if ($null -ne $node -and $node.InnerText.Trim().Length -gt 0) { return $node.InnerText.Trim() }
    }
    return $null
}

try {
    # 1. Version -------------------------------------------------------------------------------------
    Write-Step 'Reading the version'
    if (-not (Test-Path $project)) { throw "Project not found: $project" }
    [xml] $csproj = Get-Content $project -Raw
    $version = Get-ProjectProperty $csproj 'Version'
    if (-not $version) { throw "No <Version> in $project." }
    if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
        throw "<Version>$version</Version> is not a semantic version such as 1.2.3 or 1.2.3-beta.1 (required by Velopack)."
    }
    Write-Host "    ZipPack $version"

    # Tools ------------------------------------------------------------------------------------------
    Write-Step 'Checking the tools'
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET SDK (dotnet) is not on PATH.' }
    if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
        throw "The Velopack CLI (vpk) is not on PATH. Install it with: dotnet tool install -g vpk"
    }

    $packageNode = $csproj.SelectSingleNode("//PackageReference[@Include='Velopack']")
    $packageVersion = if ($null -ne $packageNode) { $packageNode.GetAttribute('Version') } else { '' }
    # vpk has no "--version" command; the .NET tool list knows which version is installed.
    $vpkVersion = ''
    $toolLine = (& dotnet tool list -g) | Where-Object { $_ -match '^\s*vpk\s+' } | Select-Object -First 1
    if ($toolLine) { $vpkVersion = ($toolLine -split '\s+' | Where-Object { $_ })[1] }
    if ($packageVersion -and $vpkVersion -and ($vpkVersion -ne $packageVersion)) {
        Write-Warning "vpk $vpkVersion differs from the Velopack package $packageVersion. Use the same version: dotnet tool update -g vpk --version $packageVersion"
    }
    if (-not $vpkVersion) { $vpkVersion = 'unknown (not a global tool)' }
    Write-Host "    dotnet $(& dotnet --version), vpk $vpkVersion, Velopack package $packageVersion"

    # vpk refuses to package a version the feed already has; say so before spending minutes on a publish.
    $feed = Join-Path $OutputDir 'releases.win.json'
    if (Test-Path $feed) {
        $existing = @((Get-Content $feed -Raw | ConvertFrom-Json).Assets | ForEach-Object { $_.Version })
        if ($existing -contains $version) {
            throw "Version $version is already in $feed. Raise <Version> in ZipPackNet.csproj, or delete the $version files from $OutputDir to rebuild it."
        }
    }

    # 2. Tests ---------------------------------------------------------------------------------------
    if (-not $SkipTests) {
        Write-Step 'Running the tests'
        Invoke-Native 'dotnet' @('test', '--project', $testProj, '-c', 'Release')
    }

    # 3. Clean ---------------------------------------------------------------------------------------
    Write-Step "Cleaning $publishDir"
    $resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    if (-not [IO.Path]::GetFullPath($publishDir).StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete $($publishDir): it is outside $($root)."
    }
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    # 4. Publish -------------------------------------------------------------------------------------
    Write-Step "Publishing $Runtime (self-contained)"
    $publishArgs = @(
        'publish', $project,
        '-c', 'Release',
        '-r', $Runtime,
        '--self-contained', 'true',
        '-p:PublishSingleFile=false',    # Velopack ships a folder; 7z.dll and Magick.Native stay real files
        '-o', $publishDir
    )
    if ($UpdateFeedUrl) { $publishArgs += "-p:UpdateFeedUrl=$UpdateFeedUrl" }
    else { Write-Warning 'No -UpdateFeedUrl: this build cannot update itself.' }
    Invoke-Native 'dotnet' $publishArgs

    $mainExe = Join-Path $publishDir 'ZipPackNet.exe'
    $sevenZip = Join-Path $publishDir 'x64\7z.dll'
    foreach ($required in @($mainExe, $sevenZip)) {
        if (-not (Test-Path $required)) { throw "The publish output is incomplete: $required is missing." }
    }
    if (-not (Get-ChildItem $publishDir -Filter 'Magick.Native-Q8-x64.dll' -Recurse -ErrorAction SilentlyContinue)) {
        Write-Warning 'Magick.Native-Q8-x64.dll was not found in the publish output; image compression would fail.'
    }
    $published = Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum
    Write-Host ("    {0} files, {1:N1} MB" -f $published.Count, ($published.Sum / 1MB))

    # 5. Pack ----------------------------------------------------------------------------------------
    Write-Step "Packing with Velopack into $OutputDir"
    New-Item -ItemType Directory -Force $OutputDir | Out-Null
    $packArgs = @(
        'pack',
        '--packId', $PackId,
        '--packVersion', $version,
        '--packDir', $publishDir,
        '--mainExe', 'ZipPackNet.exe',
        '--runtime', $Runtime,           # otherwise vpk assumes x86 for the installer and the update feed
        '--packTitle', 'ZipPack',
        '--packAuthors', 'ZipPack contributors',
        '--outputDir', $OutputDir
    )
    if (Test-Path $icon) { $packArgs += @('--icon', $icon) }
    Invoke-Native 'vpk' $packArgs

    $setup = Join-Path $OutputDir "$PackId-win-Setup.exe"
    if (-not (Test-Path $setup)) { throw "vpk did not produce $setup." }
    $versionedSetup = Join-Path $OutputDir "$PackId-$version-win-Setup.exe"
    Copy-Item $setup $versionedSetup -Force

    Write-Step "ZipPack $version is ready"
    Get-ChildItem $OutputDir -File |
        Where-Object { $_.Name -like "$PackId-*" -or $_.Name -like 'releases.*.json' -or $_.Name -eq 'RELEASES' } |
        Sort-Object Name |
        ForEach-Object { Write-Host ("    {0,-45} {1,10:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
    exit 0
}
catch {
    Write-Host "`nRelease build FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

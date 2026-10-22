<#
.SYNOPSIS
    Publishes a Ravenfield VR GitHub release that ships ONLY the setup program.

.DESCRIPTION
    One download, not three. Earlier releases carried three assets — the setup, a standalone
    launcher and the raw zip — but the setup already contains and installs the launcher, so the
    standalone launcher is redundant. This script attaches only RavenfieldVR-Setup-<version>.exe
    (add -IncludeZip to also attach the zip for manual / mod-manager installs), and removes a
    stale standalone-launcher asset from the release if one is already there.

    Needs the GitHub CLI (`gh auth login`). Build the release first: ./build.ps1 -Package

.EXAMPLE
    ./tools/publish.ps1
    ./tools/publish.ps1 -PreRelease
    ./tools/publish.ps1 -IncludeZip -NotesFile notes.md
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Draft,
    [switch]$PreRelease,
    [switch]$IncludeZip,
    [string]$NotesFile
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI 'gh' not found. Install it and run 'gh auth login' first."
}

if (-not $Version) {
    $m = Select-String -Path (Join-Path $Root 'src/Core/PluginInfo.cs') -Pattern 'Version\s*=\s*"([^"]+)"'
    if (-not $m) { throw "Could not read the version from src/Core/PluginInfo.cs; pass -Version." }
    $Version = $m.Matches[0].Groups[1].Value
}
$tag = "v$Version"
$setup = Join-Path $Root "dist/RavenfieldVR-Setup-$Version.exe"
$zip = Join-Path $Root "dist/RavenfieldVR-$Version.zip"
if (-not (Test-Path $setup)) {
    throw "Missing '$setup'. Build the release first:  ./build.ps1 -Package"
}

$assets = @($setup)
if ($IncludeZip) {
    if (-not (Test-Path $zip)) { throw "Missing '$zip' (needed for -IncludeZip)." }
    $assets += $zip
}

Write-Host "==> Publishing $tag  (only: $((($assets | Split-Path -Leaf) -join ', ')))"

# Does the release already exist?
$exists = $true
& gh release view $tag --repo (& gh repo view --json nameWithOwner --jq '.nameWithOwner') *> $null
if ($LASTEXITCODE -ne 0) { $exists = $false }

if ($exists) {
    Write-Host "    Release $tag exists - updating its assets."
    foreach ($a in $assets) {
        & gh release upload $tag $a --clobber
        if ($LASTEXITCODE -ne 0) { throw "Failed to upload $(Split-Path $a -Leaf)." }
    }
    # Remove the redundant standalone launcher asset if a previous publish left one.
    & gh release delete-asset $tag "RavenfieldVR-Launcher-$Version.exe" --yes *> $null
    if (-not $IncludeZip) {
        & gh release delete-asset $tag "RavenfieldVR-$Version.zip" --yes *> $null
    }
}
else {
    $args = @('release', 'create', $tag) + $assets + @('--title', "Ravenfield VR $Version")
    if ($Draft) { $args += '--draft' }
    if ($PreRelease) { $args += '--prerelease' }
    if ($NotesFile) { $args += @('--notes-file', $NotesFile) } else { $args += @('--notes', "Ravenfield VR $Version") }
    & gh @args
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
}

Write-Host "==> Done. Release page:"
& gh release view $tag --web

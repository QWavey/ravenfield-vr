<#
.SYNOPSIS
    Installs Ravenfield VR into your Ravenfield folder.

.DESCRIPTION
    - Finds Ravenfield through your Steam libraries (or use -GameDir).
    - Installs BepInEx 5 unless a BepInEx 5 install is already there (e.g. from RavenM), which is reused.
    - Copies the plugin, the OpenVR XR runtime (ravenfield_Data/Plugins/x86_64) and the
      UnitySubsystems manifest Unity needs to discover SteamVR at start-up.
    - Backs up every file it would overwrite and records everything it added, so uninstall.ps1 can
      put the folder back exactly as it was.

.EXAMPLE
    ./install.ps1
    ./install.ps1 -GameDir "D:/SteamLibrary/steamapps/common/Ravenfield"
#>
[CmdletBinding()]
param([string]$GameDir)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tools/common.ps1')

# Release zip layout: this script sits next to BepInEx/ and ravenfield_Data/. Dev layout: use dist/.
$Source = $PSScriptRoot
if (-not (Test-Path (Join-Path $Source 'BepInEx/plugins/RavenfieldVR/RavenfieldVR.dll'))) {
    $Source = Join-Path $PSScriptRoot 'dist/RavenfieldVR'
}
if (-not (Test-Path (Join-Path $Source 'BepInEx/plugins/RavenfieldVR/RavenfieldVR.dll'))) {
    throw "No built mod found. Run ./build.ps1 -Package first (or run install.ps1 from the release folder)."
}

$GameDir = Resolve-RavenfieldDir -GameDir $GameDir
Write-Step "Installing into $GameDir"
if (Get-Process -Name ravenfield -ErrorAction SilentlyContinue) {
    throw 'Ravenfield is running. Close it first.'
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupDir = Join-Path $GameDir "RavenfieldVR-backup/$stamp"
$added = New-Object System.Collections.Generic.List[string]
$backedUp = New-Object System.Collections.Generic.List[string]

# Upgrading over an earlier install: files we added last time are ours to overwrite (no backup), the original
# backups stay valid, and the old plugin folder is cleared so removed files don't linger.
$ours = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
$previousManifest = Join-Path $GameDir 'BepInEx/plugins/RavenfieldVR/install-manifest.txt'
$previousBackup = ''
$previousInstalledBepInEx = $false
if (Test-Path $previousManifest) {
    $prev = Get-Content $previousManifest
    foreach ($line in $prev) {
        if ($line -like 'added=*') { [void]$ours.Add(($line -replace '^added=', '')) }
        if ($line -like 'replaced=*') { $backedUp.Add(($line -replace '^replaced=', '')) }
    }
    $previousBackup = ($prev | Where-Object { $_ -like 'backup=*' } | Select-Object -First 1) -replace '^backup=', ''
    $previousInstalledBepInEx = $prev -contains 'installedBepInEx=True'
    Write-Step 'Upgrading an existing Ravenfield VR install'
    Remove-Item (Join-Path $GameDir 'BepInEx/plugins/RavenfieldVR') -Recurse -Force
}

function Install-File([string]$relative) {
    $src = Join-Path $Source $relative
    $dst = Join-Path $GameDir $relative
    if ($ours.Contains($relative)) {
        if (-not $added.Contains($relative)) { $added.Add($relative) }
        New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
        Copy-Item $src $dst -Force
        return
    }
    if (Test-Path $dst) {
        $same = (Get-FileHash $src).Hash -eq (Get-FileHash $dst).Hash
        if ($same) { return }
        if ($backedUp.Contains($relative)) {
            Copy-Item $src $dst -Force
            return
        }
        $bak = Join-Path $backupDir $relative
        New-Item -ItemType Directory -Force (Split-Path $bak) | Out-Null
        Copy-Item $dst $bak -Force
        $backedUp.Add($relative)
    }
    else {
        $added.Add($relative)
    }
    New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
    Copy-Item $src $dst -Force
}

function Get-RelativeFiles([string]$folder) {
    $base = Join-Path $Source $folder
    if (-not (Test-Path $base)) { return @() }
    Get-ChildItem $base -Recurse -File | ForEach-Object {
        $_.FullName.Substring($Source.Length).TrimStart('\', '/')
    }
}

# --- BepInEx ---------------------------------------------------------------------------------------
$existingBep = Join-Path $GameDir 'BepInEx/core/BepInEx.dll'
$installedBepInEx = $previousInstalledBepInEx
if ((Test-Path $existingBep) -and -not $previousInstalledBepInEx) {
    $ver = (Get-Item $existingBep).VersionInfo.FileVersion
    if ($ver -and $ver.StartsWith('6')) {
        throw "BepInEx $ver (version 6) is installed. Ravenfield mods such as RavenM and this mod need BepInEx 5."
    }
    Write-Step "Reusing existing BepInEx $ver"
}
else {
    Write-Step 'Installing BepInEx 5.4.23.5'
    foreach ($f in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt')) {
        if (Test-Path (Join-Path $Source $f)) { Install-File $f }
    }
    foreach ($f in Get-RelativeFiles 'BepInEx/core') { Install-File $f }
    $installedBepInEx = $true
}

# --- plugin + XR runtime ---------------------------------------------------------------------------
Write-Step 'Copying Ravenfield VR'
foreach ($f in Get-RelativeFiles 'BepInEx/plugins/RavenfieldVR') { Install-File $f }
Write-Step 'Copying the OpenVR XR runtime'
foreach ($f in Get-RelativeFiles 'ravenfield_Data') { Install-File $f }

# --- record ----------------------------------------------------------------------------------------
$manifest = Join-Path $GameDir 'BepInEx/plugins/RavenfieldVR/install-manifest.txt'
$backupRef = if ($previousBackup) { $previousBackup } elseif (Test-Path $backupDir) { $backupDir } else { '' }
$lines = @("# Ravenfield VR install $stamp", "installedBepInEx=$installedBepInEx", "backup=$backupRef")
$lines += $added | ForEach-Object { "added=$_" }
$lines += $backedUp | ForEach-Object { "replaced=$_" }
Set-Content -Path $manifest -Value $lines -Encoding UTF8

Write-Step "Done: $($added.Count) files added, $($backedUp.Count) replaced (backed up)."
Write-Host ''
Write-Host 'Start SteamVR with your headset, then launch Ravenfield from Steam.' -ForegroundColor Green
Write-Host 'If SteamVR is not running when the game starts, Ravenfield plays flat. Launch options: -vr forces VR, -novr forces flat.'
Write-Host "Settings: $GameDir\BepInEx\config\com.qwavey.ravenfieldvr.cfg (or the in-VR settings: double-click the left stick)."

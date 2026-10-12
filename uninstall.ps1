<#
.SYNOPSIS
    Removes Ravenfield VR and restores every file the installer replaced.

.DESCRIPTION
    Reads BepInEx/plugins/RavenfieldVR/install-manifest.txt written by install.ps1: deletes the files it
    added, restores the backed-up originals, and removes BepInEx only if the installer put it there.
    Your config (BepInEx/config/com.qwavey.ravenfieldvr.cfg) is kept unless -RemoveConfig is given.
#>
[CmdletBinding()]
param([string]$GameDir, [switch]$RemoveConfig)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tools/common.ps1')

$GameDir = Resolve-RavenfieldDir -GameDir $GameDir
if (Get-Process -Name ravenfield -ErrorAction SilentlyContinue) {
    throw 'Ravenfield is running. Close it first.'
}
$manifest = Join-Path $GameDir 'BepInEx/plugins/RavenfieldVR/install-manifest.txt'
if (-not (Test-Path $manifest)) {
    throw "No install manifest at $manifest; nothing to uninstall (or it was installed manually)."
}

$entries = Get-Content $manifest
$backup = ($entries | Where-Object { $_ -like 'backup=*' } | Select-Object -First 1) -replace '^backup=', ''
$installedBepInEx = ($entries -contains 'installedBepInEx=True')

foreach ($line in $entries) {
    if ($line -like 'added=*') {
        $path = Join-Path $GameDir ($line -replace '^added=', '')
        if (Test-Path $path) { Remove-Item $path -Force }
    }
}
foreach ($line in $entries) {
    if ($line -like 'replaced=*') {
        $rel = $line -replace '^replaced=', ''
        $src = Join-Path $backup $rel
        if ($backup -and (Test-Path $src)) {
            Copy-Item $src (Join-Path $GameDir $rel) -Force
        }
        else {
            Write-Warning "Backup for $rel not found; left the modded file in place."
        }
    }
}

$pluginDir = Join-Path $GameDir 'BepInEx/plugins/RavenfieldVR'
if (Test-Path $pluginDir) { Remove-Item $pluginDir -Recurse -Force }
$subsystems = Join-Path $GameDir 'ravenfield_Data/UnitySubsystems/XRSDKOpenVR'
if (Test-Path $subsystems) { Remove-Item $subsystems -Recurse -Force }
$subsystemsRoot = Join-Path $GameDir 'ravenfield_Data/UnitySubsystems'
if ((Test-Path $subsystemsRoot) -and -not (Get-ChildItem $subsystemsRoot)) { Remove-Item $subsystemsRoot -Force }

if ($RemoveConfig) {
    foreach ($cfg in 'com.qwavey.ravenfieldvr.cfg', 'RavenfieldVR.weapons.json') {
        $p = Join-Path $GameDir "BepInEx/config/$cfg"
        if (Test-Path $p) { Remove-Item $p -Force }
    }
}

if ($installedBepInEx) {
    $others = Get-ChildItem (Join-Path $GameDir 'BepInEx/plugins') -ErrorAction SilentlyContinue
    if ($others) {
        Write-Warning 'Other BepInEx plugins are installed; BepInEx itself was left in place.'
    }
    else {
        foreach ($p in 'BepInEx', 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt') {
            $full = Join-Path $GameDir $p
            if (Test-Path $full) { Remove-Item $full -Recurse -Force }
        }
        Write-Step 'Removed BepInEx (it was installed by Ravenfield VR).'
    }
}
Write-Step 'Ravenfield VR uninstalled.'

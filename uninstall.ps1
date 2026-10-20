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
if (Get-Process -Name ravenfield, RavenfieldVR-Launcher -ErrorAction SilentlyContinue) {
    throw 'Ravenfield (or its VR launcher) is running. Close it first.'
}
$manifest = Join-Path $GameDir 'BepInEx/plugins/RavenfieldVR/install-manifest.txt'
if (-not (Test-Path -LiteralPath $manifest)) {
    throw "No install manifest at $manifest; nothing to uninstall (or it was installed manually)."
}

$entries = Get-Content -LiteralPath $manifest
$backup = ($entries | Where-Object { $_ -like 'backup=*' } | Select-Object -First 1) -replace '^backup=', ''
$installedBepInEx = ($entries -contains 'installedBepInEx=True')
$loaderFiles = 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt'

# Decided before anything goes: BepInEx stays when other plugins use it or the settings are kept, and then its loader and core
# (files the installer added with it) stay too. Deleted first, BepInEx stayed but no longer loaded, and the other mods stopped
# without a word (review, 2026-10-20).
$pluginsRoot = Join-Path $GameDir 'BepInEx/plugins'
$otherPlugins = @(Get-ChildItem -LiteralPath $pluginsRoot -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne 'RavenfieldVR' }).Count -gt 0
$keepBepInEx = (-not $installedBepInEx) -or $otherPlugins -or (-not $RemoveConfig)

foreach ($line in $entries) {
    if ($line -like 'added=*') {
        $rel = $line -replace '^added=', ''
        $bepInExOwn = ($loaderFiles -contains $rel) -or ($rel -like 'BepInEx\core\*') -or ($rel -like 'BepInEx/core/*')
        if ($keepBepInEx -and $bepInExOwn) { continue }
        $path = Join-Path $GameDir $rel
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
}
foreach ($line in $entries) {
    if ($line -like 'replaced=*') {
        $rel = $line -replace '^replaced=', ''
        $src = Join-Path $backup $rel
        if ($backup -and (Test-Path -LiteralPath $src)) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $GameDir $rel) -Force
        }
        else {
            Write-Warning "Backup for $rel not found; left the modded file in place."
        }
    }
}

$pluginDir = Join-Path $GameDir 'BepInEx/plugins/RavenfieldVR'
if (Test-Path -LiteralPath $pluginDir) { Remove-Item -LiteralPath $pluginDir -Recurse -Force }
$subsystems = Join-Path $GameDir 'ravenfield_Data/UnitySubsystems/XRSDKOpenVR'
if (Test-Path -LiteralPath $subsystems) { Remove-Item -LiteralPath $subsystems -Recurse -Force }
$subsystemsRoot = Join-Path $GameDir 'ravenfield_Data/UnitySubsystems'
if ((Test-Path -LiteralPath $subsystemsRoot) -and -not (Get-ChildItem -LiteralPath $subsystemsRoot)) { Remove-Item -LiteralPath $subsystemsRoot -Force }

if ($RemoveConfig) {
    foreach ($cfg in 'com.qwavey.ravenfieldvr.cfg', 'RavenfieldVR.weapons.json') {
        $p = Join-Path $GameDir "BepInEx/config/$cfg"
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
    }
}

if ($installedBepInEx) {
    if ($keepBepInEx) {
        # Kept for the other plugins or for the settings (the config is kept unless -RemoveConfig, as the header says: the whole
        # BepInEx folder went before, the config with it; review, 2026-10-20).
        if ($otherPlugins) { Write-Warning 'Other BepInEx plugins are installed; BepInEx itself was left in place.' }
        else { Write-Step 'BepInEx left in place so your Ravenfield VR settings are kept (use -RemoveConfig to remove it too).' }
    }
    else {
        foreach ($p in @('BepInEx') + $loaderFiles) {
            $full = Join-Path $GameDir $p
            if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
        }
        Write-Step 'Removed BepInEx (it was installed by Ravenfield VR).'
    }
}
Write-Step 'Ravenfield VR uninstalled.'

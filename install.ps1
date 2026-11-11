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
# The launcher too, as the setup and uninstall.ps1 check: waiting for the headset it holds its own exe, and copying over it
# failed part way through the install (improve round, 2026-10-22).
# (The offline tests install into made-up folders of their own and set RFVR_TESTS_IN_OWN_FOLDER: the player's game may run then.)
if ($env:RFVR_TESTS_IN_OWN_FOLDER -ne '1' -and (Get-Process -Name ravenfield, RavenfieldVR-Launcher -ErrorAction SilentlyContinue)) {
    throw 'Ravenfield (or its VR launcher) is running. Close it first.'
}

# A path from the install record, only if it stays inside the game folder (a rooted or '..' entry would reach outside it), as
# uninstall.ps1 and the setup take them: the clean-up of files an earlier version added deleted whatever the record named.
function Resolve-Inside([string]$root, [string]$rel) {
    if ([string]::IsNullOrWhiteSpace($rel) -or [System.IO.Path]::IsPathRooted($rel)) { Write-Warning "Skipped record entry '$rel' (not a path inside the game folder)."; return $null }
    $full = [System.IO.Path]::GetFullPath((Join-Path $root $rel))
    $base = [System.IO.Path]::GetFullPath($root).TrimEnd([char]'\', [char]'/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) { Write-Warning "Skipped record entry '$rel' (outside $root)."; return $null }
    return $full
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
    # Cleared but for the install record: an upgrade that fails part way still leaves what the install added and
    # replaced, so uninstall can put the game back.
    Get-ChildItem (Join-Path $GameDir 'BepInEx/plugins/RavenfieldVR') -Force |
        Where-Object { $_.Name -ne 'install-manifest.txt' } | Remove-Item -Recurse -Force
    # Backed up into the install's own backup folder: the record names one folder, and an original first replaced by an
    # upgrade went into a new one that uninstall never looked in.
    # Setup writes it relative to the game folder: resolved there, not in the working directory.
    if ($previousBackup -and -not [System.IO.Path]::IsPathRooted($previousBackup)) { $previousBackup = Join-Path $GameDir $previousBackup }
    # An older record's absolute folder that is no longer there (the game moved to another drive since): a fresh one in the game
    # folder, as the setup does. Kept, every backup of this install failed and it stopped part way, each time (installer scout,
    # 2026-11-11).
    if ($previousBackup -and [System.IO.Path]::IsPathRooted($previousBackup) -and -not (Test-Path -LiteralPath $previousBackup)) {
        Write-Warning "The earlier install's backup folder $previousBackup is gone; this install backs up into $backupDir."
        $previousBackup = ''
    }
    if ($previousBackup) { $backupDir = $previousBackup }
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

# --- record ----------------------------------------------------------------------------------------
# Written however the copy ends: one that stopped part way (a locked file, a full disk) wrote no record, so uninstall could not
# put back what it had replaced, and the next install took the first run's backups for game files (round-7 scout).
function Write-Record {
    $manifest = Join-Path $GameDir 'BepInEx/plugins/RavenfieldVR/install-manifest.txt'
    New-Item -ItemType Directory -Force (Split-Path $manifest) | Out-Null
    $backupRef = if ($previousBackup) { $previousBackup } elseif (Test-Path $backupDir) { $backupDir } else { '' }
    # Relative to the game folder, as the setup writes it: an absolute path broke the restore of a game folder moved since.
    $gameRoot = [System.IO.Path]::GetFullPath($GameDir).TrimEnd([char]'\', [char]'/') + [System.IO.Path]::DirectorySeparatorChar
    if ($backupRef -and [System.IO.Path]::GetFullPath($backupRef).StartsWith($gameRoot, [StringComparison]::OrdinalIgnoreCase)) {
        $backupRef = [System.IO.Path]::GetFullPath($backupRef).Substring($gameRoot.Length)
    }
    $dll = Join-Path $Source 'BepInEx/plugins/RavenfieldVR/RavenfieldVR.dll'
    $modVersion = if (Test-Path $dll) { (Get-Item $dll).VersionInfo.FileVersion } else { '' }
    $lines = @("# Ravenfield VR install $stamp", "version=$modVersion", "installedBepInEx=$installedBepInEx", "backup=$backupRef")
    $lines += $added | ForEach-Object { "added=$_" }
    # Stopped part way: the earlier install's files not reached yet are still in the game and still ours (left out, uninstall left
    # them behind, and the next install took them for the player's own and backed them up; installer scout, 2026-11-11).
    if (-not $script:completed) {
        $lines += $ours | Where-Object { -not ($added -contains $_) } | ForEach-Object { "added=$_" }
    }
    $lines += $backedUp | ForEach-Object { "replaced=$_" }
    # Written beside it and then put in its place: written in place, a full disk or a lock left it cut short, and the originals it
    # names were never put back (installer scout, 2026-11-11).
    $temp = "$manifest.tmp"
    Set-Content -Path $temp -Value $lines -Encoding UTF8
    Move-Item -LiteralPath $temp -Destination $manifest -Force
}

$script:completed = $false

# --- BepInEx ---------------------------------------------------------------------------------------
$existingBep = Join-Path $GameDir 'BepInEx/core/BepInEx.dll'
$installedBepInEx = $previousInstalledBepInEx
try {
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
if (Test-Path (Join-Path $Source 'RavenfieldVR-Launcher.exe')) {
    Write-Step 'Copying the launcher'
    Install-File 'RavenfieldVR-Launcher.exe'
}

# Files an earlier version added that this one no longer has were the install's: they go.
foreach ($old in $ours) {
    if (-not ($added -contains $old)) {
        $path = Resolve-Inside $GameDir $old
        if ($path -and (Test-Path -LiteralPath $path -PathType Leaf)) {
            Remove-Item -LiteralPath $path -Force
            Write-Step "Removed $old (no longer part of Ravenfield VR)"
        }
    }
}
$script:completed = $true

}
finally {
    Write-Record
}

Write-Step "Done: $($added.Count) files added, $($backedUp.Count) replaced (backed up)."
Write-Host ''
Write-Host 'Start SteamVR with your headset, then launch Ravenfield from Steam.' -ForegroundColor Green
Write-Host 'If SteamVR is not running when the game starts, Ravenfield plays flat. Launch options: -vr forces VR, -novr forces flat.'
Write-Host "Settings: $GameDir\BepInEx\config\com.qwavey.ravenfieldvr.cfg (or the in-VR settings: double-click the left stick)."

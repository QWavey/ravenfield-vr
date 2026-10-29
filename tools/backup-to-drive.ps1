<#
.SYNOPSIS
    Backs up Ravenfield-VR to a Google Drive folder ("Repos backup 2026-10-28" by default) with rclone.

.DESCRIPTION
    First run only: installs rclone with winget if it is missing (you confirm winget's prompt), then opens Google's sign-in page in
    your browser once so you can allow rclone to use your Drive. rclone gets full Drive access: it has to write into a folder you
    made yourself, which its narrower "files it created" access cannot. The sign-in is kept in rclone's own config
    (%APPDATA%\rclone\rclone.conf) for later runs; remove it with `rclone config delete gdrive`.

    Every run uploads into  <folder>/Ravenfield-VR/<yyyy-MM-dd HHmm> <commit>/ :
      patches/      one file per commit since the last backup (git format-patch: readable, and `git am` applies them)
      *.bundle      the whole repository with its history the first time (or with -Full); after that only the commits since the
                    last backup. Restore: `git clone Ravenfield-VR-full.bundle Ravenfield-VR`, then in it
                    `git pull <each later bundle, oldest first> <branch>`
      dist/         the newest installer and its release notes, when they are new since the last backup
      README.txt    what is in it and how to restore
    Only committed work is backed up (uncommitted changes are listed in README.txt but not sent). The last backed-up commit and
    installer are remembered in .git/drive-backup-last and .git/drive-backup-dist, so the next run sends only what is new.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/backup-to-drive.ps1
    powershell -ExecutionPolicy Bypass -File tools/backup-to-drive.ps1 -DryRun   # makes the files, shows what would be uploaded
    powershell -ExecutionPolicy Bypass -File tools/backup-to-drive.ps1 -Full     # a whole-repository bundle again
    powershell -ExecutionPolicy Bypass -File tools/backup-to-drive.ps1 -LocalOnly  # only makes the files (no rclone, no upload)
#>
[CmdletBinding()]
param(
    [string]$FolderId = '1Ctwccw597vFFEZ0cogVxUx-yTt6SwYD4',
    [string]$Remote = 'gdrive',
    [switch]$Full,
    [switch]$DryRun,
    [switch]$LocalOnly
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent

function Write-Step([string]$text) { Write-Host "==> $text" }

function Invoke-Git([string[]]$GitArgs) {
    $out = & git.exe -C $Root @GitArgs
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed" }
    return $out
}

# A git call that may fail (its message hidden): Windows PowerShell turns a native command's error output sent to $null into an
# error, which 'Stop' made end the script.
function Test-Git([string[]]$GitArgs) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git.exe -C $Root @GitArgs 2>$null
        return [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0); Out = $out }
    } finally {
        $ErrorActionPreference = $prev
    }
}

# --- rclone, installed and signed in once ------------------------------------------------------
function Find-Rclone {
    $cmd = Get-Command rclone -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $link = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\rclone.exe' # (winget's link: not on this session's PATH yet)
    if (Test-Path $link) { return $link }
    $pkg = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Recurse -Filter rclone.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pkg) { return $pkg.FullName }
    return $null
}

$rclone = if ($LocalOnly) { $null } else { Find-Rclone }
if (-not $rclone -and -not $LocalOnly) {
    Write-Step 'rclone is not installed: installing it with winget (Rclone.Rclone, from winget''s catalog)'
    & winget install --id Rclone.Rclone --exact
    $rclone = Find-Rclone
    if (-not $rclone) { throw 'rclone was not found after winget. Open a new terminal and run this script again.' }
}
if (-not $LocalOnly) {
    Write-Step "rclone: $rclone"
}

$remotes = if ($LocalOnly) { @("${Remote}:") } else { & $rclone listremotes }
if (-not ($remotes -contains "${Remote}:")) {
    Write-Step "Signing in to Google Drive for rclone: allow it in the browser page that opens (once)"
    & $rclone config create $Remote drive scope=drive
    if ($LASTEXITCODE -ne 0) { throw 'The Google Drive sign-in for rclone did not finish.' }
}

# The repository's own git folder: in a linked worktree .git is a file naming it, and the state files under '.git' could not be
# written there (the upload done, the run failed and remembered nothing; round 12).
$GitDir = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($Root, "$(Invoke-Git @('rev-parse', '--git-dir'))".Trim()))

# A state file's line, or '' when it is missing or empty (an empty one read as $null and stopped every run; round 12).
function Read-State([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    $raw = Get-Content -LiteralPath $path -Raw
    if ($raw) { return $raw.Trim() }
    return ''
}

# --- what is new since the last backup ---------------------------------------------------------
$head = (Invoke-Git @('rev-parse', 'HEAD')).Trim()
$short = $head.Substring(0, 7)
$branch = (Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')).Trim()
$lastFile = Join-Path $GitDir 'drive-backup-last'
$distFile = Join-Path $GitDir 'drive-backup-dist'
$last = Read-State $lastFile
if ($last -and -not (Test-Git @('cat-file', '-e', "$last^{commit}")).Ok) {
    $last = '' # (rewritten history: start again with a whole bundle)
}
# The last backup not behind HEAD (another branch checked out, a reset): its commits since it make no bundle (git refuses an
# empty one, and the run failed every time until a commit passed the remembered one; round 12): a whole bundle then.
if ($last -and $last -ne $head -and -not (Test-Git @('merge-base', '--is-ancestor', $last, $head)).Ok) {
    $last = ''
}
$whole = $Full -or -not $last

$setup = Get-ChildItem (Join-Path $Root 'dist') -Filter 'RavenfieldVR-Setup-*.exe' -ErrorAction SilentlyContinue |
    Sort-Object { try { [version]($_.BaseName -replace '^RavenfieldVR-Setup-', '') } catch { [version]'0.0' } } | Select-Object -Last 1
$setupKey = if ($setup) { "$($setup.Name) $($setup.Length) $($setup.LastWriteTimeUtc.Ticks)" } else { '' }
$lastDist = Read-State $distFile
$newDist = $setup -and $setupKey -ne $lastDist

if (-not $whole -and $last -eq $head -and -not $newDist) {
    Write-Step "Nothing new since the last backup ($short)."
    exit 0
}

$stamp = Get-Date -Format 'yyyy-MM-dd HHmm'
$name = "$stamp $short"
$work = Join-Path $env:TEMP "rfvr-drive-backup-$(Get-Date -Format 'yyyyMMddHHmmss')"
New-Item -ItemType Directory -Force (Join-Path $work 'patches') | Out-Null

# Patches: one a commit since the last backup (the first time: since the newest release tag).
$base = $last
if (-not $base) {
    $tag = Test-Git @('describe', '--tags', '--abbrev=0')
    if ($tag.Ok -and $tag.Out) { $base = "$($tag.Out)".Trim() }
}
$patchCount = 0
if ($base -and $base -ne $head) {
    Invoke-Git @('format-patch', '--quiet', "$base..HEAD", '-o', (Join-Path $work 'patches')) | Out-Null
    $patchCount = (Get-ChildItem (Join-Path $work 'patches') -Filter *.patch).Count
}
Write-Step "Patches: $patchCount commit(s) since $(if ($base) { $base } else { 'the start' })"

# Bundle: the whole repository, or the commits since the last backup.
if ($whole) {
    $bundle = Join-Path $work 'Ravenfield-VR-full.bundle'
    Invoke-Git @('bundle', 'create', '--quiet', $bundle, '--all') | Out-Null
} elseif ($last -ne $head) {
    $bundle = Join-Path $work "Ravenfield-VR-since-$($last.Substring(0, 7)).bundle"
    Invoke-Git @('bundle', 'create', '--quiet', $bundle, $branch, "^$last") | Out-Null
} else {
    $bundle = $null
}
if ($bundle) { Write-Step ("Bundle: {0} ({1:N1} MB)" -f (Split-Path $bundle -Leaf), ((Get-Item $bundle).Length / 1MB)) }

# The installer and its notes, when new.
if ($newDist) {
    New-Item -ItemType Directory -Force (Join-Path $work 'dist') | Out-Null
    Copy-Item $setup.FullName (Join-Path $work 'dist')
    $version = $setup.BaseName -replace '^RavenfieldVR-Setup-', ''
    $notes = Join-Path $Root "dist/notes-$version.md"
    if (Test-Path $notes) { Copy-Item $notes (Join-Path $work 'dist') }
    Write-Step "Installer: $($setup.Name)"
}

# README: what is here and how to restore it.
$log = if ($base -and $base -ne $head) { Invoke-Git @('log', '--format=%h %ad %s', '--date=short', "$base..HEAD") } else { @() }
$dirty = Invoke-Git @('status', '--short', '--untracked-files=no')
$readme = @(
    "Ravenfield-VR backup $stamp",
    "Branch $branch at $head",
    '',
    'Restore:',
    '  git clone Ravenfield-VR-full.bundle Ravenfield-VR       (from the first backup, or one made with -Full)',
    "  cd Ravenfield-VR; git pull <later bundle> $branch          (each later bundle, oldest first)",
    '  or apply the patches in order on a checkout of their base: git am patches/*.patch',
    '',
    "Commits in this backup ($patchCount):"
) + $log + @('', 'Not backed up (uncommitted changes in the working tree):') + $(if ($dirty) { $dirty } else { @('  none') })
Set-Content -Path (Join-Path $work 'README.txt') -Value $readme -Encoding UTF8

# --- upload -------------------------------------------------------------------------------------
if ($LocalOnly) {
    Write-Step "Local only: the backup is in $work (nothing uploaded, nothing remembered)"
    exit 0
}
$dest = "${Remote}:Ravenfield-VR/$name"
Write-Step "Uploading to Google Drive: Ravenfield-VR/$name"
$rcloneArgs = @('copy', $work, $dest, '--drive-root-folder-id', $FolderId, '--progress')
if ($DryRun) { $rcloneArgs += '--dry-run' }
& $rclone @rcloneArgs
if ($LASTEXITCODE -ne 0) { throw "rclone copy failed (the files are kept in $work)" }

if ($DryRun) {
    Write-Step "Dry run: nothing uploaded, nothing remembered (the files are in $work)"
    exit 0
}
Set-Content -Path $lastFile -Value $head -Encoding ASCII
if ($newDist) { Set-Content -Path $distFile -Value $setupKey -Encoding ASCII }
Remove-Item -Recurse -Force $work
Write-Step "Backed up $short to Drive (Ravenfield-VR/$name)."

# After a Ravenfield update: is the mod still sound against the new game?
#   1. the game's Assembly-CSharp.dll against the one the mod was last checked with (.ref/game-dll.sha256)
#   2. a changed one decompiled into .ref/decompiled-new (ilspycmd in tools/ilspy, run with dotnet)
#   3. every Harmony patch and reflected member named by a string checked against it (tools/tests/harmony_targets_test.py)
#   4. the mod compiled against the new DLL (build.ps1: nameof() targets and every call the compiler sees)
#   5. the game classes the mod patches or names, listed where the new decompile differs from the old one
# Nothing is installed and the game is not started. With -Accept the new decompile replaces .ref/decompiled and the new hash is kept.
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-game-update.ps1 [-Accept] [-Force]
param([switch]$Accept, [switch]$Force)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'common.ps1')
$game = Resolve-RavenfieldDir ''
$dll = Join-Path $game 'ravenfield_Data\Managed\Assembly-CSharp.dll'
$first = Join-Path $game 'ravenfield_Data\Managed\Assembly-CSharp-firstpass.dll'
$ref = Join-Path $root '.ref'
$hashFile = Join-Path $ref 'game-dll.sha256'
$hash = (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant()
$known = if (Test-Path $hashFile) { (Get-Content $hashFile -Raw).Trim() } else { '' }
$manifest = Get-ChildItem (Split-Path (Split-Path $game -Parent) -Parent) -Filter 'appmanifest_636480.acf' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($manifest) {
    $build = (Select-String -Path $manifest.FullName -Pattern '"buildid"\s+"(\d+)"').Matches.Groups[1].Value
    $updated = (Select-String -Path $manifest.FullName -Pattern '"LastUpdated"\s+"(\d+)"').Matches.Groups[1].Value
    Write-Step ("Steam build {0}, installed {1}" -f $build, ([DateTimeOffset]::FromUnixTimeSeconds([long]$updated).LocalDateTime))
}
if ($hash -eq $known -and -not $Force) {
    Write-Step "Assembly-CSharp.dll is the one the mod was checked against ($($hash.Substring(0, 12))): nothing to do."
    exit 0
}
Write-Step "Assembly-CSharp.dll changed ($($hash.Substring(0, 12)), checked against $(if ($known) { $known.Substring(0, 12) } else { 'nothing yet' }))."

$ilspy = Join-Path $root 'tools\ilspy\pkg\tools\net10.0\any\ilspycmd.dll'
$new = Join-Path $ref 'decompiled-new'
if (Test-Path $new) { Remove-Item -Recurse -Force $new }
New-Item -ItemType Directory -Force (Join-Path $new 'Assembly-CSharp'), (Join-Path $new 'firstpass') | Out-Null
Write-Step 'Decompiling the new game'
& dotnet $ilspy -p -o (Join-Path $new 'Assembly-CSharp') $dll | Out-Null
if ($LASTEXITCODE -ne 0) { throw "ilspycmd failed on $dll" }
if (Test-Path $first) {
    & dotnet $ilspy -p -o (Join-Path $new 'firstpass') $first | Out-Null
}

$failures = 0
Write-Step 'Harmony targets and reflected members against the new game'
$env:RFVR_DECOMPILED = $new
& python -I (Join-Path $root 'tools\tests\harmony_targets_test.py')
if ($LASTEXITCODE -ne 0) { $failures++ }
Remove-Item Env:RFVR_DECOMPILED

Write-Step 'The mod compiled against the new DLL'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { $failures++ }

Write-Step 'Game classes the mod names that changed'
$names = New-Object System.Collections.Generic.HashSet[string]
Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.cs | ForEach-Object {
    $text = [IO.File]::ReadAllText($_.FullName)
    foreach ($m in [regex]::Matches($text, 'typeof\(\s*([A-Z][A-Za-z0-9_]*)\s*\)')) { [void]$names.Add($m.Groups[1].Value) }
}
$old = Join-Path $ref 'decompiled\Assembly-CSharp'
$changed = @()
foreach ($n in $names) {
    $a = Get-ChildItem $old -Recurse -Filter "$n.cs" -ErrorAction SilentlyContinue | Select-Object -First 1
    $b = Get-ChildItem (Join-Path $new 'Assembly-CSharp') -Recurse -Filter "$n.cs" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($a -and -not $b) { $changed += "$n (GONE)"; continue }
    if ($a -and $b -and (Get-FileHash $a.FullName).Hash -ne (Get-FileHash $b.FullName).Hash) { $changed += $n }
}
if ($changed.Count -eq 0) { Write-Host '  none' } else { $changed | Sort-Object | ForEach-Object { Write-Host "  $_" } }
Write-Host "  (read each against the mod's patches on it: git diff --no-index .ref/decompiled/Assembly-CSharp/<Name>.cs .ref/decompiled-new/Assembly-CSharp/<Name>.cs)"

if ($failures -gt 0) {
    Write-Step "$failures check(s) FAILED against the new game: see above. Nothing replaced."
    exit 1
}
if ($Accept) {
    Remove-Item -Recurse -Force (Join-Path $ref 'decompiled')
    Rename-Item $new 'decompiled'
    Set-Content -Path $hashFile -Value $hash -Encoding ascii
    Write-Step 'Accepted: .ref/decompiled is the new game now, its hash kept.'
}
else {
    Write-Step 'All checks passed. Read the changed classes, then run again with -Accept.'
}

<#
.SYNOPSIS
    Builds and runs the checks that need no game (tools/tests/OfflineTests.cs): the installer's path containment, the Steam
    config reader and the left-handed bindings. Exit code 0 when all pass.
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\common.ps1')
$Root = Split-Path (Split-Path $PSScriptRoot)
$out = Join-Path $Root 'build\tests'
New-Item -ItemType Directory -Force $out | Out-Null
$exe = Join-Path $out 'OfflineTests.exe'
$fx = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
# The mod's own pure logic is checked too, through the built RavenfieldVR.dll with Unity's managed core (vector maths only:
# Unity's native calls do not run outside the game).
$Game = Resolve-RavenfieldDir -GameDir ''
$Managed = Join-Path $Game 'ravenfield_Data/Managed'
$BepInExCore = Join-Path $Game 'BepInEx/core'
Invoke-Csc @(
    '-nologo', '-target:exe', '-langversion:latest', '-nostdlib', '-noconfig', "-out:$exe",
    '-main:RavenfieldVR.Tests.OfflineTests',
    "-r:$fx/mscorlib.dll", "-r:$fx/System.dll", "-r:$fx/System.Core.dll", "-r:$fx/System.Drawing.dll",
    "-r:$fx/System.Windows.Forms.dll", "-r:$fx/System.IO.Compression.dll", "-r:$fx/System.IO.Compression.FileSystem.dll",
    "-r:$Managed/UnityEngine.CoreModule.dll", "-r:$BepInExCore/BepInEx.dll",
    (Join-Path $PSScriptRoot 'OfflineTests.cs'),
    (Join-Path $Root 'installer/Setup.cs'), (Join-Path $Root 'installer/SteamLaunchOption.cs'), (Join-Path $Root 'installer/SteamPlayEntry.cs'),
    (Join-Path $Root 'src/Input/BindingsWriter.cs'),
    # The Steam launcher (it edits the mod's config and builds the game's command line on every launch), with Valve's bindings it uses.
    (Join-Path $Root 'installer/Launcher.cs'), (Join-Path $Root 'tools/openvr-xr/package/Runtime/openvr_api.cs')
)
& $exe $Managed (Join-Path $Root 'build/RavenfieldVR.dll') $BepInExCore
$code = $LASTEXITCODE
# The picture tool's restore after a killed run, on temporary copies (tools/tests/vrshots_restore_test.py).
python -I (Join-Path $PSScriptRoot 'vrshots_restore_test.py')
if ($LASTEXITCODE -ne 0) { $code = 1 }
# Every controller binds every button action, but for the gaps listed on purpose (tools/tests/bindings_coverage_test.py).
python -I (Join-Path $PSScriptRoot 'bindings_coverage_test.py')
if ($LASTEXITCODE -ne 0) { $code = 1 }
# Every failure a self-check writes in capitals counts as a problem in the autotest (tools/tests/failure_phrases_test.py).
python -I (Join-Path $PSScriptRoot 'failure_phrases_test.py')
if ($LASTEXITCODE -ne 0) { $code = 1 }
# No doc comment left on another member's, no private member nothing uses (both cleaned to zero on 2026-10-22).
$stacked = python -I (Join-Path $Root 'tools/export_checks/stacked_docs.py') --brief
$stacked | Select-Object -Last 1
if (($stacked | Select-Object -Last 1) -notmatch '^0 ') { $stacked | Select-Object -SkipLast 1; $code = 1 }
$unused = python -I (Join-Path $Root 'tools/export_checks/unused_private.py')
$unused | Select-Object -Last 1
if (($unused | Select-Object -Last 1) -notmatch '^0 ') { $unused | Select-Object -SkipLast 1; $code = 1 }
# The magazine-line table against the sandbox export, in Unity's space (it once shipped mirrored in z: 134-178 degrees off in
# game). Needs the local export (reports/sandbox/data, not in the repository): skipped without it.
if (Test-Path (Join-Path $Root 'reports/sandbox/data/weapons')) {
    python -I (Join-Path $Root 'tools/export_checks/mag_line_table_check.py') | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { $code = 1 }
} else {
    Write-Host 'Magazine-line table check skipped (no sandbox export in reports/sandbox/data)'
}
exit $code

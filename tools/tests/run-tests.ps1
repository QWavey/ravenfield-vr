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
    (Join-Path $Root 'src/Input/BindingsWriter.cs')
)
& $exe $Managed (Join-Path $Root 'build/RavenfieldVR.dll') $BepInExCore
$code = $LASTEXITCODE
# The picture tool's restore after a killed run, on temporary copies (tools/tests/vrshots_restore_test.py).
python -I (Join-Path $PSScriptRoot 'vrshots_restore_test.py')
if ($LASTEXITCODE -ne 0) { $code = 1 }
# Every controller binds every button action, but for the gaps listed on purpose (tools/tests/bindings_coverage_test.py).
python -I (Join-Path $PSScriptRoot 'bindings_coverage_test.py')
if ($LASTEXITCODE -ne 0) { $code = 1 }
exit $code

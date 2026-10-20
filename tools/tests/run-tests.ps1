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
Invoke-Csc @(
    '-nologo', '-target:exe', '-langversion:latest', '-nostdlib', '-noconfig', "-out:$exe",
    '-main:RavenfieldVR.Tests.OfflineTests',
    "-r:$fx/mscorlib.dll", "-r:$fx/System.dll", "-r:$fx/System.Core.dll", "-r:$fx/System.Drawing.dll",
    "-r:$fx/System.Windows.Forms.dll", "-r:$fx/System.IO.Compression.dll", "-r:$fx/System.IO.Compression.FileSystem.dll",
    (Join-Path $PSScriptRoot 'OfflineTests.cs'),
    (Join-Path $Root 'installer/Setup.cs'), (Join-Path $Root 'installer/SteamLaunchOption.cs'), (Join-Path $Root 'installer/SteamPlayEntry.cs'),
    (Join-Path $Root 'src/Input/BindingsWriter.cs')
)
& $exe
exit $LASTEXITCODE

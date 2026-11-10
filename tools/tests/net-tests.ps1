<#
.SYNOPSIS
    Builds and runs the multiplayer's checks that need no game (tools/tests/NetTests.cs with src/Net's plain files: the wire format,
    the reliable channel, the session over a network in memory, UDP over this PC's loopback). Exit code 0 when all pass.
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\common.ps1')
$Root = Split-Path (Split-Path $PSScriptRoot)
$out = Join-Path $Root 'build\tests'
New-Item -ItemType Directory -Force $out | Out-Null
$exe = Join-Path $out 'NetTests.exe'
$fx = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$Game = Resolve-RavenfieldDir -GameDir ''
$Managed = Join-Path $Game 'ravenfield_Data/Managed'
Invoke-Csc @(
    '-nologo', '-target:exe', '-langversion:latest', '-nostdlib', '-noconfig', "-out:$exe", '-main:RavenfieldVR.Tests.NetTests',
    "-r:$fx/mscorlib.dll", "-r:$fx/System.dll", "-r:$fx/System.Core.dll", "-r:$Managed/UnityEngine.CoreModule.dll",
    (Join-Path $PSScriptRoot 'NetTests.cs'),
    (Join-Path $Root 'src/Net/NetWire.cs'), (Join-Path $Root 'src/Net/NetTransport.cs'), (Join-Path $Root 'src/Net/NetReliable.cs'),
    (Join-Path $Root 'src/Net/NetSession.cs'), (Join-Path $Root 'src/Net/NetUdp.cs')
)
& $exe $Managed
exit $LASTEXITCODE

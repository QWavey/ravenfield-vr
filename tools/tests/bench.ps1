<#
.SYNOPSIS
    Builds and runs the offline timing of GunVolume's per-point solid tests (tools/tests/GunVolumeBench.cs) on the built
    RavenfieldVR.dll. Prints ns per call and the hit count (the count must not change when the code gets faster).
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\common.ps1')
$Root = Split-Path (Split-Path $PSScriptRoot)
$out = Join-Path $Root 'build\tests'
New-Item -ItemType Directory -Force $out | Out-Null
$exe = Join-Path $out 'GunVolumeBench.exe'
$fx = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$Managed = Join-Path (Resolve-RavenfieldDir -GameDir '') 'ravenfield_Data/Managed'
Invoke-Csc @(
    '-nologo', '-target:exe', '-optimize+', '-langversion:latest', '-nostdlib', '-noconfig', "-out:$exe",
    "-r:$fx/mscorlib.dll", "-r:$fx/System.dll", "-r:$fx/System.Core.dll", "-r:$Managed/UnityEngine.CoreModule.dll",
    (Join-Path $PSScriptRoot 'GunVolumeBench.cs')
)
& $exe $Managed (Join-Path $Root 'build/RavenfieldVR.dll')
exit $LASTEXITCODE

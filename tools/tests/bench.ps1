<#
.SYNOPSIS
    Builds and runs the offline timings on the built RavenfieldVR.dll: GunVolume's per-point solid tests
    (tools/tests/GunVolumeBench.cs), the glove skin's nearness test (SkinBench.cs) and the collider fit (ShapeFitBench.cs).
    Each prints its time and a count or checksum that must not change when the code gets faster.
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
$code = $LASTEXITCODE
# The glove skin's nearness test (HandModel.NearSkin), as copies before and after its bounding sphere (tools/tests/SkinBench.cs).
$skin = Join-Path $out 'SkinBench.exe'
Invoke-Csc @(
    '-nologo', '-target:exe', '-optimize+', '-langversion:latest', '-nostdlib', '-noconfig', "-out:$skin",
    "-r:$fx/mscorlib.dll", "-r:$fx/System.dll", "-r:$fx/System.Core.dll",
    (Join-Path $PSScriptRoot 'SkinBench.cs')
)
& $skin
if ($LASTEXITCODE -ne 0) { $code = 1 }
# A weapon's collider pieces (ShapeFit, on the main thread when it is let go): its time and every output point's checksum
# (tools/tests/ShapeFitBench.cs; the checksum must not change when the code gets faster).
$fit = Join-Path $out 'ShapeFitBench.exe'
Invoke-Csc @(
    '-nologo', '-target:exe', '-optimize+', '-langversion:latest', '-nostdlib', '-noconfig', "-out:$fit",
    "-r:$fx/mscorlib.dll", "-r:$fx/System.dll", "-r:$fx/System.Core.dll", "-r:$Managed/UnityEngine.CoreModule.dll",
    (Join-Path $PSScriptRoot 'ShapeFitBench.cs')
)
& $fit $Managed (Join-Path $Root 'build/RavenfieldVR.dll')
if ($LASTEXITCODE -ne 0) { $code = 1 }
exit $code

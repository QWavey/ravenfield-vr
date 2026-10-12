<#
.SYNOPSIS
    Downloads the third-party build/runtime dependencies into tools/ (not committed to git).

    BepInEx 5.4.23.5 (x64)            github.com/BepInEx/BepInEx            LGPL-2.1
    OpenVR Unity XR plugin 1.2.4      github.com/ValveSoftware/unity-xr-plugin  BSD-3 (+ Unity Companion headers)
    Microsoft.Net.Compilers.Toolset   nuget.org (Roslyn C# compiler)        MIT
    ilspycmd (optional, -WithDecompiler) nuget.org                          MIT
#>
[CmdletBinding()]
param([switch]$WithDecompiler, [switch]$Force)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$deps = @(
    @{ Name = 'bepinex'; Url = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip'; File = 'BepInEx_win_x64_5.4.23.5.zip'; Kind = 'zip'; Check = 'pkg/BepInEx/core/BepInEx.dll' },
    @{ Name = 'openvr-xr'; Url = 'https://github.com/ValveSoftware/unity-xr-plugin/releases/download/v1.2.4/com.valvesoftware.unity.openvr-1.2.4.tgz'; File = 'com.valvesoftware.unity.openvr-1.2.4.tgz'; Kind = 'tgz'; Check = 'package/Runtime/x64/XRSDKOpenVR.dll' },
    @{ Name = 'roslyn'; Url = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/5.9.0/microsoft.net.compilers.toolset.5.9.0.nupkg'; File = 'toolset.nupkg'; Kind = 'zip'; Check = 'pkg/tasks/netcore/bincore/csc.dll' }
)
if ($WithDecompiler) {
    $deps += @{ Name = 'ilspy'; Url = 'https://api.nuget.org/v3-flatcontainer/ilspycmd/11.1.0.9782/ilspycmd.11.1.0.9782.nupkg'; File = 'ilspycmd.nupkg'; Kind = 'zip'; Check = 'pkg/tools/net10.0/any/ilspycmd.dll' }
}

foreach ($d in $deps) {
    $dir = Join-Path $PSScriptRoot $d.Name
    if (-not $Force -and (Test-Path (Join-Path $dir $d.Check))) {
        Write-Step "$($d.Name): present"
        continue
    }
    New-Item -ItemType Directory -Force $dir | Out-Null
    $archive = Join-Path $dir $d.File
    Write-Step "$($d.Name): downloading $($d.Url)"
    Invoke-WebRequest -Uri $d.Url -OutFile $archive -UseBasicParsing
    if ($d.Kind -eq 'zip') {
        $pkg = Join-Path $dir 'pkg'
        if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
        # .nupkg files are zips; Expand-Archive insists on the .zip extension.
        $zipCopy = "$archive.zip"
        Copy-Item $archive $zipCopy -Force
        Expand-Archive -Path $zipCopy -DestinationPath $pkg -Force
        Remove-Item $zipCopy
    }
    else {
        tar -xzf $archive -C $dir
        if ($LASTEXITCODE -ne 0) { throw "tar failed on $archive" }
    }
}
Write-Step 'Dependencies ready.'

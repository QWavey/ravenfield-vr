<#
.SYNOPSIS
    Builds the Ravenfield VR plugin and (optionally) a ready-to-install release folder/zip.

.DESCRIPTION
    No .NET SDK is required. The script uses the Roslyn compiler from the
    Microsoft.Net.Compilers.Toolset package (tools/roslyn) running on any installed
    .NET runtime, and compiles against the game's own Managed assemblies.

    Steps:
      1. Locate Ravenfield (Steam library scan or -GameDir).
      2. Build tools/Publicizer (once) and publicize Assembly-CSharp(-firstpass) into build/refs.
      3. Compile src/**/*.cs + Valve's openvr_api.cs into build/RavenfieldVR.dll.
      4. With -Package: lay out dist/RavenfieldVR (BepInEx + plugin + XR runtime) and zip it.

.EXAMPLE
    ./build.ps1
    ./build.ps1 -Package
    ./build.ps1 -GameDir "D:/SteamLibrary/steamapps/common/Ravenfield" -Configuration Debug
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
. (Join-Path $Root 'tools/common.ps1')

$GameDir = Resolve-RavenfieldDir -GameDir $GameDir
$Managed = Join-Path $GameDir 'ravenfield_Data/Managed'
Write-Step "Ravenfield: $GameDir"

$Tools = Join-Path $Root 'tools'
$Build = Join-Path $Root 'build'
$Refs = Join-Path $Build 'refs'
$BepCore = Join-Path $Tools 'bepinex/pkg/BepInEx/core'
$XrRuntime = Join-Path $Tools 'openvr-xr/package/Runtime'
New-Item -ItemType Directory -Force $Build, $Refs | Out-Null

foreach ($required in @($BepCore, $XrRuntime, (Join-Path $Tools 'roslyn/pkg/tasks/netcore/bincore/csc.dll'))) {
    if (-not (Test-Path $required)) {
        throw "Missing dependency '$required'. Run ./tools/fetch-deps.ps1 first."
    }
}

# --- 1. Publicizer ---------------------------------------------------------------------------
$PublicizerDir = Join-Path $Build 'publicizer'
$PublicizerExe = Join-Path $PublicizerDir 'Publicizer.exe'
$PublicizerSrc = Join-Path $Tools 'Publicizer/Publicizer.cs'
if (-not (Test-Path $PublicizerExe) -or (Get-Item $PublicizerSrc).LastWriteTime -gt (Get-Item $PublicizerExe).LastWriteTime) {
    Write-Step 'Building Publicizer'
    New-Item -ItemType Directory -Force $PublicizerDir | Out-Null
    Copy-Item (Join-Path $BepCore 'Mono.Cecil.dll') $PublicizerDir -Force
    $fx = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
    Invoke-Csc @(
        '-nologo', '-target:exe', '-optimize+', '-langversion:latest', '-nostdlib', '-noconfig',
        "-out:$PublicizerExe",
        "-r:$fx/mscorlib.dll", "-r:$fx/System.dll", "-r:$fx/System.Core.dll",
        "-r:$PublicizerDir/Mono.Cecil.dll",
        $PublicizerSrc
    )
}

foreach ($asm in 'Assembly-CSharp', 'Assembly-CSharp-firstpass') {
    $src = Join-Path $Managed "$asm.dll"
    $dst = Join-Path $Refs "$asm.dll"
    if (-not (Test-Path $dst) -or (Get-Item $src).LastWriteTime -gt (Get-Item $dst).LastWriteTime) {
        Write-Step "Publicizing $asm"
        & $PublicizerExe $src $dst $Managed
        if ($LASTEXITCODE -ne 0) { throw "Publicizer failed for $asm" }
    }
}

# --- 2. Compile the plugin -------------------------------------------------------------------
Write-Step "Compiling RavenfieldVR ($Configuration)"
$sources = @(Get-ChildItem (Join-Path $Root 'src') -Recurse -Filter *.cs | ForEach-Object FullName)
$sources += (Join-Path $XrRuntime 'openvr_api.cs')

$references = New-Object System.Collections.Generic.List[string]
Get-ChildItem $Managed -Filter *.dll | Where-Object {
    $_.Name -notin @('Assembly-CSharp.dll', 'Assembly-CSharp-firstpass.dll')
} | ForEach-Object { $references.Add($_.FullName) }
$references.Add((Join-Path $Refs 'Assembly-CSharp.dll'))
$references.Add((Join-Path $Refs 'Assembly-CSharp-firstpass.dll'))
$references.Add((Join-Path $BepCore 'BepInEx.dll'))
$references.Add((Join-Path $BepCore '0Harmony.dll'))

$defines = 'UNITY_5_3_OR_NEWER;UNITY_STANDALONE;UNITY_STANDALONE_WIN'
$cscArgs = @(
    '-nologo', '-target:library', '-langversion:latest', '-nostdlib', '-noconfig', '-unsafe+',
    '-deterministic', '-nowarn:CS0649,CS0169,CS0414,CS0618,CS1591,CS0436', '-warn:4',
    "-out:$(Join-Path $Build 'RavenfieldVR.dll')",
    "-doc:$(Join-Path $Build 'RavenfieldVR.xml')"
)
if ($Configuration -eq 'Debug') {
    $cscArgs += @('-debug:portable', '-optimize-', "-define:$defines;DEBUG;TRACE")
}
else {
    $cscArgs += @('-debug:portable', '-optimize+', "-define:$defines;TRACE")
}
$cscArgs += $references | ForEach-Object { "-r:$_" }
# SteamVR action manifest + default bindings ship inside the DLL and are extracted at runtime.
Get-ChildItem (Join-Path $Root 'src/Input/SteamVR') -Filter *.json | ForEach-Object {
    $cscArgs += "-resource:$($_.FullName),RavenfieldVR.Input.$($_.Name)"
}
# The glove model and its textures (tools/hands/convert_glove.py makes them from assets/hands/steamvr-glove).
Get-ChildItem (Join-Path $Root 'src/Rig/Glove') -File | Where-Object { $_.Extension -in '.bin', '.jpg', '.png' } | ForEach-Object {
    $cscArgs += "-resource:$($_.FullName),RavenfieldVR.Rig.Glove.$($_.Name)"
}
$cscArgs += $sources
Invoke-Csc $cscArgs
Write-Step "Built $(Join-Path $Build 'RavenfieldVR.dll')"

# install.ps1 (dev layout) installs from dist/RavenfieldVR: keep its plugin current on plain builds too,
# otherwise an install after a non-package build silently ships the previous DLL.
$distPlugin = Join-Path $Root 'dist/RavenfieldVR/BepInEx/plugins/RavenfieldVR'
if (-not $Package -and (Test-Path $distPlugin)) {
    Copy-Item (Join-Path $Build 'RavenfieldVR.dll'), (Join-Path $Build 'RavenfieldVR.pdb') $distPlugin -Force
    Write-Step "Updated $distPlugin"
}

# --- 3. Package ------------------------------------------------------------------------------
if ($Package) {
    $version = (Select-String -Path (Join-Path $Root 'src/Core/PluginInfo.cs') -Pattern 'Version\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
    $dist = Join-Path $Root 'dist/RavenfieldVR'
    Write-Step "Packaging v$version -> $dist"
    if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
    $pluginOut = Join-Path $dist 'BepInEx/plugins/RavenfieldVR'
    $x64Out = Join-Path $dist 'ravenfield_Data/Plugins/x86_64'
    $subsysOut = Join-Path $dist 'ravenfield_Data/UnitySubsystems/XRSDKOpenVR'
    New-Item -ItemType Directory -Force $pluginOut, $x64Out, $subsysOut | Out-Null

    Copy-Item (Join-Path $Tools 'bepinex/pkg/*') $dist -Recurse -Force
    Copy-Item (Join-Path $Build 'RavenfieldVR.dll'), (Join-Path $Build 'RavenfieldVR.pdb') $pluginOut -Force

    # Native OpenVR XR provider + its manifest, where Unity discovers them at start-up...
    foreach ($native in 'XRSDKOpenVR.dll', 'openvr_api.dll', 'ucrtbased.dll') {
        Copy-Item (Join-Path $XrRuntime "x64/$native") $x64Out -Force
    }
    Copy-Item (Join-Path $XrRuntime 'UnitySubsystemsManifest.json') $subsysOut -Force
    # ...and a copy inside the plugin folder so a plugins-only install (mod managers) can self-repair.
    $pluginRuntime = Join-Path $pluginOut 'runtime'
    New-Item -ItemType Directory -Force $pluginRuntime | Out-Null
    Get-ChildItem $x64Out -Filter *.dll | ForEach-Object { Copy-Item $_.FullName (Join-Path $pluginRuntime ($_.Name + '.bin')) -Force }
    Copy-Item (Join-Path $subsysOut 'UnitySubsystemsManifest.json') $pluginRuntime -Force

    # Launcher (VR / flat + graphics preset), installed next to ravenfield.exe and run from Steam's Play button.
    $fx = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
    Invoke-Csc @(
        '-nologo', '-target:winexe', '-optimize+', '-langversion:latest', '-nostdlib', '-noconfig',
        "-out:$(Join-Path $dist 'RavenfieldVR-Launcher.exe')", "-win32manifest:$(Join-Path $Root 'installer/app.manifest')",
        "-r:$fx/mscorlib.dll", "-r:$fx/System.dll", "-r:$fx/System.Core.dll", "-r:$fx/System.Drawing.dll", "-r:$fx/System.Windows.Forms.dll",
        (Join-Path $Root 'installer/Launcher.cs')
    )
    Write-Step 'Built RavenfieldVR-Launcher.exe'

    New-Item -ItemType Directory -Force (Join-Path $dist 'tools') | Out-Null
    Copy-Item (Join-Path $Root 'tools/common.ps1') (Join-Path $dist 'tools') -Force
    foreach ($f in 'install.ps1', 'uninstall.ps1', 'install.cmd', 'uninstall.cmd', 'README.md', 'THIRD-PARTY-NOTICES.md') {
        Copy-Item (Join-Path $Root $f) $dist -Force
    }

    $zip = Join-Path $Root "dist/RavenfieldVR-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zip
    Write-Step "Release zip: $zip"

    # One-file setup program carrying the release.
    $setupObj = Join-Path $Build 'setup'
    New-Item -ItemType Directory -Force $setupObj | Out-Null
    Set-Content -Path (Join-Path $setupObj 'version.txt') -Value $version -NoNewline -Encoding ASCII
    $setupExe = Join-Path $Root "dist/RavenfieldVR-Setup-$version.exe"
    Invoke-Csc @(
        '-nologo', '-target:winexe', '-optimize+', '-langversion:latest', '-nostdlib', '-noconfig',
        "-out:$setupExe", "-win32manifest:$(Join-Path $Root 'installer/app.manifest')",
        "-r:$fx/mscorlib.dll", "-r:$fx/System.dll", "-r:$fx/System.Core.dll", "-r:$fx/System.Drawing.dll",
        "-r:$fx/System.Windows.Forms.dll", "-r:$fx/System.IO.Compression.dll", "-r:$fx/System.IO.Compression.FileSystem.dll",
        "-resource:$zip,RavenfieldVR.Setup.payload.zip",
        "-resource:$(Join-Path $setupObj 'version.txt'),RavenfieldVR.Setup.version.txt",
        (Join-Path $Root 'installer/Setup.cs'), (Join-Path $Root 'installer/SteamLaunchOption.cs')
    )
    Write-Step "Setup: $setupExe"
}

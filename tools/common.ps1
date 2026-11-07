# Shared helpers for build.ps1 / install.ps1 / uninstall.ps1 / fetch-deps.ps1.

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Get-SteamLibraries {
    $steam = $null
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        try {
            $item = Get-ItemProperty -Path $key -ErrorAction Stop
            if ($item.SteamPath) { $steam = $item.SteamPath; break }
            if ($item.InstallPath) { $steam = $item.InstallPath; break }
        }
        catch { }
    }
    if (-not $steam) { $steam = 'C:\Program Files (x86)\Steam' }
    $libs = New-Object System.Collections.Generic.List[string]
    $libs.Add(($steam -replace '/', '\'))
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($m in (Select-String -Path $vdf -Encoding UTF8 -Pattern '"path"\s+"([^"]+)"' -AllMatches).Matches) {
            $p = $m.Groups[1].Value -replace '\\\\', '\'
            if (-not $libs.Contains($p)) { $libs.Add($p) }
        }
    }
    return $libs
}

function Resolve-RavenfieldDir([string]$GameDir) {
    if ($GameDir) {
        if (-not (Test-Path (Join-Path $GameDir 'ravenfield.exe'))) { throw "ravenfield.exe not found in '$GameDir'" }
        return (Resolve-Path $GameDir).Path
    }
    foreach ($lib in Get-SteamLibraries) {
        $candidate = Join-Path $lib 'steamapps\common\Ravenfield'
        if (Test-Path (Join-Path $candidate 'ravenfield.exe')) { return $candidate }
    }
    throw 'Ravenfield not found in any Steam library. Pass -GameDir "<path to Ravenfield>".'
}

function Get-DotnetExe {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $default = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path $default) { return $default }
    throw 'No .NET runtime found (dotnet.exe). Install the .NET 8+ runtime.'
}

function Invoke-Csc([string[]]$Arguments) {
    $csc = Join-Path $PSScriptRoot 'roslyn\pkg\tasks\netcore\bincore\csc.dll'
    $rsp = [System.IO.Path]::GetTempFileName()
    try {
        # A response file keeps the command line short and handles paths with spaces.
        # -noconfig is only honoured on the real command line, never inside a response file.
        $lines = $Arguments | Where-Object { $_ -ne '-noconfig' } | ForEach-Object {
            if ($_ -match '^(-[a-zA-Z]+:)(.*\s.*)$') { "$($Matches[1])`"$($Matches[2])`"" }
            elseif ($_ -match '\s') { "`"$_`"" }
            else { $_ }
        }
        [System.IO.File]::WriteAllLines($rsp, $lines)
        $output = & (Get-DotnetExe) $csc -noconfig "@$rsp" 2>&1
        $code = $LASTEXITCODE
        foreach ($line in $output) {
            $text = "$line"
            if ($text -match ': error ') { Write-Host $text -ForegroundColor Red }
            elseif ($text -match ': warning ') { Write-Host $text -ForegroundColor Yellow }
            elseif ($text.Trim().Length -gt 0) { Write-Host $text }
        }
        if ($code -ne 0) { throw "C# compilation failed (exit $code)" }
    }
    finally {
        Remove-Item $rsp -ErrorAction SilentlyContinue
    }
}

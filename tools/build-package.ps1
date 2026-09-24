<#
.SYNOPSIS
    Builds the SeedLab machine-report package from this repository:
    dist\SeedLab-MachineReport-<version>\, its zip and dist\SHA256SUMS.txt.

.DESCRIPTION
    Standalone: it uses only this repository and an installed .NET 10 SDK (no NuGet download, no
    other download of any kind). The steps:

      1. delete every bin\ and obj\ in the repository (an incremental build can keep a stale apphost);
      2. publish tools\SeedLab.MachineReport framework-dependent for win-x64 into app\ - a
         deterministic build with every source path mapped to /_/ and no debug symbols;
      3. copy the installed .NET runtime into dotnet\ (dotnet.exe, host\fxr\<v>,
         shared\Microsoft.NETCore.App\<v>, LICENSE.txt, ThirdPartyNotices.txt), unmodified;
      4. prove the program starts on that copy and on nothing else: it is run with DOTNET_ROOT
         pointing at a folder that does not exist and must still report the bundled runtime;
      5. add natives\, reference\, the launcher, README-FIRST.txt, LICENSE.txt and
         THIRD-PARTY-NOTICES.txt, then package-files.sha256 listing every file's SHA-256;
      6. zip it with sorted entries and fixed timestamps, and write SHA256SUMS.txt.

    -MakeReference first rebuilds reference\fingerprints.json on this machine (the reference
    machine) by running the program's reference mode from the assembled package.

.PARAMETER DotnetRoot
    The .NET installation to take the runtime from. Default: C:\Program Files\dotnet.

.PARAMETER RuntimeVersion
    The Microsoft.NETCore.App version to bundle. Default: 10.0.12.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\build-package.ps1
#>
[CmdletBinding()]
param(
    [switch]$MakeReference,
    [string]$DotnetRoot = (Join-Path $env:ProgramFiles 'dotnet'),
    [string]$RuntimeVersion = '10.0.12'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$Version  = '1.0.0'
$Repo     = Split-Path -Parent $PSScriptRoot
$Name     = "SeedLab-MachineReport-$Version"
$Dist     = Join-Path $Repo 'dist'
$Pkg      = Join-Path $Dist $Name
$ZipPath  = Join-Path $Dist "$Name-win-x64.zip"
$SumsPath = Join-Path $Dist 'SHA256SUMS.txt'
$Project  = Join-Path $Repo 'tools\SeedLab.MachineReport\SeedLab.MachineReport.csproj'
$Utf8     = New-Object System.Text.UTF8Encoding($false)
# The zip's entries all carry this time, so the same inputs give the same zip.
$ZipTime  = New-Object DateTimeOffset(2026, 9, 24, 0, 0, 0, [TimeSpan]::Zero)

function Say([string]$s) { Write-Host "== $s" }

function Sha256([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $fs = [System.IO.File]::OpenRead($path)
    try { return ([BitConverter]::ToString($sha.ComputeHash($fs)) -replace '-', '').ToLowerInvariant() }
    finally { $fs.Dispose(); $sha.Dispose() }
}

function RelPath([string]$root, [string]$full) {
    $r = $full.Substring($root.TrimEnd('\').Length + 1)
    return $r -replace '\\', '/'
}

function Invoke-Checked([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe exited with code $LASTEXITCODE" }
}

# ---- 0. inputs -----------------------------------------------------------------------------------
$fxr    = Join-Path $DotnetRoot "host\fxr\$RuntimeVersion"
$shared = Join-Path $DotnetRoot "shared\Microsoft.NETCore.App\$RuntimeVersion"
foreach ($p in @($fxr, $shared, (Join-Path $DotnetRoot 'dotnet.exe'), (Join-Path $DotnetRoot 'LICENSE.txt'),
                 (Join-Path $DotnetRoot 'ThirdPartyNotices.txt'))) {
    if (-not (Test-Path -LiteralPath $p)) { throw "Not found: $p - pass -DotnetRoot / -RuntimeVersion" }
}

# ---- 1. clean ------------------------------------------------------------------------------------
Say 'cleaning bin\ and obj\'
Get-ChildItem -LiteralPath (Join-Path $Repo 'tools'), (Join-Path $Repo 'vendor') -Recurse -Directory -Force |
    Where-Object { $_.Name -eq 'bin' -or $_.Name -eq 'obj' } |
    Sort-Object { $_.FullName.Length } -Descending |
    ForEach-Object { if (Test-Path -LiteralPath $_.FullName) { Remove-Item -LiteralPath $_.FullName -Recurse -Force } }
if (Test-Path -LiteralPath $Pkg) { Remove-Item -LiteralPath $Pkg -Recurse -Force }
foreach ($f in @($ZipPath, $SumsPath)) { if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force } }
New-Item -ItemType Directory -Force -Path $Pkg | Out-Null

# ---- 2. publish ----------------------------------------------------------------------------------
Say 'publishing the program (framework-dependent, win-x64, deterministic)'
Invoke-Checked 'dotnet' @('publish', $Project, '-c', 'Release', '-o', (Join-Path $Pkg 'app'), '--nologo')
$pdbs = @(Get-ChildItem -LiteralPath (Join-Path $Pkg 'app') -Filter '*.pdb' -Recurse)
if ($pdbs.Count -gt 0) { throw 'debug symbols were produced; they must not be shipped' }

# ---- 3. the app-local runtime --------------------------------------------------------------------
Say "copying the .NET $RuntimeVersion runtime into dotnet\"
$rt = Join-Path $Pkg 'dotnet'
New-Item -ItemType Directory -Force -Path (Join-Path $rt 'host\fxr'), (Join-Path $rt 'shared\Microsoft.NETCore.App') | Out-Null
Copy-Item -LiteralPath (Join-Path $DotnetRoot 'dotnet.exe'), (Join-Path $DotnetRoot 'LICENSE.txt'),
          (Join-Path $DotnetRoot 'ThirdPartyNotices.txt') -Destination $rt
Copy-Item -LiteralPath $fxr -Destination (Join-Path $rt 'host\fxr') -Recurse
Copy-Item -LiteralPath $shared -Destination (Join-Path $rt 'shared\Microsoft.NETCore.App') -Recurse

# ---- 4. prove the program runs on that copy, and only on it ---------------------------------------
Say 'proving the program starts on the bundled runtime only'
$exe = Join-Path $Pkg 'app\SeedLab.MachineReport.exe'
$saved = @{}
foreach ($v in @('DOTNET_ROOT', 'DOTNET_ROOT_X64')) {
    $saved[$v] = [Environment]::GetEnvironmentVariable($v, 'Process')
    [Environment]::SetEnvironmentVariable($v, (Join-Path $Pkg 'no-such-folder'), 'Process')
}
try {
    & $exe --which-runtime
    if ($LASTEXITCODE -ne 0) { throw 'the program did not start on the bundled runtime' }
}
finally {
    foreach ($v in $saved.Keys) { [Environment]::SetEnvironmentVariable($v, $saved[$v], 'Process') }
}

# ---- 5. the rest of the package ------------------------------------------------------------------
Say 'adding natives\, the launcher and the documents'
Copy-Item -LiteralPath (Join-Path $Repo 'natives') -Destination $Pkg -Recurse
$bat = [IO.File]::ReadAllText((Join-Path $Repo 'tools\package\Run SeedLab machine report.bat'))
$bat = ($bat -replace "`r`n", "`n") -replace "`n", "`r`n"
[IO.File]::WriteAllText((Join-Path $Pkg 'Run SeedLab machine report.bat'), $bat, [System.Text.Encoding]::ASCII)
Copy-Item -LiteralPath (Join-Path $Repo 'tools\package\README-FIRST.txt') -Destination $Pkg
Copy-Item -LiteralPath (Join-Path $Repo 'LICENSE') -Destination (Join-Path $Pkg 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $Repo 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $Pkg 'THIRD-PARTY-NOTICES.txt')

function Write-Manifest {
    $lines = New-Object System.Collections.Generic.List[string]
    $files = Get-ChildItem -LiteralPath $Pkg -Recurse -File -Force |
        Where-Object { $_.Name -ne 'package-files.sha256' } |
        ForEach-Object { RelPath $Pkg $_.FullName } |
        Sort-Object -CaseSensitive
    foreach ($rel in $files) {
        $lines.Add((Sha256 (Join-Path $Pkg ($rel -replace '/', '\'))) + ' *' + $rel)
    }
    [IO.File]::WriteAllText((Join-Path $Pkg 'package-files.sha256'), (($lines -join "`n") + "`n"), $Utf8)
}

if ($MakeReference) {
    Say 'making reference\fingerprints.json on this machine'
    Write-Manifest
    $refOut = Join-Path $Repo 'reference\fingerprints.json'
    $env:DOTNET_ROOT = Join-Path $Pkg 'dotnet'
    $env:DOTNET_ROOT_X64 = Join-Path $Pkg 'dotnet'
    try {
        & $exe --make-reference $refOut
        if ($LASTEXITCODE -ne 0) { throw 'the reference run did not pass; no reference was written' }
    }
    finally {
        Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue
        Remove-Item Env:DOTNET_ROOT_X64 -ErrorAction SilentlyContinue
    }
}

$refFile = Join-Path $Repo 'reference\fingerprints.json'
if (-not (Test-Path -LiteralPath $refFile)) { throw 'reference\fingerprints.json is missing - run with -MakeReference on the reference machine' }
New-Item -ItemType Directory -Force -Path (Join-Path $Pkg 'reference') | Out-Null
Copy-Item -LiteralPath $refFile -Destination (Join-Path $Pkg 'reference')
Write-Manifest

# ---- 6. zip --------------------------------------------------------------------------------------
Say "zipping $Name-win-x64.zip"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$entries = Get-ChildItem -LiteralPath $Pkg -Recurse -File -Force | ForEach-Object { RelPath $Pkg $_.FullName } | Sort-Object -CaseSensitive
$zs = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew)
$zip = New-Object System.IO.Compression.ZipArchive($zs, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($rel in $entries) {
        $e = $zip.CreateEntry("$Name/$rel", [System.IO.Compression.CompressionLevel]::Optimal)
        $e.LastWriteTime = $ZipTime
        $out = $e.Open()
        $in = [System.IO.File]::OpenRead((Join-Path $Pkg ($rel -replace '/', '\')))
        try { $in.CopyTo($out) } finally { $in.Dispose(); $out.Dispose() }
    }
}
finally {
    $zip.Dispose()
    $zs.Dispose()
}

$zipHash = Sha256 $ZipPath
[IO.File]::WriteAllText($SumsPath, "$zipHash  $Name-win-x64.zip`n", $Utf8)

$size = (Get-Item -LiteralPath $ZipPath).Length
$unpacked = (Get-ChildItem -LiteralPath $Pkg -Recurse -File -Force | Measure-Object -Property Length -Sum).Sum
Say "done"
Write-Host ("  package  dist\{0}\  ({1} files, {2:N1} MB)" -f $Name, $entries.Count, ($unpacked / 1MB))
Write-Host ("  zip      dist\{0}-win-x64.zip  ({1:N1} MB)" -f $Name, ($size / 1MB))
Write-Host ("  sha256   {0}" -f $zipHash)

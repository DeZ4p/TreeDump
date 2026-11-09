<#
.SYNOPSIS
  Builds TreeDump Pro release artifacts: portable zip, Inno Setup installer, SHA256SUMS.txt
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\Build-Release.ps1
#>
param(
    [string]$Root = "",
    [string]$IsccPath = ""
)

$ErrorActionPreference = "Stop"

function Stop-Build([string]$Message) {
    Write-Host "STOP: $Message" -ForegroundColor Red
    exit 1
}

# ---------- Root ----------
if ([string]::IsNullOrWhiteSpace($Root)) {
    $scriptPath = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        $p = $MyInvocation.MyCommand.Path
        if (-not [string]::IsNullOrWhiteSpace($p)) { $scriptPath = Split-Path -Parent $p }
    }
    if (-not [string]::IsNullOrWhiteSpace($scriptPath)) {
        $Root = Split-Path -Parent $scriptPath
    } else {
        $Root = "D:\Project\TreeDump"
    }
}
if (-not (Test-Path (Join-Path $Root "TreeDump.csproj"))) { Stop-Build "TreeDump.csproj not found in $Root" }
Write-Host "Root: $Root"

$csproj = Join-Path $Root "TreeDump.csproj"
$iss    = Join-Path $Root "installer\TreeDump.iss"
$dist   = Join-Path $Root "dist"
if (-not (Test-Path $iss)) { Stop-Build "installer\TreeDump.iss not found" }

# ---------- Version ----------
[xml]$proj = Get-Content -LiteralPath $csproj -Raw
$version = $null
foreach ($pg in $proj.Project.PropertyGroup) {
    if ($pg.Version) { $version = [string]$pg.Version; break }
}
if ([string]::IsNullOrWhiteSpace($version)) { Stop-Build "<Version> not found in TreeDump.csproj" }
$version = $version.Trim()
Write-Host "Version: $version"

# ---------- Inno Setup compiler ----------
function Find-Iscc([string]$Explicit) {
    if ($Explicit -and (Test-Path $Explicit)) { return (Resolve-Path $Explicit).Path }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $bases = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA "Programs")) |
        Where-Object { $_ -and (Test-Path $_) }
    foreach ($b in $bases) {
        $dirs = Get-ChildItem -LiteralPath $b -Directory -Filter "Inno Setup*" -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending
        foreach ($d in $dirs) {
            $c = Join-Path $d.FullName "ISCC.exe"
            if (Test-Path $c) { return $c }
        }
    }

    $regRoots = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
    )
    foreach ($r in $regRoots) {
        if (-not (Test-Path $r)) { continue }
        foreach ($k in Get-ChildItem $r -ErrorAction SilentlyContinue) {
            $props = Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue
            if ($props -and $props.DisplayName -like "Inno Setup*" -and $props.InstallLocation) {
                $c = Join-Path $props.InstallLocation "ISCC.exe"
                if (Test-Path $c) { return $c }
            }
        }
    }
    return $null
}

$iscc = Find-Iscc $IsccPath
if (-not $iscc) { Stop-Build "ISCC.exe not found. Install Inno Setup or pass -IsccPath." }
Write-Host "ISCC: $iscc"

# ---------- Publish ----------
$publish = Join-Path $env:TEMP "treedump-publish"
if (Test-Path $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }

Write-Host ""
Write-Host "Publishing..."
& dotnet publish $csproj -c Release -r win-x64 --self-contained true `
    -p:DebugType=None -p:DebugSymbols=false -p:SatelliteResourceLanguages=en `
    -o $publish
if ($LASTEXITCODE -ne 0) { Stop-Build "dotnet publish failed (exit $LASTEXITCODE)" }

$exePath = Join-Path $publish "TreeDump.exe"
if (-not (Test-Path $exePath)) {
    Write-Host "Publish folder content (top level):"
    Get-ChildItem -LiteralPath $publish -File | Select-Object -First 20 | ForEach-Object { Write-Host "  $($_.Name)" }
    Stop-Build "TreeDump.exe was not produced by publish."
}
$pubSize = (Get-ChildItem -LiteralPath $publish -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("[ OK ] Published: {0:N1} MB" -f ($pubSize / 1MB)) -ForegroundColor Green

# ---------- Clean old artifacts ----------
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }
Get-ChildItem -LiteralPath $dist -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like "*$version*" -or $_.Name -eq "SHA256SUMS.txt" } |
    Remove-Item -Force

# ---------- Portable zip ----------
Write-Host ""
Write-Host "Packaging portable build..."

$folderName = "TreeDump-Portable-$version"
$stageBase  = Join-Path $env:TEMP "treedump-portable"
$stage      = Join-Path $stageBase $folderName
if (Test-Path $stageBase) { Remove-Item -LiteralPath $stageBase -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

Copy-Item -Path (Join-Path $publish "*") -Destination $stage -Recurse -Force

$readme = @"
TreeDump Pro $version - Portable
================================

Run TreeDump.exe. No installation and no .NET runtime are required.

Where your data lives
---------------------
  treedump.db   next to TreeDump.exe  (settings and project history)
  export\       next to TreeDump.exe  (Export and Handoff output, one folder per project)
  logs          %LocalAppData%\TreeDump\logs

Keep the whole folder together. Move it anywhere (USB drive, another PC)
and your settings and exports move with it. Use a folder you can write to.

Updating
--------
Extract the new version into a new folder, then copy treedump.db
(and export\ if you want to keep it) from the old folder.

Windows SmartScreen
-------------------
The files are not code-signed. If SmartScreen appears, click
"More info" and then "Run anyway".

Author
------
DeZ4p
GitHub:   https://github.com/DeZ4p
Telegram: https://t.me/DeZ4p
Project:  https://github.com/DeZ4p/TreeDump
License:  MIT
"@
Set-Content -LiteralPath (Join-Path $stage "README-Portable.txt") -Value $readme -Encoding UTF8

if (-not (Test-Path (Join-Path $stage "TreeDump.exe"))) { Stop-Build "TreeDump.exe missing in staging folder." }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zipPath = Join-Path $dist "$folderName-win-x64.zip"
$fs  = $null
$zip = $null
try {
    $fs  = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::Create)
    $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
    $baseLen = $stageBase.Length
    foreach ($f in Get-ChildItem -LiteralPath $stage -Recurse -File) {
        $rel = $f.FullName.Substring($baseLen).TrimStart('\').Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $f.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    if ($zip) { $zip.Dispose() }
    if ($fs)  { $fs.Dispose() }
}

# Verify zip (names normalized, so both separators are accepted)
$expected = "$folderName/TreeDump.exe"
$found = $false
$entryCount = 0
$rz = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    foreach ($e in $rz.Entries) {
        $entryCount++
        if ($e.FullName.Replace('\', '/') -eq $expected) { $found = $true }
    }
}
finally { $rz.Dispose() }
if (-not $found) { Stop-Build "$expected was not found inside the portable zip ($entryCount entries)." }
Write-Host ("[ OK ] Portable zip: {0:N1} MB, {1} entries" -f ((Get-Item $zipPath).Length / 1MB), $entryCount) -ForegroundColor Green

# ---------- Installer ----------
Write-Host ""
Write-Host "Building installer..."
& $iscc "/Q" "/DAppVersion=$version" "/DSourceDir=$publish" "/DOutputDir=$dist" $iss
if ($LASTEXITCODE -ne 0) { Stop-Build "ISCC failed (exit $LASTEXITCODE)" }

$setupPath = Join-Path $dist "TreeDump-Setup-$version.exe"
if (-not (Test-Path $setupPath)) { Stop-Build "Setup exe was not produced: $setupPath" }
Write-Host ("[ OK ] Setup: {0:N1} MB" -f ((Get-Item $setupPath).Length / 1MB)) -ForegroundColor Green

# ---------- Checksums ----------
$lines = New-Object System.Collections.Generic.List[string]
foreach ($p in @($zipPath, $setupPath)) {
    $h = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines.Add("$h *$([System.IO.Path]::GetFileName($p))")
}
$sumsPath = Join-Path $dist "SHA256SUMS.txt"
[System.IO.File]::WriteAllText($sumsPath, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))

# ---------- Cleanup temp ----------
Remove-Item -LiteralPath $stageBase -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "DONE" -ForegroundColor Green
Get-ChildItem -LiteralPath $dist -File | ForEach-Object {
    Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
}
Write-Host ""
Get-Content -LiteralPath $sumsPath | ForEach-Object { Write-Host "  $_" }
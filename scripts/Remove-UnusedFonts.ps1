<#
  Removes the unused Vazirmatn fonts and fixes the stale comment in Foundation.xaml.
  Verifies nothing references the fonts first, then rebuilds.

  Run:
    powershell -ExecutionPolicy Bypass -File D:\Project\TreeDump\scripts\Remove-UnusedFonts.ps1
#>
param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = "Stop"

$fonts = Join-Path $Root "Fonts"
if (-not (Test-Path -LiteralPath $fonts)) {
    Write-Host "No Fonts folder. Nothing to do."
    exit 0
}

# 1. Nothing may reference the fonts (the stale comment in Foundation.xaml is the only allowed hit).
$hits = Get-ChildItem -LiteralPath $Root -Recurse -File -Include *.cs, *.xaml, *.csproj |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|export|backups|scripts)\\' } |
    Select-String -Pattern 'Vazirmatn|Fonts[\\/]' |
    Where-Object { -not ($_.Filename -eq "Foundation.xaml" -and $_.Line -match 'is embedded as a Resource') }

if ($hits) {
    Write-Host "STOP: the fonts are still referenced:" -ForegroundColor Red
    foreach ($h in $hits) { Write-Host ("  {0}:{1}: {2}" -f $h.Filename, $h.LineNumber, $h.Line.Trim()) }
    exit 1
}
Write-Host "[ OK ] No real references to the fonts."

# 2. Fix the stale comment (exactly one match required).
$foundation = Join-Path $Root "Themes\Foundation.xaml"
$text = [System.IO.File]::ReadAllText($foundation)
$rx = [regex]'Vazirmatn is embedded as a Resource \(see TreeDump\.csproj\)\.'
$count = $rx.Matches($text).Count

if ($count -gt 1) {
    Write-Host "STOP: comment pattern matched $count times. Edit Foundation.xaml line 5 by hand." -ForegroundColor Red
    exit 1
}
if ($count -eq 1) {
    Copy-Item -LiteralPath $foundation -Destination "$foundation.pre-fonts.bak" -Force
    $newText = $rx.Replace($text, 'Fonts are system fonts (Segoe UI, Cascadia Code, Consolas).')
    [System.IO.File]::WriteAllText($foundation, $newText, (New-Object System.Text.UTF8Encoding($true)))
    Write-Host "[ OK ] Foundation.xaml comment updated."
}
else {
    Write-Host "[ -- ] Foundation.xaml comment already clean."
}

# 3. Remove the fonts and rebuild.
Remove-Item -LiteralPath $fonts -Recurse -Force
Write-Host "[ OK ] Fonts folder removed."

Get-Process TreeDump -ErrorAction SilentlyContinue | Stop-Process -Force
$out = (& dotnet build (Join-Path $Root "TreeDump.csproj") -c Debug --no-incremental 2>&1 | Out-String -Width 4096)
$w = [regex]::Match($out, '(\d+)\s+Warning\(s\)').Groups[1].Value
$e = [regex]::Match($out, '(\d+)\s+Error\(s\)').Groups[1].Value
Write-Host ""
Write-Host "Build result: $w Warning(s), $e Error(s)"

if ($w -eq "0" -and $e -eq "0") {
    Write-Host "RESULT: OK" -ForegroundColor Green
}
else {
    Write-Host "RESULT: BUILD PROBLEM. Restore Foundation.xaml.pre-fonts.bak and send me the output." -ForegroundColor Red
    $out -split "`r?`n" | Where-Object { $_ -match '\s(error|warning)\s' } | Sort-Object -Unique | Select-Object -First 30
    exit 1
}
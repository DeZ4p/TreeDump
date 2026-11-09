<#
  TreeDump Pro - pre-release check.
  Builds the project, scans for mojibake and possible secrets, and lists files
  that must not be committed. Writes a report to the Desktop.

  Run:
    powershell -ExecutionPolicy Bypass -File D:\Project\TreeDump\scripts\Check-Release.ps1
#>
param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = "Continue"
$report = New-Object System.Collections.Generic.List[string]
$script:failed = $false

function Say([string]$text) {
    $report.Add($text)
    Write-Host $text
}
function Fail([string]$text) {
    $script:failed = $true
    Say "[FAIL] $text"
}
function Pass([string]$text) { Say "[ OK ] $text" }
function Warn([string]$text) { Say "[WARN] $text" }

function Get-RepoFiles {
    Get-ChildItem -LiteralPath $Root -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object {
            $rel = $_.FullName.Substring($Root.Length)
            $rel -notmatch '\\(bin|obj|export|backups|\.git|\.vs)\\'
        }
}

Say "TreeDump Pro - pre-release check"
Say "Root: $Root"
Say ("Time: " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss"))
Say ""

# ---------------------------------------------------------------- 1. Tools
Say "== 1. Tools =="
$dotnetVer = (& dotnet --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -eq 0) { Pass "dotnet SDK $dotnetVer" } else { Fail "dotnet SDK not found" }

$gitCmd = Get-Command git -ErrorAction SilentlyContinue
if ($gitCmd) { Pass ((& git --version 2>&1 | Out-String).Trim()) } else { Fail "git not found on PATH" }
Say ""

# ---------------------------------------------------------------- 2. Build
Say "== 2. Build (Debug, --no-incremental) =="
Get-Process TreeDump -ErrorAction SilentlyContinue | Stop-Process -Force
$csproj = Join-Path $Root "TreeDump.csproj"
if (-not (Test-Path -LiteralPath $csproj)) {
    Fail "TreeDump.csproj not found in $Root"
}
else {
    $buildOut = (& dotnet build $csproj -c Debug --no-incremental 2>&1 | Out-String -Width 4096)
    $w = [regex]::Match($buildOut, '(\d+)\s+Warning\(s\)')
    $e = [regex]::Match($buildOut, '(\d+)\s+Error\(s\)')
    if (-not $w.Success -or -not $e.Success) {
        Fail "Could not read build summary"
    }
    else {
        $warnCount = [int]$w.Groups[1].Value
        $errCount = [int]$e.Groups[1].Value
        if ($warnCount -eq 0 -and $errCount -eq 0) {
            Pass "Build: 0 Warning(s), 0 Error(s)"
        }
        else {
            Fail "Build: $warnCount Warning(s), $errCount Error(s)"
            $lines = $buildOut -split "`r?`n" |
                Where-Object { $_ -match '\s(error|warning)\s' } |
                Sort-Object -Unique |
                Select-Object -First 40
            foreach ($l in $lines) { Say ("   " + $l.Trim()) }
        }
    }
}
Say ""

# ---------------------------------------------------------------- 3. Mojibake
Say "== 3. Mojibake scan (broken em dash) =="
$moji = ([string][char]0x00E2) + ([string][char]0x20AC)
$textExt = @(".cs", ".xaml", ".md", ".csproj", ".json", ".yml", ".yaml", ".txt")
$mojiHits = 0
foreach ($f in (Get-RepoFiles | Where-Object { $textExt -contains $_.Extension.ToLowerInvariant() })) {
    $content = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
    if ($content.Contains($moji)) {
        $mojiHits++
        $lineNo = 0
        foreach ($line in ($content -split "`n")) {
            $lineNo++
            if ($line.Contains($moji)) {
                Warn ("{0}:{1}" -f $f.FullName.Substring($Root.Length + 1), $lineNo)
            }
        }
    }
}
if ($mojiHits -eq 0) { Pass "No broken characters found" } else { Fail "$mojiHits file(s) contain broken characters" }
Say ""

# ---------------------------------------------------------------- 4. Secrets
Say "== 4. Secret scan (locations only, values are never printed) =="
$secretRules = @(
    @{ Name = "Private key block";   Rx = '-----BEGIN [A-Z ]*PRIVATE KEY-----' },
    @{ Name = "GitHub token";        Rx = 'gh[pousr]_[A-Za-z0-9]{30,}' },
    @{ Name = "AWS access key";      Rx = 'AKIA[0-9A-Z]{16}' },
    @{ Name = "Hardcoded credential"; Rx = '(?i)(password|passwd|secret|api[_-]?key)\s*=\s*"[^"\s]{8,}"' }
)
$scanExt = @(".cs", ".xaml", ".csproj", ".json", ".xml", ".md", ".ps1", ".config", ".txt", ".yml", ".yaml")
$secretHits = 0
$scanFiles = Get-RepoFiles | Where-Object {
    ($scanExt -contains $_.Extension.ToLowerInvariant()) -and ($_.Name -ne "Check-Release.ps1")
}
foreach ($rule in $secretRules) {
    foreach ($f in $scanFiles) {
        $matches2 = Select-String -LiteralPath $f.FullName -Pattern $rule.Rx -ErrorAction SilentlyContinue
        foreach ($m in $matches2) {
            $secretHits++
            Warn ("{0}:{1} [{2}]" -f $f.FullName.Substring($Root.Length + 1), $m.LineNumber, $rule.Name)
        }
    }
}
if ($secretHits -eq 0) {
    Pass "No suspicious patterns found"
}
else {
    Warn "$secretHits possible hit(s). Review each one. Redaction regexes inside ExportService.cs or FileHelper.cs may be false positives."
}
Say ""

# ---------------------------------------------------------------- 5. Files that must not be committed
Say "== 5. Local files (must be covered by .gitignore) =="
$localPatterns = @("*.db", "*.db-shm", "*.db-wal", "*.bak", "crash.log", "*.user", "*.log")
$localHits = Get-RepoFiles | Where-Object {
    $n = $_.Name
    ($localPatterns | Where-Object { $n -like $_ }).Count -gt 0
}
if ($localHits) {
    foreach ($h in $localHits) { Say ("   " + $h.FullName.Substring($Root.Length + 1)) }
    Say "   (these are ignored by .gitignore; just confirm none should be kept)"
}
else {
    Pass "None found"
}

$big = Get-RepoFiles | Where-Object { $_.Length -gt 5MB }
if ($big) {
    foreach ($b in $big) { Warn ("Large file ({0:N1} MB): {1}" -f ($b.Length / 1MB), $b.FullName.Substring($Root.Length + 1)) }
}
else {
    Pass "No file above 5 MB"
}
Say ""

# ---------------------------------------------------------------- 6. Fonts / third-party assets
Say "== 6. Bundled fonts (check license before publishing) =="
$fontsDir = Join-Path $Root "Fonts"
if (Test-Path -LiteralPath $fontsDir) {
    $fontFiles = Get-ChildItem -LiteralPath $fontsDir -Recurse -File -ErrorAction SilentlyContinue
    if ($fontFiles) {
        foreach ($ff in $fontFiles) { Say ("   {0} ({1:N0} KB)" -f $ff.Name, ($ff.Length / 1KB)) }
        Warn "Confirm each font allows redistribution, or remove it from the repo."
    }
    else {
        Pass "Fonts folder is empty"
    }
}
else {
    Pass "No Fonts folder"
}
Say ""

# ---------------------------------------------------------------- 7. Repo files
Say "== 7. Repository files =="
$wanted = @(".gitignore", ".gitattributes", "README.md", "LICENSE", "CHANGELOG.md", "SECURITY.md", "CONTRIBUTING.md", ".github\workflows\ci.yml", ".github\workflows\release.yml")
foreach ($w2 in $wanted) {
    if (Test-Path -LiteralPath (Join-Path $Root $w2)) { Pass "$w2" } else { Say "[ -- ] $w2 (missing)" }
}
Say ""

# ---------------------------------------------------------------- Summary
if ($script:failed) { Say "RESULT: NOT READY (see [FAIL] lines)" } else { Say "RESULT: READY (review any [WARN] lines)" }

$desktop = [Environment]::GetFolderPath("Desktop")
$outFile = Join-Path $desktop "treedump_release_check.txt"
Set-Content -LiteralPath $outFile -Value $report -Encoding UTF8
Say ""
Say "Report saved: $outFile"

if ($script:failed) { exit 1 } else { exit 0 }
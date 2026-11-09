<#
  Initializes the Git repository, stages everything, audits the staged files,
  and creates the first commit only if the audit is clean. Never pushes.

  Run:
    powershell -ExecutionPolicy Bypass -File D:\Project\TreeDump\scripts\Init-Repo.ps1 -Email "ID+USERNAME@users.noreply.github.com"
#>
param(
    [Parameter(Mandatory = $true)][string]$Email,
    [string]$UserName = "DeZ4p",
    [string]$Root = ""
)

$ErrorActionPreference = "Continue"

function Stop-Now([string]$msg) {
    Write-Host "STOP: $msg" -ForegroundColor Red
    exit 1
}

# 0. Resolve the project root without relying on a param default.
if ([string]::IsNullOrWhiteSpace($Root)) {
    $scriptDir = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptDir) -and $MyInvocation.MyCommand.Path) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    if (-not [string]::IsNullOrWhiteSpace($scriptDir)) {
        $Root = Split-Path -Parent $scriptDir
    }
    else {
        $Root = "D:\Project\TreeDump"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $Root "TreeDump.csproj"))) {
    Stop-Now "TreeDump.csproj not found in '$Root'. Pass the project folder with -Root."
}
Write-Host "Root: $Root"

# 1. Only a GitHub noreply address is accepted, so a personal email can never leak into history.
if ($Email -notmatch '^(\d+\+)?[A-Za-z0-9-]+@users\.noreply\.github\.com$') {
    Stop-Now "Email must be your GitHub noreply address (see your GitHub email settings page)."
}
if (Test-Path -LiteralPath (Join-Path $Root ".git")) {
    Stop-Now "A .git folder already exists in $Root. Nothing was changed."
}
foreach ($need in ".gitignore", ".gitattributes", "README.md", "LICENSE", "TreeDump.csproj") {
    if (-not (Test-Path -LiteralPath (Join-Path $Root $need))) { Stop-Now "Missing required file: $need" }
}

# 2. Init and local identity (global config is not touched).
& git -C $Root init -b main
if ($LASTEXITCODE -ne 0) { Stop-Now "git init failed." }
& git -C $Root config user.name $UserName
& git -C $Root config user.email $Email

# 3. Stage everything and audit.
& git -C $Root add -A
if ($LASTEXITCODE -ne 0) { Stop-Now "git add failed." }

$staged = @(& git -C $Root diff --cached --name-only)
Write-Host ""
Write-Host ("Staged files: {0}" -f $staged.Count)

$forbidden = @(
    '(^|/)(bin|obj|export|backups|\.vs|Fonts)/',
    '\.(db|db-shm|db-wal|bak|log|user|pfx|pem|key|snk)$',
    '(^|/)\.env',
    '(^|/)crash\.log$',
    '(^|/)Prep-GitHub\.ps1$'
)
$violations = @()
foreach ($f in $staged) {
    foreach ($rx in $forbidden) {
        if ($f -match $rx) { $violations += $f; break }
    }
}

# Personal traces inside staged content. Needles are built from pieces so this
# file does not match itself. Only locations are reported, never the content.
$needles = @(
    ("ba" + "bak"),
    ("gm" + "ail.com"),
    ("C:" + "\Us" + "ers\")
)
$traceHits = @()
foreach ($needle in $needles) {
    $found = @(& git -C $Root grep --cached -n -I -F -e $needle)
    if ($LASTEXITCODE -eq 0 -and $found.Count -gt 0) {
        foreach ($line in $found) {
            $parts = $line -split ':', 3
            $traceHits += ("{0}:{1}" -f $parts[0], $parts[1])
        }
    }
}
$traceHits = @($traceHits | Sort-Object -Unique)

# 4. Report for review.
$desktop = [Environment]::GetFolderPath("Desktop")
$reportPath = Join-Path $desktop "treedump_staged.txt"
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("Staged files ($($staged.Count)):")
foreach ($f in $staged) { $lines.Add("  $f") }
$lines.Add("")
$lines.Add("Forbidden files: $($violations.Count)")
foreach ($v in $violations) { $lines.Add("  $v") }
$lines.Add("Personal traces (file:line): $($traceHits.Count)")
foreach ($t in $traceHits) { $lines.Add("  $t") }
Set-Content -LiteralPath $reportPath -Value $lines -Encoding UTF8
Write-Host "Report saved: $reportPath"

if ($violations.Count -gt 0 -or $traceHits.Count -gt 0) {
    Write-Host ""
    Write-Host "AUDIT FAILED. Nothing was committed." -ForegroundColor Red
    foreach ($v in $violations) { Write-Host "  forbidden: $v" }
    foreach ($t in $traceHits) { Write-Host "  trace    : $t" }
    Write-Host "Fix the cause, then delete the .git folder and run this script again."
    exit 1
}

# 5. Commit.
& git -C $Root commit -m "feat: TreeDump Pro 1.0.0" | Out-Null
if ($LASTEXITCODE -ne 0) { Stop-Now "git commit failed." }

Write-Host ""
Write-Host "AUDIT CLEAN. First commit created:" -ForegroundColor Green
& git -C $Root log -1 --format="  %h  %an <%ae>  %s"

Write-Host ""
Write-Host "Ignore rules working on real paths:"
foreach ($p in "treedump.db", "crash.log", "export/x.md", "ExportService.cs.bak", "bin/x.dll") {
    $res = (& git -C $Root check-ignore $p | Out-String).Trim()
    if ($res) { Write-Host ("  ignored      {0}" -f $p) } else { Write-Host ("  NOT IGNORED  {0}" -f $p) -ForegroundColor Red }
}

Write-Host ""
Write-Host "Next: create an EMPTY repo named TreeDump on GitHub (no README, no license, no .gitignore), then:"
Write-Host "  git remote add origin https://github.com/DeZ4p/TreeDump.git"
Write-Host "  git push -u origin main"
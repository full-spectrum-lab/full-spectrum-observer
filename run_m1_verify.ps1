# =============================================================================
# run_m1_verify.ps1 - M1 (Observer v0.3.0-beta) verification runner
#
# NuGetAudit BYPASS (EXPLICIT OPT-IN, tracked, NOT a permanent exemption):
#   The HIGH advisory NU1903 on SQLitePCLRaw.lib.e_sqlite3 2.1.6
#   (GHSA-2m69-gcr7-jv3q) is a required transitive pin of Microsoft.Data.Sqlite
#   8.0.10. It is registered as a v0.3 Final-HBG + RC/Release BLOCKER and MUST
#   be resolved via the dependency upgrade (item 2).
#
#   This bypass is now EXPLICIT / OPT-IN: it only switches NuGetAudit OFF when
#   you pass the -AllowKnownNugetAuditBypass switch. By DEFAULT (no switch) the
#   audit STAYS ON and is no longer silently disabled. Pass the switch ONLY for
#   the known, already-registered NU1903 / GHSA-2m69-gcr7-jv3q bypass that is
#   still pending a real fix via dependency upgrade (MUST be resolved via
#   dependency upgrade). Do NOT leave this bypass on permanently - REMOVE the
#   switch usage once SQLitePCLRaw / Microsoft.Data.Sqlite is upgraded to a
#   non-advisory version.
# =============================================================================
[CmdletBinding()]
param(
    [switch] $AllowKnownNugetAuditBypass
)
$ErrorActionPreference = 'Continue'
$dotnet = "C:\Users\wangjian0926\.dotnet10\dotnet.exe"
$env:DOTNET_ROOT = "C:\Users\wangjian0926\.dotnet10"
$env:PATH = "C:\Users\wangjian0926\.dotnet10;$env:PATH"
$repo = "C:\Users\wangjian0926\WorkBuddy\2026-07-12-20-20-07\full-spectrum-observer"
$log = "C:\Users\wangjian0926\AppData\Local\Temp\m1_verify.log"
$precheck = "C:\Users\wangjian0926\AppData\Local\Temp\verify_repo_identity.ps1"

# Build the optional NuGetAudit bypass argument set. Empty unless the explicit
# opt-in switch is provided - this keeps the audit enabled by default.
$bypassArg = if ($AllowKnownNugetAuditBypass) { @('-p:NuGetAudit=false') } else { @() }

function Log($s){ Add-Content -Path $log -Value $s -Encoding UTF8; Write-Host $s }

Set-Content -Path $log -Value "" -Encoding UTF8
Log "===== STEP V: REAL M1 VERIFICATION ($(Get-Date -Format u)) ====="
Log "AllowKnownNugetAuditBypass=$AllowKnownNugetAuditBypass"
Log "NuGetAudit effective bypass args: $($bypassArg -join ' ')"

# --- identity precheck ---
Log "===== IDENTITY PRECHECK ====="
& "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $precheck
if ($LASTEXITCODE -ne 0) { Log "PRECHECK FAILED -> ABORT"; exit 1 }

cd $repo

# --- SDK / global.json ---
Log "===== SDK / global.json ====="
Log "dotnet --version => $(& $dotnet --version 2>&1)"
& $dotnet --info 2>&1 | ForEach-Object { Log $_ }

# --- restore (inject nuget.org since NuGet.Config has <clear/>; .packages used for SQLite) ---
Log "===== dotnet restore ====="
& $dotnet restore FullSpectrum.Observer.sln -s https://api.nuget.org/v3/index.json @bypassArg 2>&1 | Tee-Object -Append -FilePath $log
$rcrestore = $LASTEXITCODE
Log "RESTORE_RC=$rcrestore"
if ($rcrestore -ne 0) { Log "RESTORE FAILED"; exit 1 }

# --- build Release ---
Log "===== dotnet build -c Release ====="
& $dotnet build FullSpectrum.Observer.sln -c Release @bypassArg 2>&1 | Tee-Object -Append -FilePath $log
$rcbuild = $LASTEXITCODE
Log "BUILD_RC=$rcbuild"
if ($rcbuild -ne 0) { Log "BUILD FAILED"; exit 1 }

# --- provision native sqlite3.dll into every bin/Release/net10.0 output ---
Log "===== provision native sqlite3.dll into bin outputs ====="
$src = Join-Path $repo ".runtime\sqlite\sqlite3.dll"
$copied = 0
Get-ChildItem $repo -Recurse -Directory -Filter net10.0 | Where-Object { $_.FullName -match 'bin\\Release' } | ForEach-Object {
    Copy-Item $src (Join-Path $_.FullName "sqlite3.dll") -Force
    $copied++
}
Log "copied sqlite3.dll into $copied bin output dirs"

# --- Unit tests ---
Log "===== dotnet test Unit (expect 18) ====="
& $dotnet test tests/Observer.Tests.Unit -c Release @bypassArg --logger "trx;LogFileName=m1-unit-tests.trx" 2>&1 | Tee-Object -Append -FilePath $log
$rcunit = $LASTEXITCODE
Log "UNIT_RC=$rcunit"

# --- Integration tests ---
Log "===== dotnet test Integration (expect 4) ====="
& $dotnet test tests/Observer.Tests.Integration -c Release @bypassArg --logger "trx;LogFileName=m1-integration-tests.trx" 2>&1 | Tee-Object -Append -FilePath $log
$rcint = $LASTEXITCODE
Log "INTEGRATION_RC=$rcint"

# --- parse discovered counts ---
function ParseCount($projLog) {
    # look for the VSTest summary line "Passed!  - Failed: X, Passed: Y, Skipped: Z, Total: T"
    $m = Select-String -Path $log -Pattern "Passed!\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)" | Select-Object -Last 1
    if ($m) { return $m.Matches[0].Groups }
    return $null
}
Log "===== SUMMARY ====="
Log "RESTORE_RC=$rcrestore BUILD_RC=$rcbuild UNIT_RC=$rcunit INTEGRATION_RC=$rcint"
Log "Expect Unit=18 Integration=4"
Log "=== DONE ($(Get-Date -Format u)) ==="

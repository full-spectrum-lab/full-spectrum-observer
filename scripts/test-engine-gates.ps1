<#
.SYNOPSIS
    Positive + negative test for the M2-ENG-01 dual-digest release gates.

.DESCRIPTION
    -Default: runs Test-EngineReleaseGates against the real repo (expect PASS).
    -Negative: copies engine/ to a temp dir, mutates one vendored file, runs the gate
     (expect FAIL), then cleans up. Proves the gate is sensitive, not a no-op.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [switch]$Negative
)
$ErrorActionPreference = "Stop"
if (-not $RepoRoot) { $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path }
. (Join-Path $PSScriptRoot "engine-release-gates.ps1")
$BaselinePath = Join-Path $RepoRoot "engine/engine-baseline.json"

if ($Negative) {
    $tmp = Join-Path $env:TEMP ("rpm_neg_" + [System.Guid]::NewGuid().ToString("N"))
    Copy-Item -Path (Join-Path $RepoRoot "engine") -Destination (Join-Path $tmp "engine") -Recurse -Force
    $victim = Join-Path $tmp "engine/vendor/full-spectrum-engine/simulate.py"
    $c = Get-Content $victim -Raw
    Set-Content -Path $victim -Value ($c + "`n# mutated for negative test`n") -Encoding utf8
    $tmpBaseline = Join-Path $tmp "engine/engine-baseline.json"
    $passed = $false
    try {
        Test-EngineReleaseGates -RepoRoot $tmp -BaselinePath $tmpBaseline
        $passed = $true
    } catch {
        Write-Host ("NEGATIVE EXPECTED FAIL: " + $_.Exception.Message)
    }
    Remove-Item -Path $tmp -Recurse -Force
    if ($passed) { Write-Host "NEGATIVE TEST FAILED: gate passed on mutated tree (BAD)"; exit 1 }
    Write-Host "NEGATIVE TEST PASSED: gate caught the mutation (GOOD)"
} else {
    try {
        $r = Test-EngineReleaseGates -RepoRoot $RepoRoot -BaselinePath $BaselinePath
        Write-Host ("POSITIVE TEST PASSED: gates green (result=$r)")
    } catch {
        Write-Host ("POSITIVE TEST FAILED: " + $_.Exception.Message); exit 1
    }
}

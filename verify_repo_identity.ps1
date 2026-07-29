#!/usr/bin/env pwsh
# Read-only repository identity precheck for FSO M1/M3 verification.
#
# Prevents silently operating on the WRONG repo/branch and verifies the working
# tree descends from (or equals) the FROZEN CANDIDATE commit. The expected
# commit is NOT hardcoded: it is taken from
#   * -ExternalIdentityPath <json>  (preferred; reads observer_commit), or
#   * -ExpectedCommit <sha>         (explicit override), or
#   * -BaselineSha <sha>            (optional ancestor-only safety net).
# Forward fix commits on top of the frozen candidate are allowed (ancestor check).
#
# Usage: pwsh verify_repo_identity.ps1 [-ExpectedPath <abs>] [-ExternalIdentityPath <json>]
#                                      [-ExpectedCommit <sha>] [-BaselineSha <sha>]
# Exits 0 on match, 1 on mismatch. Strictly read-only: never modifies the tree.

param(
    [string]$ExpectedPath = (Resolve-Path '.').Path,
    [string]$ExternalIdentityPath = "",
    [string]$ExpectedCommit = "",
    [string]$BaselineSha = ""
)

$ErrorActionPreference = 'Stop'

$ExpectedRemote = 'gitee.com/full-spectrum/full-spectrum-observer'
$ExpectedBranch = 'feature/v0.3-observer-console'

function Fail($msg) {
    Write-Error "IDENTITY CHECK FAILED: $msg"
    exit 1
}

# 1) absolute path of the git toplevel must equal the expected working tree.
$toplevel = (git rev-parse --show-toplevel).Replace('/', '\').Trim().TrimEnd('\')
$expected = (Resolve-Path $ExpectedPath).Path.Replace('/', '\').Trim().TrimEnd('\')
if ($toplevel -ne $expected) {
    Fail "path mismatch: toplevel='$toplevel' expected='$expected'"
}

# 2) remote
$remote = (git remote get-url origin).Trim()
if (-not $remote.Contains($ExpectedRemote)) {
    Fail "remote mismatch: '$remote' (expected to contain '$ExpectedRemote')"
}

# 3) branch
$branch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne $ExpectedBranch) {
    Fail "branch mismatch: '$branch' (expected '$ExpectedBranch')"
}

# 4) resolve the frozen-candidate commit: external identity > explicit > none
$resolvedExpected = ""
if (-not [string]::IsNullOrWhiteSpace($ExternalIdentityPath) -and (Test-Path -LiteralPath $ExternalIdentityPath)) {
    try {
        $doc = Get-Content -LiteralPath $ExternalIdentityPath -Raw | ConvertFrom-Json
        if ($doc.PSObject.Properties.Name -contains 'observer_commit') {
            $resolvedExpected = ($doc.observer_commit -replace '[^0-9a-fA-F]', '').ToLowerInvariant()
        }
    } catch { /* parse errors -> treated as not provided */ }
}
if ([string]::IsNullOrWhiteSpace($resolvedExpected)) {
    $resolvedExpected = ($ExpectedCommit -replace '[^0-9a-fA-F]', '').ToLowerInvariant()
}

# 5) verify HEAD vs frozen candidate (ancestor tolerance: forward fixes allowed)
$head = (git rev-parse HEAD).Trim().ToLowerInvariant()
if (-not [string]::IsNullOrWhiteSpace($resolvedExpected)) {
    if ($head -ne $resolvedExpected) {
        git merge-base --is-ancestor $resolvedExpected $head
        if ($LASTEXITCODE -ne 0) {
            Fail "HEAD $head is neither the frozen candidate $resolvedExpected nor descended from it"
        }
    }
    Write-Host "IDENTITY OK: toplevel=$toplevel branch=$branch HEAD=$head frozen-candidate=$resolvedExpected"
} else {
    Write-Host "IDENTITY OK (no frozen-candidate constraint): toplevel=$toplevel branch=$branch HEAD=$head"
}

# 6) optional baseline ancestor safety net (never hardcoded as the sole gate)
if (-not [string]::IsNullOrWhiteSpace($BaselineSha)) {
    $bs = ($BaselineSha -replace '[^0-9a-fA-F]', '').ToLowerInvariant()
    git merge-base --is-ancestor $bs $head
    if ($LASTEXITCODE -ne 0) {
        Fail "baseline $bs is NOT an ancestor of HEAD $head"
    }
    Write-Host "BASELINE OK: $bs is ancestor of HEAD"
}
exit 0

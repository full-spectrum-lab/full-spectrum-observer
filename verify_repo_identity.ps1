#!/usr/bin/env pwsh
# Read-only repository identity precheck for FSO M1 verification.
#
# Prevents silently operating on the WRONG repo/branch (e.g. the Desktop
# codex showcase clone instead of the correct M1 working tree). Verifies:
#   1. absolute path  == expected M1 working tree
#   2. remote origin  == gitee.com/full-spectrum/full-spectrum-observer
#   3. current branch == feature/v0.3-observer-console
#   4. baseline 0ba4d12c5cc177256bbea4ccd579b887705ec3ff is an ANCESTOR of
#      HEAD (never hardcode HEAD; allows forward fix commits while still
#      guaranteeing we descend from the known-good M1 baseline).
#
# Usage:  pwsh verify_repo_identity.ps1 [-ExpectedPath <abs-path>]
# Exits 0 on match, 1 on mismatch. Strictly read-only: never modifies the tree.

param(
    [string]$ExpectedPath = (Resolve-Path '.').Path
)

$ErrorActionPreference = 'Stop'

$BaselineSha  = '0ba4d12c5cc177256bbea4ccd579b887705ec3ff'
$ExpectedRemote = 'gitee.com/full-spectrum/full-spectrum-observer'
$ExpectedBranch = 'feature/v0.3-observer-console'

function Fail($msg) {
    Write-Error "IDENTITY CHECK FAILED: $msg"
    exit 1
}

# 1) absolute path of the git toplevel must equal the expected M1 working tree.
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

# 4) baseline is an ancestor of current HEAD (forward fixes allowed).
$head = (git rev-parse HEAD).Trim()
git merge-base --is-ancestor $BaselineSha HEAD
if ($LASTEXITCODE -ne 0) {
    Fail "baseline $BaselineSha is NOT an ancestor of HEAD $head"
}

Write-Host "IDENTITY OK: toplevel=$toplevel branch=$branch HEAD=$head baseline=$BaselineSha is-ancestor"
exit 0

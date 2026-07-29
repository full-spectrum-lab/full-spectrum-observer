[CmdletBinding()]
param(
    # Repository root. Defaults to the parent of this script's directory.
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    # Optional label used only for output clarity (e.g. PRE_RUN / POST_TEST).
    [string]$Stage = ""
)

<#
.SYNOPSIS
    Worktree cleanliness gate for the official build/test pipeline.

.DESCRIPTION
    Runs `git status --porcelain` and fails (Write-Error + exit 1) if ANY tracked
    change is present (status codes M/A/D/R/C, i.e. anything other than untracked
    `?? `). Untracked files are intentionally ignored because run-generated evidence
    is redirected out of the repository (see FSP_EVIDENCE_ROOT).

    Outputs PACKAGES_LOCK_DRIFT = number of tracked changes touching
    packages.lock.json.

    IMPORTANT: This gate must never use `git checkout/restore/reset` to manufacture a
    clean tree. It only inspects; it never mutates the working tree.

    Only tracked changes are inspected; untracked (`??`) files do not cause failure.
#>

$ErrorActionPreference = "Stop"

$StageInfo = if ([string]::IsNullOrWhiteSpace($Stage)) { "" } else { " [$Stage]" }
Write-Host "FSP worktree cleanliness gate${StageInfo}: repo='$RepoRoot'"

$porcelain = & git -C $RepoRoot status --porcelain 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Error "git status failed (exit $LASTEXITCODE); cannot determine worktree state."
    exit 1
}

$dirty = [System.Collections.Generic.List[string]]::new()
$packagesLockDrift = 0

foreach ($line in $porcelain) {
    if ($null -eq $line -or $line.Length -lt 2) { continue }

    # First two characters are the porcelain status code (e.g. " M", "M ", "A ", "??").
    $statusCode = $line.Substring(0, 2)

    # Untracked (`?? `) files are permitted: run evidence is redirected out of the repo.
    if ($statusCode -eq "??") { continue }

    # Any other (tracked) status is a dirty change we must reject.
    $dirty.Add($line)
    if ($line -match 'packages\.lock\.json') {
        $packagesLockDrift++
    }
}

# Surface the packages.lock.json drift metric for downstream diagnostics.
Write-Host "PACKAGES_LOCK_DRIFT=$packagesLockDrift"

if ($dirty.Count -gt 0) {
    Write-Error ("Worktree is dirty${StageInfo}: {0} tracked change(s) detected " +
                 "(POST_TEST_TRACKED_CHANGES={0})." -f $dirty.Count)
    foreach ($entry in $dirty) {
        Write-Error "  DIRTY: $entry"
    }
    exit 1
}

Write-Host "Worktree is clean${StageInfo}: no tracked changes (untracked files ignored)."
exit 0

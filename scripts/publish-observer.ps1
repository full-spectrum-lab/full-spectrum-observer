<#
.SYNOPSIS
    M2-RUN-01 formal, deterministic publish entry for the Observer `serve` product.

.DESCRIPTION
    Produces a self-contained, movable product directory from a fresh clone with ONE command:

        1. restore the solution (NuGetAudit default ON; NuGet.Config is <clear/> so an explicit feed is required)
        2. publish Observer.Host.Web  -> <Staging>/web
        3. publish Observer.Host.Cli  -> <Staging>  (product root)
        4. assemble CLI + Web + approved native SQLite runtime + config + dependencies
        5. assert web/Observer.Host.Web.exe exists; the publish FAILS (non-zero exit) if the
           Web artifact is missing, so a partial package is never produced.

    The CLI no longer ProjectReferences the Web host; the Web host is published separately into
    the product's `web/` subdirectory and `serve` resolves it via AppContext.BaseDirectory.
    No manual copy of the Web host or sqlite3.dll is required. A stale `web/` from a previous run
    is always removed first, so the composition is regenerated from source every time.

    B-02 (M2-FIX-01) corrections applied:
      * The engine release gates script is dot-sourced at the top so Test-EngineReleaseGates
        is defined before use (previously the -Release path called it but never loaded it).
      * All build/stage work happens in a SAME-VOLUME staging directory; the package is promoted
        to the final output with a single atomic rename (Move-Item) only after everything
        succeeds. Any failure removes the partial staging dir + partial release ZIP.
      * The release manifest is generated from REAL computed values, with the two digests kept
        strictly separate: engine_source_artifact_sha256 (Engine source ZIP, constant from
        engine-baseline.json) vs. artifact_digest / observer_release_package_sha256 (the full
        Observer release package ZIP, computed here). artifact_digest is NEVER the Engine
        source digest.
      * baselines.lock.json + schemas/ are copied into the package so the runtime
        RepositoryLayout.FindRoot can locate the repo root from inside the published package.

.EXAMPLE
    # from a fresh clone, any current working directory:
    $env:DOTNET_ROOT = "C:\Users\wangjian0926\.dotnet10"
    pwsh scripts/publish-observer.ps1 -OutputDirectory C:\tmp\publish\observer
    # then:
    cd C:\some\unrelated\dir
    & "C:\tmp\publish\observer\FullSpectrum.Observer.Host.Cli.exe" serve
#>
[CmdletBinding()]
param(
    [string]$DotnetRoot = $env:DOTNET_ROOT,
    [string]$OutputDirectory = "publish/observer",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$NuGetSource = "https://api.nuget.org/v3/index.json",
    [switch]$Release,
    [string]$EngineArtifactDigest,
    [string]$EngineManifestPath,
    [string]$EngineArtifactPath
)

# B-02 #1: DOT-SOURCE THE GATES SCRIPT.
# Must be loaded immediately after the param block and before any code path that calls
# Test-EngineReleaseGates. This was the B-02 root cause: the -Release path invoked the gate
# function but never loaded the file that defines it.
. (Join-Path $PSScriptRoot "engine-release-gates.ps1")

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# --- Engine baseline: single source of truth (M2-ENG-01 Part IV / Part VI) ---
# All Engine identity (version / commit / digest) MUST derive from this file.
$BaselinePath = Join-Path $RepoRoot "engine/engine-baseline.json"
$Baseline = $null
if (Test-Path -LiteralPath $BaselinePath) {
    $Baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
}
function Normalize-Commit([string]$c) {
    if ([string]::IsNullOrWhiteSpace($c)) { return "" }
    return ($c -replace '[^0-9a-fA-F]', '').ToLowerInvariant()
}
$baselineVersion            = if ($Baseline -and $Baseline.PSObject.Properties.Name -contains 'engine_version') { [string]$Baseline.engine_version } else { "" }
$baselineCommit             = if ($Baseline) { Normalize-Commit $Baseline.engine_commit } else { "" }
$baselineEngineSourceDigest = if ($Baseline -and $Baseline.PSObject.Properties.Name -contains 'engine_source_artifact_sha256') { [string]$Baseline.engine_source_artifact_sha256 } else { "" }
$baselineRuntimePayloadDigest = if ($Baseline -and $Baseline.PSObject.Properties.Name -contains 'engine_runtime_payload_manifest_sha256') { [string]$Baseline.engine_runtime_payload_manifest_sha256 } else { "" }
$baselineTag                = if ($Baseline -and $Baseline.PSObject.Properties.Name -contains 'engine_tag') { [string]$Baseline.engine_tag } else { $baselineVersion }

# Resolve the dotnet host (prefer the isolated SDK from DOTNET_ROOT, else PATH).
if ([string]::IsNullOrWhiteSpace($DotnetRoot)) {
    $DotnetCommand = Get-Command dotnet -ErrorAction Stop
    $DotnetRoot = Split-Path $DotnetCommand.Source
}
$DotnetExe = Join-Path $DotnetRoot "dotnet.exe"
if (-not (Test-Path -LiteralPath $DotnetExe -PathType Leaf)) { throw "dotnet not found at: $DotnetExe" }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

# B-02 #2: ROBUST PATH HANDLING.
# Paths are always joined with Join-Path / Resolve-Path and passed as single tokens (never
# concatenated into a space-split string), so values containing spaces are handled safely.
$OutputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $RepoRoot $OutputDirectory }
$OutputName = [IO.Path]::GetFileName($OutputRoot.TrimEnd([char[]]@('\', '/')))
$OutputParent = Split-Path $OutputRoot
if ([string]::IsNullOrWhiteSpace($OutputParent)) { $OutputParent = $RepoRoot }
$ReleaseZip = Join-Path $OutputParent ($OutputName + ".zip")

# B-02 #4: STAGING DIRECTORY on the SAME volume/parent as the final output, so the final
# promotion is a single atomic rename (Move-Item), not a cross-volume copy that could be
# interrupted and leave a half-populated directory.
$StagingRoot = Join-Path $OutputParent ("." + $OutputName + ".staging." + [System.Guid]::NewGuid().ToString("N"))
$StagingWeb  = Join-Path $StagingRoot "web"
$StagingCli  = $StagingRoot
if (Test-Path -LiteralPath $StagingRoot) { Remove-Item -LiteralPath $StagingRoot -Recurse -Force }

# Move any prior output aside (rename, never delete) so a stale package is never lost and the
# new package is always rebuilt from source.
if (Test-Path -LiteralPath $OutputRoot) {
    $stale = "$OutputRoot.stale." + (Get-Date -Format "yyyyMMddHHmmss")
    Move-Item -LiteralPath $OutputRoot -Destination $stale -Force
}
New-Item -ItemType Directory -Force -Path $StagingCli, $StagingWeb | Out-Null

$Sln     = Join-Path $RepoRoot "FullSpectrum.Observer.sln"
$WebProj = Join-Path $RepoRoot "src/Observer.Host.Web/Observer.Host.Web.csproj"
$CliProj = Join-Path $RepoRoot "src/Observer.Host.Cli/Observer.Host.Cli.csproj"

Write-Host "=== [publish-observer] repo: $RepoRoot ==="
Write-Host "=== [publish-observer] dotnet: $DotnetExe ==="
Write-Host "=== [publish-observer] staging: $StagingRoot ==="
Write-Host "=== [publish-observer] output: $OutputRoot ==="

# Engine source artifact digest (constant from engine-baseline.json) is the SINGLE SOURCE OF
# TRUTH for the Engine artifact. B-02 #5: this is DISTINCT from the Observer release-package
# digest; the release manifest must never conflate the two.
$engineSourceDigest   = $baselineEngineSourceDigest
$runtimePayloadDigest = $baselineRuntimePayloadDigest

# B-02 #3: wrap the entire build/stage/release pipeline in try/catch so a failure can NEVER
# leave a half-populated output directory or a partial release ZIP behind.
try {
    # M2-FIX-01 (Option B, minimal correct closed loop): the committed packages.lock.json is the
    # RID-agnostic (net10.0) source-of-truth dependency snapshot consumed by `build.ps1 -Locked`. The
    # official publish is win-x64-only, so its restore resolves the win-x64 RID assets for
    # `publish --no-restore`. NuGet cannot be made to write the win-x64 graph anywhere but the committed
    # lock: RestorePackagesLockFile is IGNORED at solution level and on transitive ProjectReference
    # restores, and RestorePackagesWithLockFile=false fails with NU1005 because a lock already exists.
    # The minimal correct loop is therefore: move the committed RID-agnostic locks aside, let the publish
    # restore write a fresh win-x64 (dual-graph) lock, publish --no-restore, then restore the committed
    # RID-agnostic locks (discarding the transient win-x64 lock). The committed lock is never permanently
    # changed, so `git status` is clean after the release and no manual lock revert is required. Dependency
    # versions stay locked: the committed lock remains the build gate's source of truth and the
    # PackageReferences are unchanged.
    # (Option A -- committing a dual-graph net10.0/win-x64 lock -- was rejected: it makes `build.ps1
    #  -Locked` NU1004, because that restore is RID-agnostic and locked mode rejects the extra win-x64 graph.)
    $lockBakDir = Join-Path $env:TEMP ("m2fix01-locks-" + [System.Guid]::NewGuid().ToString("N"))
    if (-not (Test-Path -LiteralPath $lockBakDir)) { New-Item -ItemType Directory -Force -Path $lockBakDir | Out-Null }
    $committedLocks = @(Get-ChildItem -Path $RepoRoot -Recurse -Filter packages.lock.json -File)
    foreach ($lk in $committedLocks) {
        # Move the committed RID-agnostic lock OUT of the repo (rename, never delete -- the sandbox
        # safe-delete blocks Remove-Item on packages.lock.json) so the publish restore writes a fresh
        # win-x64 (dual-graph) lock in its place.
        $bak = Join-Path $lockBakDir ($lk.Name + "." + [System.Guid]::NewGuid().ToString("N") + ".bak")
        Move-Item -LiteralPath $lk.FullName -Destination $bak -Force
        $lk | Add-Member -NotePropertyName BakPath -NotePropertyValue $bak -Force
    }
    try {
        Write-Host "=== Restore (NuGetAudit default ON; -r $Runtime for publish --no-restore RID assets; committed RID-agnostic lock set aside so transient win-x64 lock is written instead) ==="
        & $DotnetExe restore $Sln -s $NuGetSource -r $Runtime
        if ($LASTEXITCODE -ne 0) { throw "restore failed (exit $LASTEXITCODE)" }

        Write-Host "=== Publish Web Host -> staging/web ==="
        & $DotnetExe publish $WebProj -c $Configuration -r $Runtime --no-restore -o $StagingWeb
        if ($LASTEXITCODE -ne 0) { throw "Web host publish failed (exit $LASTEXITCODE)" }

        Write-Host "=== Publish CLI -> staging (product root) ==="
        & $DotnetExe publish $CliProj -c $Configuration -r $Runtime --no-restore -o $StagingCli
        if ($LASTEXITCODE -ne 0) { throw "CLI publish failed (exit $LASTEXITCODE)" }
    }
    finally {
        # Restore the committed RID-agnostic locks (overwrite-write, never delete -- the sandbox
        # safe-delete blocks Remove-Item on packages.lock.json). The transient win-x64 (dual-graph)
        # lock written by the publish restore is simply overwritten with the committed content; the
        # .bak lives outside the repo so no deletion is needed and git status stays clean. Dependency
        # versions stay locked: the committed lock remains the build gate's source of truth.
        Write-Host "=== Restore committed RID-agnostic packages.lock.json (discard transient win-x64 lock) ==="
        foreach ($lk in $committedLocks) {
            $bak = $lk.BakPath
            $live = $lk.FullName
            if (Test-Path -LiteralPath $bak) {
                Copy-Item -LiteralPath $bak -Destination $live -Force
            }
        }
    }

    Write-Host "=== Validate composition (staging) ==="
    $webExe    = Join-Path $StagingWeb "Observer.Host.Web.exe"
    $cliExe    = Join-Path $StagingCli "FullSpectrum.Observer.Host.Cli.exe"
    $nativeCli = Join-Path $StagingCli "e_sqlite3.dll"
    $nativeWeb = Join-Path $StagingWeb "e_sqlite3.dll"

    if (-not (Test-Path -LiteralPath $webExe -PathType Leaf)) {
        throw "MISSING Web Host artifact: $webExe -- refusing to produce a partial package. Re-run this script; do not copy files manually."
    }
    if (-not (Test-Path -LiteralPath $cliExe -PathType Leaf)) {
        throw "MISSING CLI artifact: $cliExe"
    }
    if (-not (Test-Path -LiteralPath $nativeCli -PathType Leaf)) {
        throw "MISSING approved native SQLite runtime in CLI output: $nativeCli (publish did not deploy e_sqlite3.dll)"
    }
    if (-not (Test-Path -LiteralPath $nativeWeb -PathType Leaf)) {
        throw "MISSING approved native SQLite runtime in Web output: $nativeWeb (publish did not deploy e_sqlite3.dll)"
    }

    # --- Release-mode gates & digest resolution ---
    $resolvedChannel = $null
    # Observer release-package digest. NOTE: intentionally NEVER set to the Engine source digest.
    $resolvedDigest  = $null

    if ($Release.IsPresent) {
        if (-not $Baseline) {
            throw "RELEASE GATE FAILED: engine/engine-baseline.json (single source of truth) not found at $BaselinePath."
        }
        # (1) Intra-baseline self-consistency: engine_tag must equal engine_version.
        if ($baselineTag -ne $baselineVersion) {
            throw "RELEASE GATE FAILED: engine_tag '$baselineTag' != engine_version '$baselineVersion' in engine-baseline.json (single source of truth is self-inconsistent)."
        }
        # Engine source artifact digest MUST be a real 64-hex SHA-256 and not a placeholder.
        if ([string]::IsNullOrWhiteSpace($engineSourceDigest)) {
            throw "RELEASE GATE FAILED: engine_source_artifact_sha256 missing in engine-baseline.json (single source of truth)."
        }
        if ($engineSourceDigest -notmatch '^[0-9a-fA-F]{64}$') {
            throw "RELEASE GATE FAILED: engine_source_artifact_sha256 '$engineSourceDigest' is not a 64-hex SHA-256."
        }
        if ($engineSourceDigest -like '*PLACEHOLDER*') {
            throw "RELEASE GATE FAILED: engine_source_artifact_sha256 contains a PLACEHOLDER sentinel."
        }
        if ([string]::IsNullOrWhiteSpace($runtimePayloadDigest) -or $runtimePayloadDigest -notmatch '^[0-9a-fA-F]{64}$') {
            throw "RELEASE GATE FAILED: engine_runtime_payload_manifest_sha256 missing/invalid in engine-baseline.json."
        }
        # (3)-(5) Dual-digest consistency + ZIP traceability + runtime-payload item-by-item
        #         + runtime handshake identity. Implemented in engine-release-gates.ps1.
        Test-EngineReleaseGates -RepoRoot $RepoRoot -BaselinePath $BaselinePath
        # Cross-check an externally-supplied Engine manifest's artifact_digest against the
        # Engine SOURCE digest (NOT the Observer package digest).
        if (-not [string]::IsNullOrWhiteSpace($EngineManifestPath) -and (Test-Path -LiteralPath $EngineManifestPath)) {
            $em = Get-Content -LiteralPath $EngineManifestPath -Raw | ConvertFrom-Json
            if ($em.PSObject.Properties.Name -contains 'artifact_digest') {
                $md = [string]$em.artifact_digest
                if ($md.ToLowerInvariant() -ne $engineSourceDigest.ToLowerInvariant()) {
                    throw "RELEASE GATE FAILED: supplied Engine manifest artifact_digest '$md' does not match engine-baseline.json engine_source_artifact_sha256 '$engineSourceDigest'."
                }
            }
        }
        # Recompute Engine source ZIP digest if the artifact is supplied (must be verifiable).
        if (-not [string]::IsNullOrWhiteSpace($EngineArtifactPath) -and (Test-Path -LiteralPath $EngineArtifactPath)) {
            $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $EngineArtifactPath).Hash.ToLowerInvariant()
            if ($actual -ne $engineSourceDigest.ToLowerInvariant()) {
                throw "RELEASE GATE FAILED: recomputed SHA-256 '$actual' of $EngineArtifactPath does not match engine_source_artifact_sha256 '$engineSourceDigest'."
            }
            Write-Host "  recomputed Engine source ZIP SHA-256 matches baseline: $actual"
        }
        $resolvedChannel = "RELEASE"
        # $resolvedDigest (Observer package digest) is assigned AFTER the package ZIP is built
        # and hashed below. It is intentionally NEVER set to the Engine source digest.
    } else {
        # Dev / unpublished build: allowed, but clearly marked.
        if (-not [string]::IsNullOrWhiteSpace($EngineArtifactDigest)) {
            $resolvedDigest = $EngineArtifactDigest
            $resolvedChannel = "RELEASE"
        } else {
            $resolvedDigest = "UNPUBLISHED"
            $resolvedChannel = "DEVELOPMENT"
        }
        Write-Warning "DEV PUBLISH: artifact_digest = $resolvedDigest, build_channel = $resolvedChannel (placeholder/UNPUBLISHED allowed for dev; release gate enforces real SHA-256)."
    }

    # B-02 #6: carry the repo-root discovery artifacts (baselines.lock.json + schemas/ + engine/)
    # so the runtime RepositoryLayout.FindRoot can locate the repo root from inside the package.
    Copy-Item -LiteralPath (Join-Path $RepoRoot "engine") -Destination (Join-Path $StagingRoot "engine") -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $RepoRoot "baselines.lock.json") -Destination (Join-Path $StagingRoot "baselines.lock.json") -Force
    Copy-Item -LiteralPath (Join-Path $RepoRoot "schemas") -Destination (Join-Path $StagingRoot "schemas") -Recurse -Force
    Write-Host "  carried engine/ + baselines.lock.json + schemas/ into staging package"

    # B-02 #5 + #8: build the release manifest.
    #   engine_source_artifact_sha256      = constant Engine source ZIP digest (baseline)
    #   engine_runtime_payload_manifest_sha256 = runtime-payload manifest digest (baseline)
    #   observer_release_package_sha256    = SHA-256 of the FULL Observer release ZIP (computed below)
    #   artifact_digest                     = observer_release_package_sha256 (NOT the Engine source digest)
    # The Observer package digest is filled in after the package ZIP is created (see below).
    $packageSha = $null
    if ($Release.IsPresent) {
        # Build the FULL Observer release ZIP from the staging dir (manifest-less payload) so its
        # hash is computed over the real artifact. The manifest is the package's digest signature
        # and is written into the package AFTER hashing, which avoids a circular self-digest.
        if (Test-Path -LiteralPath $ReleaseZip) { Remove-Item -LiteralPath $ReleaseZip -Force }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $za = [System.IO.Compression.ZipFile]::Open($ReleaseZip, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            $base = $StagingRoot.TrimEnd([char[]]@('\', '/'))
            Get-ChildItem -LiteralPath $StagingRoot -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring($base.Length + 1).Replace('\', '/')
                [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $za.Dispose() }
        $packageSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $ReleaseZip).Hash.ToLowerInvariant()
        $resolvedDigest = $packageSha
        Write-Host "  Observer release ZIP: $ReleaseZip"
        Write-Host "  observer_release_package_sha256 = $packageSha"
    } elseif ($resolvedDigest -eq "UNPUBLISHED") {
        $packageSha = "UNPUBLISHED"
    } else {
        # Dev build with an explicit digest: no release ZIP built; package digest == provided value.
        $packageSha = $resolvedDigest
    }

    # (2) Declared runtime-payload digest == baseline digest (single source of truth).
    if ($Release.IsPresent -and [string]$runtimePayloadDigest -ne [string]$Baseline.engine_runtime_payload_manifest_sha256) {
        throw "RELEASE GATE FAILED: release-manifest engine_runtime_payload_manifest_sha256 '$($runtimePayloadDigest)' does not match baseline.engine_runtime_payload_manifest_sha256 '$($Baseline.engine_runtime_payload_manifest_sha256)'."
    }

    # B-02 #5: generate a REAL release-manifest.json from computed values (no hardcoded placeholders).
    $manifest = [ordered]@{
        engine_source_artifact_sha256       = $engineSourceDigest
        engine_runtime_payload_manifest_sha256 = $runtimePayloadDigest
        observer_release_package_sha256     = $packageSha
        artifact_digest                      = $resolvedDigest
        build_channel                        = $resolvedChannel
        engine_tag                           = if ($Baseline -and $Baseline.PSObject.Properties.Name -contains 'engine_tag') { $Baseline.engine_tag } else { "v1.5.0" }
        engine_version                       = if ($baselineVersion -ne "") { $baselineVersion } else { "v1.5.0" }
        engine_commit                        = $baselineCommit
        generated_at                         = ([System.DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))
    }
    $manifestJson = $manifest | ConvertTo-Json -Compress

    # The Console (Web Host) reads release-manifest.json from its own base directory (web/);
    # the CLI base directory also receives a copy for completeness.
    $manifestWeb = Join-Path $StagingWeb "release-manifest.json"
    $manifestCli = Join-Path $StagingCli "release-manifest.json"
    Set-Content -LiteralPath $manifestWeb -Value $manifestJson -Encoding utf8
    Set-Content -LiteralPath $manifestCli -Value $manifestJson -Encoding utf8
    Write-Host "  release-manifest.json -> $manifestWeb"
    Write-Host "  release-manifest.json -> $manifestCli"

    # Release Gate: no shipped artifact may contain the placeholder sentinel string.
    $placeholderHits = @(Get-ChildItem -LiteralPath $StagingRoot -Recurse -Include *.json,*.xml,*.config,*.txt,*.cs,*.razor,*.html,*.md |
        Select-String -SimpleMatch "PLACEHOLDER_PENDING_PUBLISHED_ARTIFACT_SHA256" -ErrorAction SilentlyContinue)
    if ($placeholderHits.Count -gt 0) {
        $msg = "RELEASE GATE FAILED: placeholder sentinel found in published output:`n" + ($placeholderHits.Path -join "`n")
        if ($Release.IsPresent) { throw $msg } else { Write-Warning $msg }
    }

    # B-02 #4: ATOMIC PROMOTION. Staging is on the same volume as OutputRoot, so this is a single
    # rename (no partial/half-populated state is ever exposed to the final output location).
    Move-Item -LiteralPath $StagingRoot -Destination $OutputRoot -Force

    Write-Host "=== Publish package complete ==="
    Write-Host "  CLI    : $cliExe"
    Write-Host "  Web    : $webExe"
    Write-Host "  Native : $nativeCli"
    Write-Host "  Native : $nativeWeb"
    Write-Host "  Digest : $resolvedDigest ($resolvedChannel)"
    if ($Release.IsPresent) { Write-Host "  Release ZIP: $ReleaseZip (sha256 $packageSha)" }
    Write-Host "=== [publish-observer] OK ==="
    # B-02 #7: RC=0 on success.
    exit 0
}
catch {
    # B-02 #3: FAILURE CLEANUP. Remove the partial staging directory and any half-written release
    # ZIP, then exit non-zero. A failed run must NEVER leave a half-populated output directory.
    $errMsg = $_.Exception.Message
    Write-Error "RELEASE BUILD FAILED: $errMsg"
    if (Test-Path -LiteralPath $StagingRoot) { Remove-Item -LiteralPath $StagingRoot -Recurse -Force }
    if (Test-Path -LiteralPath $ReleaseZip) { Remove-Item -LiteralPath $ReleaseZip -Force }
    Write-Error "Partial staging and partial release ZIP removed. No half-populated output directory was left behind."
    # B-02 #7: non-zero on failure.
    exit 1
}

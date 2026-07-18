<#
.SYNOPSIS
    M2-RUN-01 formal, deterministic publish entry for the Observer `serve` product.

.DESCRIPTION
    Produces a self-contained, movable product directory from a fresh clone with ONE command:

        1. restore the solution (NuGetAudit default ON; NuGet.Config is <clear/> so an explicit feed is required)
        2. publish Observer.Host.Web  -> <OutputDirectory>/web
        3. publish Observer.Host.Cli  -> <OutputDirectory>  (product root)
        4. assemble CLI + Web + approved native SQLite runtime + config + dependencies
        5. assert web/Observer.Host.Web.exe exists; the publish FAILS (non-zero exit) if the
           Web artifact is missing, so a partial package is never produced.

    The CLI no longer ProjectReferences the Web host; the Web host is published separately into
    the product's `web/` subdirectory and `serve` resolves it via AppContext.BaseDirectory.
    No manual copy of the Web host or sqlite3.dll is required. A stale `web/` from a previous run
    is always removed first, so the composition is regenerated from source every time.

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

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# --- Engine baseline: single source of truth (M2-ENG-01 Part IV / Part VI) ---
# All Engine identity (version / commit / digest) MUST derive from this file.
$BaselinePath = Join-Path $RepoRoot "engine/engine-baseline.json"
$Baseline = $null
if (Test-Path $BaselinePath) {
    $Baseline = Get-Content $BaselinePath -Raw | ConvertFrom-Json
}
function Normalize-Commit([string]$c) {
    if ([string]::IsNullOrWhiteSpace($c)) { return "" }
    return ($c -replace '[^0-9a-fA-F]', '').ToLowerInvariant()
}
$baselineVersion = if ($Baseline -and $Baseline.PSObject.Properties.Name -contains 'engine_version') { [string]$Baseline.engine_version } else { "" }
$baselineCommit  = if ($Baseline) { Normalize-Commit $Baseline.engine_commit } else { "" }
$baselineDigest  = if ($Baseline -and $Baseline.PSObject.Properties.Name -contains 'artifact_sha256') { [string]$Baseline.artifact_sha256 } else { "" }
$baselineTag     = if ($Baseline -and $Baseline.PSObject.Properties.Name -contains 'engine_tag') { [string]$Baseline.engine_tag } else { $baselineVersion }

# Resolve the dotnet host (prefer the isolated SDK from DOTNET_ROOT, else PATH).
if ([string]::IsNullOrWhiteSpace($DotnetRoot)) {
    $DotnetCommand = Get-Command dotnet -ErrorAction Stop
    $DotnetRoot = Split-Path $DotnetCommand.Source
}
$DotnetExe = Join-Path $DotnetRoot "dotnet.exe"
if (-not (Test-Path $DotnetExe -PathType Leaf)) { throw "dotnet not found at: $DotnetExe" }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

$OutputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $RepoRoot $OutputDirectory }
$WebOut = Join-Path $OutputRoot "web"
$CliOut = $OutputRoot

# Always regenerate from source. In delete-protected environments a hard Remove-Item can be
# intercepted and fail closed, so we MOVE any prior output aside (rename) instead of deleting.
# A fresh clone has no prior output, so this is a no-op there; either way the full product
# directory is always rebuilt from source (including a stale web/ subdir).
if (Test-Path $OutputRoot) {
    $stale = "$OutputRoot.stale." + (Get-Date -Format "yyyyMMddHHmmss")
    Move-Item -Path $OutputRoot -Destination $stale -Force
}
New-Item -ItemType Directory -Force -Path $CliOut, $WebOut | Out-Null

$Sln     = Join-Path $RepoRoot "FullSpectrum.Observer.sln"
$WebProj = Join-Path $RepoRoot "src/Observer.Host.Web/Observer.Host.Web.csproj"
$CliProj = Join-Path $RepoRoot "src/Observer.Host.Cli/Observer.Host.Cli.csproj"

Write-Host "=== [publish-observer] repo: $RepoRoot ==="
Write-Host "=== [publish-observer] dotnet: $DotnetExe ==="
Write-Host "=== [publish-observer] output: $OutputRoot ==="

Write-Host "=== Restore (NuGetAudit default ON; -r $Runtime so publish --no-restore has the RID asset target) ==="
& $DotnetExe restore $Sln -s $NuGetSource -r $Runtime
if ($LASTEXITCODE -ne 0) { throw "restore failed (exit $LASTEXITCODE)" }

Write-Host "=== Publish Web Host -> web/ ==="
& $DotnetExe publish $WebProj -c $Configuration -r $Runtime --no-restore -o $WebOut
if ($LASTEXITCODE -ne 0) { throw "Web host publish failed (exit $LASTEXITCODE)" }

Write-Host "=== Publish CLI -> product root ==="
& $DotnetExe publish $CliProj -c $Configuration -r $Runtime --no-restore -o $CliOut
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed (exit $LASTEXITCODE)" }

Write-Host "=== Validate composition ==="
$webExe  = Join-Path $WebOut  "Observer.Host.Web.exe"
$cliExe  = Join-Path $CliOut  "FullSpectrum.Observer.Host.Cli.exe"
$nativeCli = Join-Path $CliOut "e_sqlite3.dll"
$nativeWeb = Join-Path $WebOut "e_sqlite3.dll"

if (-not (Test-Path $webExe -PathType Leaf)) {
    throw "MISSING Web Host artifact: $webExe -- refusing to produce a partial package. Re-run this script; do not copy files manually."
}
if (-not (Test-Path $cliExe -PathType Leaf)) {
    throw "MISSING CLI artifact: $cliExe"
}
if (-not (Test-Path $nativeCli -PathType Leaf)) {
    throw "MISSING approved native SQLite runtime in CLI output: $nativeCli (publish did not deploy e_sqlite3.dll)"
}
if (-not (Test-Path $nativeWeb -PathType Leaf)) {
    throw "MISSING approved native SQLite runtime in Web output: $nativeWeb (publish did not deploy e_sqlite3.dll)"
}

Write-Host "=== Generate release-manifest.json (single source of truth for artifact digest) ==="
$resolvedDigest = $null
$resolvedChannel = $null

if ($Release.IsPresent) {
    # Release mode: a real, valid digest is mandatory.
    if (-not [string]::IsNullOrWhiteSpace($EngineManifestPath) -and (Test-Path $EngineManifestPath)) {
        $em = Get-Content $EngineManifestPath -Raw | ConvertFrom-Json
        if ($em.PSObject.Properties.Name -contains 'artifact_digest') { $resolvedDigest = [string]$em.artifact_digest }
        if ($em.PSObject.Properties.Name -contains 'build_channel') { $resolvedChannel = [string]$em.build_channel }
    }
    if ([string]::IsNullOrWhiteSpace($resolvedDigest) -and -not [string]::IsNullOrWhiteSpace($EngineArtifactDigest)) {
        $resolvedDigest = $EngineArtifactDigest
    }
    if ([string]::IsNullOrWhiteSpace($resolvedDigest) -and $Baseline -and $Baseline.PSObject.Properties.Name -contains 'engine_source_artifact_sha256') {
        $resolvedDigest = [string]$Baseline.engine_source_artifact_sha256
    }
    if ([string]::IsNullOrWhiteSpace($resolvedDigest)) {
        throw "RELEASE GATE FAILED: a real Engine artifact digest is required (engine-baseline.json or -EngineArtifactDigest / -EngineManifestPath). Refusing to publish a release with UNPUBLISHED/placeholder digest."
    }
    # Single source of truth: in RELEASE mode the resolved digest MUST equal the baseline digest.
    if ($Baseline -and $Baseline.PSObject.Properties.Name -contains 'engine_source_artifact_sha256') {
        if ($resolvedDigest.ToLowerInvariant() -ne [string]$Baseline.engine_source_artifact_sha256.ToLowerInvariant()) {
            throw "RELEASE GATE FAILED: resolved digest '$resolvedDigest' does not match engine-baseline.json engine_source_artifact_sha256 '$($Baseline.engine_source_artifact_sha256)' (single source of truth)."
        }
    }
    # Must be a valid 64-hex SHA-256 and must NOT be a placeholder.
    if ($resolvedDigest -notmatch '^[0-9a-fA-F]{64}$') {
        throw "RELEASE GATE FAILED: artifact_digest '$resolvedDigest' is not a 64-hex SHA-256."
    }
    if ($resolvedDigest -like '*PLACEHOLDER*') {
        throw "RELEASE GATE FAILED: artifact_digest contains a PLACEHOLDER sentinel."
    }
    # --- M2-ENG-01 Part VI: Declared = Manifest = Packaged = Runtime consistency gates ---
    if (-not $Baseline) {
        throw "RELEASE GATE FAILED: engine/engine-baseline.json (single source of truth) not found at $BaselinePath."
    }
    # (1) Intra-baseline self-consistency: engine_tag must equal engine_version.
    if ($baselineTag -ne $baselineVersion) {
        throw "RELEASE GATE FAILED: engine_tag '$baselineTag' != engine_version '$baselineVersion' in engine-baseline.json (single source of truth is self-inconsistent)."
    }
    # (2) Declared Digest == baseline digest already enforced above (single source of truth).

    # (3)-(5) Dual-digest consistency + ZIP traceability + runtime-payload item-by-item
    #         + runtime handshake identity. Implemented in engine-release-gates.ps1.
    #         The source artifact (full ZIP, 355 entries) and the runtime payload
    #         (24 vendored files + 2 worker files) are DISTINCT objects proven by
    #         DISTINCT digests; they must never be conflated into one digest.
    Test-EngineReleaseGates -RepoRoot $RepoRoot -BaselinePath $BaselinePath
    # Cross-check against provided manifest.
    if (-not [string]::IsNullOrWhiteSpace($EngineManifestPath) -and (Test-Path $EngineManifestPath)) {
        $em = Get-Content $EngineManifestPath -Raw | ConvertFrom-Json
        if ($em.PSObject.Properties.Name -contains 'artifact_digest') {
            $md = [string]$em.artifact_digest
            if ($md -ne $resolvedDigest) {
                throw "RELEASE GATE FAILED: provided Engine manifest digest '$md' does not match resolved digest '$resolvedDigest'."
            }
        }
    }
    # Recompute from the actual artifact if supplied (must be recomputable/verifiable).
    if (-not [string]::IsNullOrWhiteSpace($EngineArtifactPath) -and (Test-Path $EngineArtifactPath)) {
        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $EngineArtifactPath).Hash.ToLowerInvariant()
        if ($actual -ne $resolvedDigest.ToLowerInvariant()) {
            throw "RELEASE GATE FAILED: recomputed SHA-256 '$actual' of $EngineArtifactPath does not match declared digest '$resolvedDigest'."
        }
        Write-Host "  recomputed SHA-256 matches declared digest: $actual"
    }
    if ([string]::IsNullOrWhiteSpace($resolvedChannel)) { $resolvedChannel = "RELEASE" }
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

$manifest = [ordered]@{
    artifact_digest = $resolvedDigest
    build_channel   = $resolvedChannel
    engine_tag      = if ($Baseline -and $Baseline.PSObject.Properties.Name -contains 'engine_tag') { $Baseline.engine_tag } else { "v1.5.0" }
    engine_version  = if ($baselineVersion -ne "") { $baselineVersion } else { "v1.5.0" }
    engine_commit   = $baselineCommit
    engine_source_artifact_sha256      = if ($Baseline -and $Baseline.PSObject.Properties.Name -contains 'engine_source_artifact_sha256') { [string]$Baseline.engine_source_artifact_sha256 } else { $resolvedDigest }
    engine_runtime_payload_manifest_sha256 = if ($Baseline -and $Baseline.PSObject.Properties.Name -contains 'engine_runtime_payload_manifest_sha256') { [string]$Baseline.engine_runtime_payload_manifest_sha256 } else { "UNPUBLISHED" }
    generated_at    = ([System.DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))
}
$manifestJson = $manifest | ConvertTo-Json -Compress
if ($Release.IsPresent -and [string]$manifest.engine_runtime_payload_manifest_sha256 -ne [string]$Baseline.engine_runtime_payload_manifest_sha256) {
    throw "RELEASE GATE FAILED: release-manifest engine_runtime_payload_manifest_sha256 '$($manifest.engine_runtime_payload_manifest_sha256)' does not match baseline.engine_runtime_payload_manifest_sha256 '$($Baseline.engine_runtime_payload_manifest_sha256)'."
}
# The Console (Web Host) reads release-manifest.json from its own base directory (web/);
# the CLI base directory also receives a copy for completeness.
$manifestWeb = Join-Path $WebOut "release-manifest.json"
$manifestCli = Join-Path $CliOut "release-manifest.json"
Set-Content -Path $manifestWeb -Value $manifestJson -Encoding utf8
Set-Content -Path $manifestCli -Value $manifestJson -Encoding utf8
Write-Host "  release-manifest.json -> $manifestWeb"
Write-Host "  release-manifest.json -> $manifestCli"

# Mode A release package must actually carry the Engine artifacts
# (baseline + vendored runtime tree + runtime-payload-manifest + source ZIP).
$engineOut = Join-Path $OutputRoot "engine"
Copy-Item -Path (Join-Path $RepoRoot "engine") -Destination $engineOut -Recurse -Force
Write-Host "  carried engine/ into package (baseline + vendored tree + runtime-payload-manifest + source ZIP)"

# Release Gate: no shipped artifact may contain the placeholder sentinel string.
$placeholderHits = @(Get-ChildItem -Path $OutputRoot -Recurse -Include *.json,*.xml,*.config,*.txt,*.cs,*.razor,*.html,*.md |
    Select-String -SimpleMatch "PLACEHOLDER_PENDING_PUBLISHED_ARTIFACT_SHA256" -ErrorAction SilentlyContinue)
if ($placeholderHits.Count -gt 0) {
    $msg = "RELEASE GATE FAILED: placeholder sentinel found in published output:`n" + ($placeholderHits.Path -join "`n")
    if ($Release.IsPresent) { throw $msg } else { Write-Warning $msg }
}

Write-Host "=== Publish package complete ==="
Write-Host "  CLI    : $cliExe"
Write-Host "  Web    : $webExe"
Write-Host "  Native : $nativeCli"
Write-Host "  Native : $nativeWeb"
Write-Host "  Digest : $resolvedDigest ($resolvedChannel)"
Write-Host "=== [publish-observer] OK ==="

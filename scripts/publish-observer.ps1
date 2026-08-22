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
    $env:DOTNET_ROOT = "C:\path\to\dotnet"
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
    [string]$EngineArtifactPath,
    # M2-FIX-03: source of the self-contained Python runtime. Supplied by CI / WorkBuddy — a
    # directory containing python.exe (the pre-built 3.12+ distribution). Do NOT hardcode a network
    # download inside this script.
    [string]$PythonSource,
    # M2-FIX-03: offline wheel cache (directory of .whl files) for numpy + jsonschema, installed
    # into the provisioned runtime with --no-index.
    [string]$WheelCache
)

# B-02 #1: DOT-SOURCE THE GATES SCRIPT.
# Must be loaded immediately after the param block and before any code path that calls
# Test-EngineReleaseGates. This was the B-02 root cause: the -Release path invoked the gate
# function but never loaded the file that defines it.
. (Join-Path $PSScriptRoot "engine-release-gates.ps1")

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Get-DirectoryTreeSha256([string]$Path) {
    $base = (Resolve-Path -LiteralPath $Path).Path.TrimEnd([char[]]@('\', '/'))
    $lines = @(Get-ChildItem -LiteralPath $base -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($base.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        "$relative`t$($_.Length)`t$hash"
    })
    $payload = [Text.UTF8Encoding]::new($false).GetBytes(($lines -join "`n"))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($payload)).ToLowerInvariant()
}

function Get-RequiredDotnetRuntimeSelection(
    [string]$RuntimeRoot,
    [string[]]$RuntimeConfigPaths,
    [string]$LockedVersion) {
    $requirements = @()
    foreach ($runtimeConfigPath in $RuntimeConfigPaths) {
        if (-not (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf)) {
            throw "DOTNET RUNTIME SELECTION FAILED: runtimeconfig missing: $runtimeConfigPath"
        }
        $runtimeOptions = (Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json).runtimeOptions
        $declared = @()
        if ($runtimeOptions.PSObject.Properties.Name -contains 'framework') { $declared += $runtimeOptions.framework }
        if ($runtimeOptions.PSObject.Properties.Name -contains 'frameworks') { $declared += @($runtimeOptions.frameworks) }
        foreach ($framework in $declared) {
            if ([string]$framework.name -in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')) {
                $requirements += [pscustomobject]@{
                    Name = [string]$framework.name
                    MinimumVersion = [Version]([string]$framework.version)
                }
            }
        }
    }

    $requirements = @($requirements | Sort-Object Name -Unique)
    if ($requirements.Count -eq 0) {
        throw "DOTNET RUNTIME SELECTION FAILED: no supported shared-framework requirement found."
    }

    $commonVersions = $null
    foreach ($requirement in $requirements) {
        $frameworkRoot = Join-Path (Join-Path $RuntimeRoot 'shared') $requirement.Name
        if (-not (Test-Path -LiteralPath $frameworkRoot -PathType Container)) {
            throw "DOTNET RUNTIME SELECTION FAILED: required framework missing: $frameworkRoot"
        }
        $compatible = @(Get-ChildItem -LiteralPath $frameworkRoot -Directory | Where-Object {
            $parsed = $null
            [Version]::TryParse($_.Name, [ref]$parsed) -and
                $parsed.Major -eq $requirement.MinimumVersion.Major -and
                $parsed.Minor -eq $requirement.MinimumVersion.Minor -and
                $parsed -ge $requirement.MinimumVersion
        } | ForEach-Object Name)
        if ($compatible.Count -eq 0) {
            throw "DOTNET RUNTIME SELECTION FAILED: no compatible $($requirement.Name) >= $($requirement.MinimumVersion)."
        }
        $commonVersions = if ($null -eq $commonVersions) {
            $compatible
        } else {
            @($commonVersions | Where-Object { $compatible -contains $_ })
        }
    }

    if (@($commonVersions).Count -eq 0) {
        throw "DOTNET RUNTIME SELECTION FAILED: required frameworks have no common patch version."
    }
    if ($LockedVersion -notin @($commonVersions)) {
        throw "DOTNET RUNTIME SELECTION FAILED: locked version $LockedVersion is not a common compatible patch; available '$($commonVersions -join ', ')'."
    }
    $selectedVersion = $LockedVersion
    $fxrPath = Join-Path (Join-Path (Join-Path $RuntimeRoot 'host') 'fxr') $selectedVersion
    if (-not (Test-Path -LiteralPath $fxrPath -PathType Container)) {
        throw "DOTNET RUNTIME SELECTION FAILED: host/fxr $selectedVersion is missing."
    }

    return [pscustomobject]@{
        Version = $selectedVersion
        Frameworks = @($requirements | ForEach-Object Name)
    }
}

# --- Engine baseline: single source of truth (M2-ENG-01 Part IV / Part VI) ---
# All Engine identity (version / commit / digest) MUST derive from this file.
$BaselinePath = Join-Path $RepoRoot "engine/engine-baseline.json"
$Baseline = $null
if (Test-Path -LiteralPath $BaselinePath) {
    $Baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
}
$DotnetRuntimeLockPath = Join-Path $RepoRoot 'engine/locks/dotnet-runtime.lock.json'
if (-not (Test-Path -LiteralPath $DotnetRuntimeLockPath -PathType Leaf)) {
    throw "dotnet runtime lock not found: $DotnetRuntimeLockPath"
}
$DotnetRuntimeLock = Get-Content -LiteralPath $DotnetRuntimeLockPath -Raw | ConvertFrom-Json
if ([string]$DotnetRuntimeLock.protocol -ne 'fs-observer-dotnet-runtime-lock/1' -or
    [string]$DotnetRuntimeLock.status -ne 'FROZEN') {
    throw "dotnet runtime lock protocol/status is invalid: $DotnetRuntimeLockPath"
}
if ([string]$DotnetRuntimeLock.architecture -ne $Runtime) {
    throw "dotnet runtime lock architecture '$($DotnetRuntimeLock.architecture)' does not match publish runtime '$Runtime'."
}
$lockedFrameworks = @($DotnetRuntimeLock.frameworks | ForEach-Object { [string]$_ } | Sort-Object -Unique)
if ($lockedFrameworks.Count -ne 2 -or
    'Microsoft.NETCore.App' -notin $lockedFrameworks -or
    'Microsoft.AspNetCore.App' -notin $lockedFrameworks) {
    throw "dotnet runtime lock frameworks must be exactly Microsoft.NETCore.App and Microsoft.AspNetCore.App."
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

    # B-02 #3 + M2-FIX-03 (T9): wrap the entire build/stage/release pipeline in try/catch/finally so a
    # failure can NEVER leave a half-populated output directory or a partial release ZIP behind.
    # Cleanup happens in `finally` BEFORE any error is emitted, so the trailing Write-Error cannot
    # abort the script and leave residue behind (the previous bug left .failure-probe.staging.<hash>).
    $catchError = $null
    # M2-FIX-04: tracks whether we reached the success tail. The `finally` block uses this to decide
    # between KEEPING the release artifacts (observer/, observer.zip, external manifest) on success
    # vs. removing ALL partial output on failure (residue = 0). This replaces the unconditional
    # "delete ReleaseZip on exit" bug, which destroyed the formal distribution artifact on success.
    $publishSucceeded = $false
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

    # V030-RC-ENTRY-FIX-01 (DEFECT_1): carry the official entry launcher into the package root
    if (Test-Path -LiteralPath (Join-Path $RepoRoot "observer.cmd")) {
        Copy-Item -LiteralPath (Join-Path $RepoRoot "observer.cmd") -Destination (Join-Path $StagingRoot "observer.cmd") -Force
        Write-Host "  carried observer.cmd (official entry launcher) into staging package"
    }

    # F1: carry the discoverable one-click entry launcher (启动Observer.cmd) and README.txt into
    # the package root, and GATE the build if either is missing from the repo root. This makes the
    # Web console discoverable from the package top level (double-click -> serve -> browser opens).
    foreach ($entryFile in @("启动Observer.cmd", "README.txt")) {
        $entrySrc = Join-Path $RepoRoot $entryFile
        if (-not (Test-Path -LiteralPath $entrySrc -PathType Leaf)) {
            throw "F1 ENTRY GATE FAILED: required discoverable entry file '$entryFile' is missing from the repo root ($RepoRoot). The published package would not be discoverable. Add it before publishing."
        }
        Copy-Item -LiteralPath $entrySrc -Destination (Join-Path $StagingRoot $entryFile) -Force
        Write-Host "  carried $entryFile (discoverable entry) into staging package"
    }

    # V030-RC-ENTRY-FIX-01 (DEFECT_3): bundle the .NET runtime into <StagingRoot>/runtime/dotnet.
    # The package is framework-dependent; observer.cmd and the CLI Launcher both launch the
    # Web host via <PKG>/runtime/dotnet/dotnet.exe. A prior publish step silently dropped this
    # copy, producing a non-startable package. Copy only the newest common compatible patch used
    # by the published runtimeconfig files. Copying the entire build-machine shared/ tree makes the
    # artifact depend on unrelated installed patches and previously duplicated 10.0.9 + 10.0.10.
    if (-not [string]::IsNullOrWhiteSpace($DotnetRoot) -and (Test-Path -LiteralPath (Join-Path $DotnetRoot "dotnet.exe"))) {
        $dotnetDst = Join-Path (Join-Path $StagingRoot "runtime") "dotnet"
        if (-not (Test-Path -LiteralPath (Join-Path $dotnetDst "dotnet.exe"))) {
            $dotnetSelection = Get-RequiredDotnetRuntimeSelection -RuntimeRoot $DotnetRoot -RuntimeConfigPaths @(
                (Join-Path $StagingRoot 'FullSpectrum.Observer.Host.Cli.runtimeconfig.json'),
                (Join-Path $StagingWeb 'Observer.Host.Web.runtimeconfig.json')
            ) -LockedVersion ([string]$DotnetRuntimeLock.version)
            $selectedFrameworks = @($dotnetSelection.Frameworks | Sort-Object -Unique)
            if ($selectedFrameworks.Count -ne $lockedFrameworks.Count -or
                @($selectedFrameworks | Where-Object { $_ -notin $lockedFrameworks }).Count -ne 0) {
                throw "DOTNET RUNTIME LOCK MISMATCH: runtimeconfig frameworks '$($selectedFrameworks -join ', ')' do not match lock '$($lockedFrameworks -join ', ')'."
            }
            New-Item -ItemType Directory -Force -Path $dotnetDst | Out-Null
            Copy-Item -LiteralPath (Join-Path $DotnetRoot "dotnet.exe") -Destination $dotnetDst -Force
            Copy-Item -LiteralPath (Join-Path $DotnetRoot "LICENSE.txt") -Destination $dotnetDst -Force -ErrorAction SilentlyContinue
            Copy-Item -LiteralPath (Join-Path $DotnetRoot "ThirdPartyNotices.txt") -Destination $dotnetDst -Force -ErrorAction SilentlyContinue
            $fxrDst = Join-Path (Join-Path $dotnetDst 'host') 'fxr'
            New-Item -ItemType Directory -Force -Path $fxrDst | Out-Null
            Copy-Item -LiteralPath (Join-Path (Join-Path (Join-Path $DotnetRoot 'host') 'fxr') $dotnetSelection.Version) -Destination $fxrDst -Recurse -Force
            foreach ($frameworkName in $dotnetSelection.Frameworks) {
                $frameworkDst = Join-Path (Join-Path $dotnetDst 'shared') $frameworkName
                New-Item -ItemType Directory -Force -Path $frameworkDst | Out-Null
                $frameworkSrc = Join-Path (Join-Path (Join-Path $DotnetRoot 'shared') $frameworkName) $dotnetSelection.Version
                Copy-Item -LiteralPath $frameworkSrc -Destination $frameworkDst -Recurse -Force
            }
            $dotnetFiles = @(Get-ChildItem -LiteralPath $dotnetDst -Recurse -File)
            $dotnetBytes = [long](($dotnetFiles | Measure-Object Length -Sum).Sum)
            $dotnetTreeSha = Get-DirectoryTreeSha256 $dotnetDst
            if ($dotnetFiles.Count -ne [int]$DotnetRuntimeLock.file_count -or
                $dotnetBytes -ne [long]$DotnetRuntimeLock.total_bytes -or
                $dotnetTreeSha -ne [string]$DotnetRuntimeLock.tree_sha256) {
                throw "DOTNET RUNTIME LOCK MISMATCH: files=$($dotnetFiles.Count), bytes=$dotnetBytes, tree=$dotnetTreeSha."
            }
            Write-Host "  bundled .NET runtime $($dotnetSelection.Version) ($($dotnetSelection.Frameworks -join ', '))"
            Write-Host "  .NET runtime lock verified: $dotnetTreeSha"
        } else {
            Write-Host "  runtime/dotnet already present, skipped bundling"
        }
    } else {
        Write-Warning "DOTNET RUNTIME NOT BUNDLED: -DotnetRoot not supplied or missing dotnet.exe; release gate will assert runtime/dotnet/dotnet.exe exists."
    }

    # V030-RC-ENTRY-FIX-01 (packaging): ensure <StagingRoot>/runtime/sqlite/sqlite3.dll exists so
    # generate-release-metadata.py (which hashes it) and the .NET native SQLite provider resolve.
    # The native lib ships as e_sqlite3.dll at the staging root; mirror it into runtime/sqlite.
    $sqliteDstDir = Join-Path (Join-Path $StagingRoot "runtime") "sqlite"
    $sqliteSrc = Join-Path $StagingRoot "e_sqlite3.dll"
    if ((Test-Path -LiteralPath $sqliteSrc) -and -not (Test-Path -LiteralPath (Join-Path $sqliteDstDir "sqlite3.dll"))) {
        New-Item -ItemType Directory -Force -Path $sqliteDstDir | Out-Null
        Copy-Item -LiteralPath $sqliteSrc -Destination (Join-Path $sqliteDstDir "sqlite3.dll") -Force
        Write-Host "  mirrored e_sqlite3.dll -> runtime/sqlite/sqlite3.dll"
    }

    # M2-FIX-03 (T7a): carry the Case Pack directory so IG5 "Case Pack directory is missing" is
    # fixed — the runtime resolver derives CasePackDirectory from <PackageRoot>/packs/foundation-case005.
    Copy-Item -LiteralPath (Join-Path $RepoRoot "packs") -Destination (Join-Path $StagingRoot "packs") -Recurse -Force
    Write-Host "  carried packs/ into staging package"

    # M2-FIX-03 (T7b): provision a self-contained Python runtime (python.exe + numpy + jsonschema)
    # from a pre-built distribution + offline wheel cache. No network egress. When CI does not
    # supply -PythonSource/-WheelCache (dev build) we skip it and warn; the formal release gate
    # below asserts the runtime is present.
    if (-not [string]::IsNullOrWhiteSpace($PythonSource) -and -not [string]::IsNullOrWhiteSpace($WheelCache)) {
        & (Join-Path $PSScriptRoot "provision-runtime-python.ps1") -PythonSource $PythonSource -WheelCache $WheelCache -Destination $StagingRoot
        if ($LASTEXITCODE -ne 0) { throw "RELEASE GATE FAILED: runtime Python provisioning failed (exit $LASTEXITCODE)." }
        $runtimeExe = Join-Path $StagingRoot "runtime/python/python.exe"
        if (-not (Test-Path -LiteralPath $runtimeExe -PathType Leaf)) {
            throw "RELEASE GATE FAILED: runtime/python/python.exe missing after provisioning."
        }
        Write-Host "  provisioned self-contained runtime/python"
    } else {
        Write-Warning "DEV PUBLISH: -PythonSource/-WheelCache not supplied; runtime/python NOT provisioned (formal release requires it)."
    }

    # Runtime inventory: explain why the portable package is large and make the exact runtime set
    # independently auditable. Directory digests hash sorted relative-path/size/file-SHA rows.
    $runtimeRoot = Join-Path $StagingRoot 'runtime'
    $inventoryPath = Join-Path $runtimeRoot 'RUNTIME-INVENTORY.md'
    $dotnetRuntimeRoot = Join-Path $runtimeRoot 'dotnet'
    $pythonRuntimeRoot = Join-Path $runtimeRoot 'python'
    $sqliteRuntimePath = Join-Path $runtimeRoot 'sqlite/sqlite3.dll'
    $pythonLock = Get-Content -LiteralPath (Join-Path $RepoRoot 'engine/locks/python-runtime.lock.json') -Raw | ConvertFrom-Json
    $dotnetFiles = if (Test-Path -LiteralPath $dotnetRuntimeRoot) { @(Get-ChildItem -LiteralPath $dotnetRuntimeRoot -Recurse -File) } else { @() }
    $pythonFiles = if (Test-Path -LiteralPath $pythonRuntimeRoot) { @(Get-ChildItem -LiteralPath $pythonRuntimeRoot -Recurse -File) } else { @() }
    $sqliteSha = if (Test-Path -LiteralPath $sqliteRuntimePath) { (Get-FileHash -Algorithm SHA256 -LiteralPath $sqliteRuntimePath).Hash.ToLowerInvariant() } else { 'NOT_AVAILABLE' }
    $numpyLock = @($pythonLock.dependencies | Where-Object name -eq 'numpy')[0]
    $openBlas = @($pythonFiles | Where-Object Name -Like 'libopenblas*.dll' | Select-Object -First 1)
    $openBlasLine = if ($openBlas.Count -eq 1) {
        "| OpenBLAS | NumPy $($numpyLock.version) 随附原生库 | $($openBlas[0].Name) | $((Get-FileHash -Algorithm SHA256 -LiteralPath $openBlas[0].FullName).Hash.ToLowerInvariant()) | NumPy wheel 内置；许可证随 NumPy 元数据提供 |"
    } else {
        '| OpenBLAS | NumPy 随附原生库 | NOT_AVAILABLE | NOT_AVAILABLE | 未在运行时中发现 |'
    }
    $dotnetVersion = if ($null -ne $dotnetSelection) { [string]$dotnetSelection.Version } else { 'NOT_BUNDLED' }
    $dotnetDigest = if ($dotnetFiles.Count -gt 0) { Get-DirectoryTreeSha256 $dotnetRuntimeRoot } else { 'NOT_AVAILABLE' }
    $inventory = @(
        '# Observer Runtime Inventory',
        '',
        '> 本文件由正式制包脚本生成，用于解释便携式发布包的运行时构成。它是审计清单，不是生产就绪声明。',
        '',
        "生成时间（UTC）：$([DateTime]::UtcNow.ToString('O'))",
        '',
        '| 组件 | 版本/范围 | 用途 | SHA-256 / 树摘要 | 来源与许可证 |',
        '|---|---|---|---|---|',
        "| .NET Host + Microsoft.NETCore.App + Microsoft.AspNetCore.App | $dotnetVersion | 启动 CLI 与本地 Web 控制台 | $dotnetDigest | 制包输入 -DotnetRoot；runtime/dotnet/LICENSE.txt 与 ThirdPartyNotices.txt |",
        "| CPython | $($pythonLock.version) x64 | 运行固定 Engine worker | $($pythonLock.runtime_tree_manifest_sha256) | $($pythonLock.distribution.source_url)；runtime/python/LICENSE.txt（若上游分发包含） |",
        "| pip | $($pythonLock.pip.version) | 离线依赖元数据与运行支持 | $($pythonLock.pip.sha256) | $($pythonLock.pip.source_url)；wheel 元数据 |",
        "| NumPy | $($numpyLock.version) | Engine 数值运行依赖 | $($numpyLock.sha256) | 固定 wheel；runtime/python/Lib/site-packages/numpy-*.dist-info |",
        $openBlasLine,
        "| SQLite native | 随 Microsoft.Data.Sqlite 运行负载 | 本地证据存储 | $sqliteSha | e_sqlite3.dll 的发布负载；需结合依赖许可证清单审查 |",
        '',
        '## 体积',
        '',
        "- .NET runtime：$([long](($dotnetFiles | Measure-Object Length -Sum).Sum)) 字节，$($dotnetFiles.Count) 个文件。",
        "- Python runtime：$([long](($pythonFiles | Measure-Object Length -Sum).Sum)) 字节，$($pythonFiles.Count) 个文件。",
        '',
        '## .NET 选择规则',
        '',
        "CLI 与 Web 的 runtimeconfig 共同解析到补丁版本 $dotnetVersion。发布包只携带这一共同兼容版本及同版本 host/fxr，不得复制构建机上的其它已安装补丁。",
        '',
        '## Python 锁定来源',
        '',
        '`engine/locks/python-runtime.lock.json` 与 `engine/locks/runtime-manifest.json` 是 Python 版本、wheel SHA 和最终文件树的权威锁定来源。'
    )
    Set-Content -LiteralPath $inventoryPath -Value ($inventory -join "`n") -Encoding utf8
    Write-Host "  runtime inventory -> $inventoryPath"

    # M2-FIX-03 (T7c): ensure CLI + Web appsettings.json ship with an empty EngineV15.PythonExecutablePath
    # (resolved at runtime by RuntimeConfigurationResolver; the value here is a placeholder only).
    $cfgCli = Join-Path $RepoRoot "src/Observer.Host.Cli/appsettings.json"
    $cfgWeb = Join-Path $RepoRoot "src/Observer.Host.Web/appsettings.json"
    if (Test-Path -LiteralPath $cfgCli) { Copy-Item -LiteralPath $cfgCli -Destination (Join-Path $StagingCli "appsettings.json") -Force }
    if (Test-Path -LiteralPath $cfgWeb) { Copy-Item -LiteralPath $cfgWeb -Destination (Join-Path $StagingWeb "appsettings.json") -Force }

    # M2-FIX-03 (T7d): the formal release must be self-contained — assert the runtime interpreter
    # exists before promotion (only enforced when a runtime was actually provisioned above).
    if (-not [string]::IsNullOrWhiteSpace($PythonSource) -and -not [string]::IsNullOrWhiteSpace($WheelCache)) {
        $finalRuntimeExe = Join-Path $StagingRoot "runtime/python/python.exe"
        if (-not (Test-Path -LiteralPath $finalRuntimeExe -PathType Leaf)) {
            throw "RELEASE GATE FAILED: runtime/python/python.exe absent at promotion; package would not be self-contained."
        }
    }

    # F6 (release mode): author the package-root release-identity.json (NO self SHA) and the
    # in-package web/release-manifest.json (placeholder SHA) BEFORE the ZIP so they are included in
    # the candidate package. This is the authoritative pipeline contract:
    #   ZIP_ROOT_RELEASE_IDENTITY = YES   (release-identity.json at package root, no package SHA)
    #   ZIP_WEB_RELEASE_MANIFEST  = YES   (release-manifest.json under web/)
    #   ZIP_ROOT_RELEASE_MANIFEST = NO    (release-manifest.json NOT at package root)
    #   ZIP_SELF_SHA              = FORBIDDEN (in-package manifest SHA = placeholder)
    #   EXTERNAL_IDENTITY_WITH_FULL_ZIP_SHA = YES (external <candidate>_IDENTITY.json carries it)
    if ($Release.IsPresent) {
        $observerVersion = "v0.3.0-maintenance-candidate"
        $observerCommit = ""
        try { $observerCommit = (git -C $RepoRoot rev-parse HEAD 2>$null).Trim() } catch { }
        if ([string]::IsNullOrWhiteSpace($observerCommit)) { $observerCommit = "UNPUBLISHED" }

        $releaseIdentity = [ordered]@{
            version = $observerVersion
            commit = $observerCommit
            channel = $resolvedChannel
            status = "NOT_RELEASED"
            engine_version = if ($baselineVersion -ne "") { $baselineVersion } else { "v1.5.0" }
            engine_commit = $baselineCommit
        }
        Set-Content -LiteralPath (Join-Path $StagingRoot "release-identity.json") -Value ($releaseIdentity | ConvertTo-Json -Compress) -Encoding utf8
        Write-Host "  release-identity.json (root, no self SHA) -> $(Join-Path $StagingRoot 'release-identity.json')"

        $inPkgManifest = [ordered]@{
            engine_source_artifact_sha256 = $engineSourceDigest
            engine_runtime_payload_manifest_sha256 = $runtimePayloadDigest
            observer_release_package_sha256 = "SEE_EXTERNAL_IDENTITY_FILE"
            artifact_digest = "SEE_EXTERNAL_IDENTITY_FILE"
            build_channel = $resolvedChannel
            engine_tag = if ($Baseline -and $Baseline.PSObject.Properties.Name -contains 'engine_tag') { $Baseline.engine_tag } else { "v1.5.0" }
            engine_version = if ($baselineVersion -ne "") { $baselineVersion } else { "v1.5.0" }
            engine_commit = $baselineCommit
            generated_at = ([System.DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))
        }
        Set-Content -LiteralPath (Join-Path $StagingWeb "release-manifest.json") -Value ($inPkgManifest | ConvertTo-Json -Compress) -Encoding utf8
        Write-Host "  web/release-manifest.json (placeholder SHA) -> $(Join-Path $StagingWeb 'release-manifest.json')"
    }

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
                # F6: the in-package web/release-manifest.json (placeholder SHA) IS shipped inside the
                # ZIP (ZIP_WEB_RELEASE_MANIFEST=YES); the package-root release-identity.json (no self
                # SHA) is also shipped. The external <candidate>_IDENTITY.json (full ZIP SHA) is written
                # OUTSIDE the ZIP after hashing, so it is naturally excluded here.
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

    # B-02 #5: generate a REAL release-manifest.json from computed values (no hardcoded placeholders)
    # for the EXTERNAL distribution signature (written outside the ZIP). In release mode the
    # in-package web/release-manifest.json already carries the placeholder SHA (written pre-ZIP),
    # so we must NOT overwrite it with the real-SHA manifest here (ZIP_SELF_SHA=FORBIDDEN).
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

    # In release mode the in-package web/release-manifest.json (placeholder SHA) is already in
    # staging; do NOT overwrite it. Only dev mode writes the manifest into the staging package.
    if (-not $Release.IsPresent) {
        $manifestWeb = Join-Path $StagingWeb "release-manifest.json"
        $manifestCli = Join-Path $StagingCli "release-manifest.json"
        Set-Content -LiteralPath $manifestWeb -Value $manifestJson -Encoding utf8
        Set-Content -LiteralPath $manifestCli -Value $manifestJson -Encoding utf8
        Write-Host "  release-manifest.json -> $manifestWeb"
        Write-Host "  release-manifest.json -> $manifestCli"
    }

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

    # F1 GATE: the published package root must contain the discoverable entry files.
    foreach ($entryFile in @("启动Observer.cmd", "README.txt")) {
        if (-not (Test-Path -LiteralPath (Join-Path $OutputRoot $entryFile) -PathType Leaf)) {
            throw "F1 ENTRY GATE FAILED: published package root missing required entry file '$entryFile'."
        }
    }

    # M2-FIX-04 + F6: For a formal Release, write the EXTERNAL distribution files alongside
    # observer.zip. The in-package web/release-manifest.json (placeholder SHA) and package-root
    # release-identity.json (no self SHA) are already inside the ZIP. Here we write, OUTSIDE the ZIP:
    #   * release-manifest.json        (EXTERNAL_RELEASE_MANIFEST=YES; carries real package SHA)
    #   * <candidate>_IDENTITY.json    (EXTERNAL_IDENTITY_WITH_FULL_ZIP_SHA=YES; package_sha256 = full ZIP SHA)
    # Then run the F6 release-gate assertions. Dev (non -Release) builds skip this.
    if ($Release.IsPresent) {
        $externalManifestPath = Join-Path $OutputParent "release-manifest.json"
        Set-Content -LiteralPath $externalManifestPath -Value $manifestJson -Encoding utf8
        Write-Host "  release-manifest.json (external) -> $externalManifestPath"

        # F6: package-EXTERNAL identity file carrying the FULL ZIP SHA-256 (pairwise with the ZIP).
        $externalIdentityPath = Join-Path $OutputParent ($OutputName + "_IDENTITY.json")
        $extIdentity = [ordered]@{
            version = if ($observerVersion) { $observerVersion } else { "v0.3.0-maintenance-candidate" }
            commit = if ($observerCommit) { $observerCommit } else { "UNPUBLISHED" }
            channel = $resolvedChannel
            status = "NOT_RELEASED"
            engine_version = if ($baselineVersion -ne "") { $baselineVersion } else { "v1.5.0" }
            engine_commit = $baselineCommit
            package_sha256 = $packageSha
        }
        Set-Content -LiteralPath $externalIdentityPath -Value ($extIdentity | ConvertTo-Json -Compress) -Encoding utf8
        Write-Host "  external identity (full ZIP SHA) -> $externalIdentityPath"

        # --- F6 RELEASE GATE (authoritative pipeline contract assertions) ---
        # G1: package root has release-identity.json with NO package self SHA.
        $rootIdentity = Join-Path $OutputRoot "release-identity.json"
        if (-not (Test-Path -LiteralPath $rootIdentity -PathType Leaf)) {
            throw "F6 REJECT: package root missing release-identity.json (ZIP_ROOT_RELEASE_IDENTITY=YES violated)."
        }
        $rootIdentityJson = Get-Content -LiteralPath $rootIdentity -Raw | ConvertFrom-Json
        if ($rootIdentityJson.PSObject.Properties.Name -contains 'package_sha256' -or $rootIdentityJson.PSObject.Properties.Name -contains 'observer_package_sha256') {
            throw "F6 REJECT: package-root release-identity.json must NOT contain a package SHA (ZIP_SELF_SHA=FORBIDDEN)."
        }

        # G2: web/release-manifest.json exists and uses the placeholder SHA (not self SHA).
        $webManifest = Join-Path $OutputRoot "web/release-manifest.json"
        if (-not (Test-Path -LiteralPath $webManifest -PathType Leaf)) {
            throw "F6 REJECT: web/release-manifest.json missing (ZIP_WEB_RELEASE_MANIFEST=YES violated)."
        }
        $webManifestJson = Get-Content -LiteralPath $webManifest -Raw | ConvertFrom-Json
        $webSha = if ($webManifestJson.PSObject.Properties.Name -contains 'observer_release_package_sha256') { [string]$webManifestJson.observer_release_package_sha256 } else { "" }
        if ($webSha -ne "SEE_EXTERNAL_IDENTITY_FILE") {
            throw "F6 REJECT: web/release-manifest.json observer_release_package_sha256 must be 'SEE_EXTERNAL_IDENTITY_FILE', got '$webSha' (ZIP_SELF_SHA=FORBIDDEN)."
        }

        # G3: external identity package_sha256 == sha256 of the ZIP.
        if (-not (Test-Path -LiteralPath $externalIdentityPath -PathType Leaf)) {
            throw "F6 REJECT: external identity file missing (EXTERNAL_IDENTITY_WITH_FULL_ZIP_SHA=YES violated)."
        }
        $extIdJson = Get-Content -LiteralPath $externalIdentityPath -Raw | ConvertFrom-Json
        $extSha = if ($extIdJson.PSObject.Properties.Name -contains 'package_sha256') { [string]$extIdJson.package_sha256 } else { "" }
        $zipSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $ReleaseZip).Hash.ToLowerInvariant()
        if ($extSha.ToLowerInvariant() -ne $zipSha) {
            throw "F6 REJECT: external identity package_sha256 '$extSha' != sha256($ReleaseZip) '$zipSha'."
        }

        # G4: release-manifest.json must NOT be at the package root (ZIP_ROOT_RELEASE_MANIFEST=NO).
        if (Test-Path -LiteralPath (Join-Path $OutputRoot "release-manifest.json") -PathType Leaf) {
            throw "F6 REJECT: release-manifest.json present at package root (ZIP_ROOT_RELEASE_MANIFEST=NO violated)."
        }

        # G5: portable runtime inventory is present and the .NET payload contains exactly the
        # selected common patch for hostfxr + both required shared frameworks. This prevents the
        # build machine's unrelated installed patches from leaking into the release artifact.
        $publishedInventory = Join-Path $OutputRoot 'runtime/RUNTIME-INVENTORY.md'
        if (-not (Test-Path -LiteralPath $publishedInventory -PathType Leaf)) {
            throw "F6 REJECT: runtime/RUNTIME-INVENTORY.md missing."
        }
        $publishedVersionSets = @(
            (Join-Path $OutputRoot 'runtime/dotnet/host/fxr'),
            (Join-Path $OutputRoot 'runtime/dotnet/shared/Microsoft.NETCore.App'),
            (Join-Path $OutputRoot 'runtime/dotnet/shared/Microsoft.AspNetCore.App')
        )
        foreach ($versionRoot in $publishedVersionSets) {
            $versions = @(Get-ChildItem -LiteralPath $versionRoot -Directory | ForEach-Object Name)
            if ($versions.Count -ne 1 -or $versions[0] -ne $dotnetSelection.Version) {
                throw "F6 REJECT: $versionRoot must contain only .NET $($dotnetSelection.Version); found '$($versions -join ', ')'."
            }
        }

        Write-Host "  F6 RELEASE GATE PASSED."
    }

    Write-Host "=== Publish package complete ==="
    Write-Host "  CLI    : $cliExe"
    Write-Host "  Web    : $webExe"
    Write-Host "  Native : $nativeCli"
    Write-Host "  Native : $nativeWeb"
    Write-Host "  Digest : $resolvedDigest ($resolvedChannel)"
    if ($Release.IsPresent) { Write-Host "  Release ZIP: $ReleaseZip (sha256 $packageSha)" }
    Write-Host "=== [publish-observer] OK ==="
    # B-02 #7: RC=0 on success.
    $publishSucceeded = $true
    exit 0
}
catch {
    # M2-FIX-03 (T9): capture the error but do NOT emit it yet. Cleanup is performed in `finally`
    # so it runs even if the trailing Write-Error re-throws under $ErrorActionPreference="Stop".
    $catchError = $_.Exception.Message
}
finally {
    # M2-FIX-04: SUCCESS vs FAILURE cleanup.
    #   * SUCCESS: keep observer/ (promoted from staging), observer.zip, and the external
    #     release-manifest.json. Only discard the transient staging directory (already renamed to the
    #     final output via Move-Item, so this is normally a no-op).
    #   * FAILURE: guarantee residue = 0 — remove the staging directory, the partial release ZIP, and
    #     any external release-manifest.json so a zero/partial digest is never shipped. This preserves
    #     the existing failure-cleanup (C8) semantics: no half-populated output directory,
    #     no partial release ZIP, non-zero exit.
    if ($publishSucceeded) {
        if (Test-Path -LiteralPath $StagingRoot) {
            [System.IO.Directory]::Delete($StagingRoot, $true)
        }
    } else {
        if (Test-Path -LiteralPath $StagingRoot) {
            [System.IO.Directory]::Delete($StagingRoot, $true)
        }
        if (Test-Path -LiteralPath $ReleaseZip) {
            [System.IO.File]::Delete($ReleaseZip)
        }
        $externalManifestPath = Join-Path $OutputParent "release-manifest.json"
        if (Test-Path -LiteralPath $externalManifestPath) {
            [System.IO.File]::Delete($externalManifestPath)
        }
    }
}

# M2-FIX-03 (T9): ONLY AFTER cleanup is guaranteed do we emit the error and exit non-zero. A failed
# run must leave NO half-populated output directory and NO partial release ZIP (residue = 0).
if ($null -ne $catchError) {
    Write-Error "RELEASE BUILD FAILED: $catchError"
    Write-Error "Partial staging and partial release ZIP removed. No half-populated output directory was left behind."
    # B-02 #7: non-zero on failure.
    exit 1
}

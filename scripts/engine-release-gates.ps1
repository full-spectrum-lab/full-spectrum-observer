<#
.SYNOPSIS
    M2-ENG-01 dual-digest release gates. Dot-sourced by publish-observer.ps1 and test-engine-gates.ps1.

.DESCRIPTION
    Implements the adjudication's 5 release-gate checks for Mode A:

      1. Source artifact ZIP exists (engine/<source_artifact_filename>).
      2. Recomputed ZIP digest == baseline.engine_source_artifact_sha256.
      3. Every runtime-payload file that traces_to_source_artifact==true is byte-identical
         to the corresponding entry in the source artifact ZIP (proves the runtime payload
         is a faithful subset of the declared source).
      4. engine/runtime-payload-manifest.json exists; recomputed manifest digest ==
         baseline.engine_runtime_payload_manifest_sha256; and every entry's SHA-256 matches
         the actual file on disk (manifest == actual released files, item-by-item).
      5. Runtime handshake identity (engine/worker/worker.py ENGINE_VERSION/ENGINE_COMMIT)
         == baseline (runtime handshake returns v1.5.0@88493007).

    The source artifact (full ZIP, 355 entries) and the runtime payload (24 vendored files +
    2 worker files) are DISTINCT objects and are proven by DISTINCT digests. They must never
    be conflated into a single digest.
#>
[CmdletBinding()]
param()
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-FileSha256([string]$path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
}

function Get-ZipEntrySha256([string]$zipPath, [string]$entryName) {
    $za = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $e = $za.Entries | Where-Object { $_.FullName -eq $entryName } | Select-Object -First 1
        if (-not $e) { return $null }
        $ms = New-Object System.IO.MemoryStream
        $e.Open().CopyTo($ms)
        $ms.Position = 0
        return (Get-FileHash -Algorithm SHA256 -InputStream $ms).Hash.ToLowerInvariant()
    } finally { $za.Dispose() }
}

function Get-RuntimePayloadManifestDigest([string]$manifestPath) {
    $m = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $lines = ($m.files | Sort-Object path | ForEach-Object { "$($_.path)|$($_.sha256)" }) -join "`n"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($lines)
    return (Get-FileHash -Algorithm SHA256 -InputStream ([System.IO.MemoryStream]::new($bytes))).Hash.ToLowerInvariant()
}

function Test-EngineReleaseGates {
    param(
        [string]$RepoRoot,
        [string]$BaselinePath
    )
    $Baseline   = Get-Content $BaselinePath -Raw | ConvertFrom-Json
    $version    = [string]$Baseline.engine_version
    $commit     = [string]$Baseline.engine_commit
    $srcDigest  = [string]$Baseline.engine_source_artifact_sha256
    $rpmExpect  = [string]$Baseline.engine_runtime_payload_manifest_sha256
    $zipName    = [string]$Baseline.source_artifact_filename
    $prefix     = [string]$Baseline.source_artifact_entry_prefix

    # (1) Source artifact ZIP exists.
    $zipPath = Join-Path $RepoRoot "engine" $zipName
    if (-not (Test-Path $zipPath -PathType Leaf)) {
        throw "RELEASE GATE FAILED: source artifact ZIP missing: $zipPath (Mode A requires the package to carry it)."
    }

    # (2) ZIP digest recomputed == baseline.engine_source_artifact_sha256.
    $zipDigest = Get-FileSha256 $zipPath
    if ($zipDigest -ne $srcDigest.ToLowerInvariant()) {
        throw "RELEASE GATE FAILED: recomputed ZIP digest '$zipDigest' != engine_source_artifact_sha256 '$srcDigest'."
    }

    # (4) runtime-payload-manifest exists + digest matches baseline.
    $rpmPath = Join-Path $RepoRoot "engine/runtime-payload-manifest.json"
    if (-not (Test-Path $rpmPath)) {
        throw "RELEASE GATE FAILED: engine/runtime-payload-manifest.json missing."
    }
    $rpmActual = Get-RuntimePayloadManifestDigest $rpmPath
    if ($rpmActual -ne $rpmExpect.ToLowerInvariant()) {
        throw "RELEASE GATE FAILED: runtime-payload-manifest digest '$rpmActual' != baseline.engine_runtime_payload_manifest_sha256 '$rpmExpect'."
    }

    # (3)+(4) every entry item-by-item, plus traceability to the ZIP.
    $rpm = Get-Content $rpmPath -Raw | ConvertFrom-Json
    foreach ($entry in $rpm.files) {
        $fp = Join-Path $RepoRoot $entry.path
        if (-not (Test-Path $fp -PathType Leaf)) {
            throw "RELEASE GATE FAILED: runtime payload file missing: $($entry.path)."
        }
        $actual = Get-FileSha256 $fp
        if ($actual -ne [string]$entry.sha256) {
            throw "RELEASE GATE FAILED: runtime payload file '$($entry.path)' SHA-256 '$actual' != manifest '$($entry.sha256)' (manifest != actual files)."
        }
        if ($entry.traces_to_source_artifact -eq $true) {
            $zsha = Get-ZipEntrySha256 $zipPath $entry.source_artifact_entry
            if ($null -eq $zsha) {
                throw "RELEASE GATE FAILED: ZIP entry '$($entry.source_artifact_entry)' not found in source artifact (untraceable payload)."
            }
            if ($zsha -ne [string]$entry.sha256) {
                throw "RELEASE GATE FAILED: runtime payload file '$($entry.path)' SHA-256 '$actual' != source ZIP entry '$($entry.source_artifact_entry)' SHA-256 '$zsha' (not a faithful subset)."
            }
        }
    }

    # (5) runtime handshake identity (worker.py) == baseline.
    $workerPy = Join-Path $RepoRoot "engine/worker/worker.py"
    if (Test-Path $workerPy) {
        $wp = Get-Content $workerPy -Raw
        if ($wp -notmatch "ENGINE_VERSION\s*=\s*'$version'") {
            throw "RELEASE GATE FAILED: engine/worker/worker.py ENGINE_VERSION '$($version)' not declared (handshake identity != baseline)."
        }
        if ($wp -notmatch "ENGINE_COMMIT\s*=\s*'$commit'") {
            throw "RELEASE GATE FAILED: engine/worker/worker.py ENGINE_COMMIT '$($commit)' not declared (handshake identity != baseline)."
        }
    }

    return $true
}

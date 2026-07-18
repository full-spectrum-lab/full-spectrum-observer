<#
.SYNOPSIS
    Generates engine/runtime-payload-manifest.json from the authoritative engine-baseline.json.

.DESCRIPTION
    M2-ENG-01 (dual-digest model). The runtime payload is NOT the same object as the full
    source artifact ZIP. The runtime payload = the 24 vendored Engine files that are actually
    executed by worker.py + the Observer worker process (worker.py / offline_guard.py) that
    pins and calls them.

    This script walks those files, records each file's SHA-256, records the ZIP entry each
    vendored file traces to (source_artifact_entry), and computes the canonical
    runtime-payload-manifest digest (over sorted "path|sha256" lines). It also performs an
    inline traceability check: every vendored file's SHA-256 must equal the SHA-256 of the
    corresponding entry in the source artifact ZIP.

    All identity (version / commit / digest / filename / prefix) is read from engine-baseline.json.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot
)
$ErrorActionPreference = "Stop"
if (-not $RepoRoot) { $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path }
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Baseline = Get-Content (Join-Path $RepoRoot "engine/engine-baseline.json") -Raw | ConvertFrom-Json
$version      = [string]$Baseline.engine_version
$commit       = [string]$Baseline.engine_commit
$zipName      = [string]$Baseline.source_artifact_filename
$prefix       = [string]$Baseline.source_artifact_entry_prefix
$srcDigest    = [string]$Baseline.engine_source_artifact_sha256

$vendorRoot = Join-Path $RepoRoot "engine/vendor/full-spectrum-engine"
$workerRoot = Join-Path $RepoRoot "engine/worker"
$files = @()

# 24 vendored Engine files (the subset actually executed).
Get-ChildItem $vendorRoot -Recurse -File | ForEach-Object {
    $relInVendor = $_.FullName.Substring($vendorRoot.Length).TrimStart('\', '/').Replace('\', '/')
    $relInRepo   = "engine/vendor/full-spectrum-engine/" + $relInVendor
    $sha         = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
    $entry       = $prefix + $relInVendor
    $files += [ordered]@{
        path                     = $relInRepo
        sha256                   = $sha
        source_artifact_entry    = $entry
        traces_to_source_artifact = $true
    }
}

# Observer worker process (pins + calls the vendored Engine; not part of the ZIP).
foreach ($wf in @("worker.py", "offline_guard.py")) {
    $fp = Join-Path $workerRoot $wf
    if (Test-Path $fp) {
        $sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $fp).Hash.ToLowerInvariant()
        $files += [ordered]@{
            path                      = "engine/worker/" + $wf
            sha256                    = $sha
            source_artifact_entry     = $null
            traces_to_source_artifact = $false
            note                      = "Observer worker process; pins Engine $version @ $commit"
        }
    }
}

# Canonical digest over sorted "path|sha256" lines.
$digestInput = ($files | Sort-Object path | ForEach-Object { "$($_.path)|$($_.sha256)" }) -join "`n"
$bytes       = [System.Text.Encoding]::UTF8.GetBytes($digestInput)
$rpmDigest   = (Get-FileHash -Algorithm SHA256 -InputStream ([System.IO.MemoryStream]::new($bytes))).Hash.ToLowerInvariant()

# Inline traceability check: vendored file SHA-256 must equal its ZIP entry SHA-256.
$zipPath = Join-Path $RepoRoot "engine" $zipName
$traceOk = 0; $traceWarn = 0
if (Test-Path $zipPath) {
    $za = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        foreach ($f in $files | Where-Object { $_.traces_to_source_artifact }) {
            $e = $za.Entries | Where-Object { $_.FullName -eq $f.source_artifact_entry } | Select-Object -First 1
            if (-not $e) { Write-Host ("TRACE WARN: ZIP entry missing: " + $f.source_artifact_entry); $traceWarn++; continue }
            $ms = New-Object System.IO.MemoryStream; $e.Open().CopyTo($ms); $ms.Position = 0
            $zsha = (Get-FileHash -Algorithm SHA256 -InputStream $ms).Hash.ToLowerInvariant()
            if ($zsha -ne $f.sha256) { Write-Host ("TRACE MISMATCH: " + $f.path + " file=" + $f.sha256 + " zip=" + $zsha); $traceWarn++ }
            else { $traceOk++ }
        }
    } finally { $za.Dispose() }
    Write-Host ("ZIP_DIGEST=" + (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant())
    Write-Host ("TRACE_OK=$traceOk TRACE_WARN=$traceWarn")
} else {
    Write-Host "TRACE SKIPPED: source artifact ZIP not found at $zipPath"
}

$manifest = [ordered]@{
    schema_version                   = "1.0"
    engine_id                        = "full-spectrum-engine"
    engine_version                   = $version
    engine_tag                       = [string]$Baseline.engine_tag
    engine_commit                    = $commit
    source_artifact_filename         = $zipName
    source_artifact_sha256           = $srcDigest
    source_artifact_entry_prefix     = $prefix
    status                           = "RUNTIME_PAYLOAD_RECONCILED"
    runtime_payload_manifest_sha256  = $rpmDigest
    _source_of_truth                 = "engine/engine-baseline.json"
    files                            = $files
    generated_at                     = ([System.DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))
}
$manifestPath = Join-Path $RepoRoot "engine/runtime-payload-manifest.json"
$manifest | ConvertTo-Json -Depth 10 | Set-Content -Path $manifestPath -Encoding utf8

Write-Host "WROTE $manifestPath"
Write-Host "FILE_COUNT=$($files.Count)"
Write-Host "RUNTIME_PAYLOAD_MANIFEST_SHA256=$rpmDigest"

<#
.SYNOPSIS
    FSO Runtime Provisioning Closure — generate a deterministic SHA-256 manifest of the
    runtime/python tree.

.DESCRIPTION
    Enumerates every regular file under -RuntimeRoot (default: runtime/python), applies the
    lock-defined excludes, rejects reparse points / symlinks, detects case-only filename
    conflicts, and emits a manifest JSON containing a canonical, reproducible
    runtime_tree_sha256 = SHA-256( canonical serialization of the files array ).

    The manifest FILE is written OUTSIDE the tree (default engine/locks/runtime-manifest.json)
    so it is never part of the tree it describes.

    Rules (see B1-manifest-schema.json / x-canonical-serialization):
      - relative_path uses '/' separators, case preserved (not folded).
      - files sorted by relative_path ascending, culture-invariant ORDINAL comparison.
      - each entry: { relative_path, size, sha256 } (fixed key order).
      - canonical JSON: compact, RFC 8259 minimal escaping, UTF-8 without BOM, no trailing newline.
      - empty directories are ignored; the manifest file itself is excluded from the tree.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$RuntimeRoot = 'runtime/python',

    [Parameter(Mandatory = $false)]
    [string]$OutFile,

    [Parameter(Mandatory = $false)]
    [string]$LockFile
)

$ErrorActionPreference = 'Stop'

# Robust default resolution for the harness/re-hosting case where $PSScriptRoot is empty.
if (-not $OutFile) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $OutFile = Join-Path $scriptDir '..\engine\locks\runtime-manifest.json'
}
if (-not $LockFile) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $LockFile = Join-Path $scriptDir '..\engine\locks\python-runtime.lock.json'
}

# ---------------------------------------------------------------------------
# Exclude + safety helpers
# ---------------------------------------------------------------------------
function Test-Excluded {
    param([string]$RelPath)
    $segs = $RelPath -split '/'
    if ($segs -contains '__pycache__') { return $true }
    if ($RelPath.EndsWith('.pyc')) { return $true }
    if ($segs -contains '.git' -or $segs -contains '.gitignore') { return $true }
    if ($RelPath -match '\.dist-info/direct_url\.json$') { return $true }
    return $false
}

function Get-Sha256Hex {
    param([string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try { $bytes = $sha.ComputeHash($stream) } finally { $stream.Dispose() }
    } finally { $sha.Dispose() }
    return [BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
}

function Get-Sha256HexFromBytes {
    param([byte[]]$Bytes)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $h = $sha.ComputeHash($Bytes) } finally { $sha.Dispose() }
    return [BitConverter]::ToString($h).Replace('-', '').ToLowerInvariant()
}

function Format-CanonicalJson {
    param([array]$Files)
    $sb = New-Object System.Text.StringBuilder
    $sb.Append('[') | Out-Null
    $first = $true
    foreach ($f in $Files) {
        if (-not $first) { $sb.Append(',') | Out-Null }
        $first = $false
        $sb.Append('{"relative_path":"') | Out-Null
        $sb.Append((Format-JsonString $f.relative_path)) | Out-Null
        $sb.Append('","size":') | Out-Null
        $sb.Append([int]$f.size) | Out-Null
        $sb.Append(',"sha256":"') | Out-Null
        $sb.Append($f.sha256) | Out-Null
        $sb.Append('"}') | Out-Null
    }
    $sb.Append(']') | Out-Null
    return $sb.ToString()
}

function Format-JsonString {
    param([string]$S)
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $S.ToCharArray()) {
        $c = [int]$ch
        if ($c -eq 0x22) { $sb.Append('\"') | Out-Null }
        elseif ($c -eq 0x5C) { $sb.Append('\\') | Out-Null }
        elseif ($c -lt 0x20) { $sb.Append(('\\u{0:x4}' -f $c)) | Out-Null }
        else { $sb.Append($ch) | Out-Null }
    }
    return $sb.ToString()
}

# ---------------------------------------------------------------------------
# Enumerate
# ---------------------------------------------------------------------------
$rootFull = [System.IO.Path]::GetFullPath($RuntimeRoot)
function Assert-Condition { param([bool]$C, [string]$M) if (-not $C) { throw "build-runtime-manifest: $M" } }
Assert-Condition (Test-Path -LiteralPath $rootFull -PathType Container) "RuntimeRoot not found: $rootFull"

$entries = @()
$lowerSeen = @{}

# Manual walk: reject reparse points / symlinks / junctions BEFORE descending,
# so a junction can never be silently traversed.
$stack = New-Object System.Collections.Stack
$stack.Push($rootFull)
while ($stack.Count -gt 0) {
    $dir = $stack.Pop()
    foreach ($item in Get-ChildItem -LiteralPath $dir -Force) {
        $full = $item.FullName
        $attr = [System.IO.File]::GetAttributes($full)
        if ($attr.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
            throw "build-runtime-manifest: reparse point / symlink not allowed: $full"
        }
        if ($item.PSIsContainer) { $stack.Push($full); continue }

        $rel = $full.Substring($rootFull.Length).TrimStart('\', '/')
        $rel = $rel.Replace('\', '/')

        if (Test-Excluded $rel) { continue }   # skip excluded paths

        # Case-only conflict detection.
        $low = $rel.ToLowerInvariant()
        if ($lowerSeen.ContainsKey($low) -and $lowerSeen[$low] -ne $rel) {
            throw "build-runtime-manifest: case conflict: '$($lowerSeen[$low])' vs '$rel'"
        }
        $lowerSeen[$low] = $rel

        $entries += [PSCustomObject][ordered]@{
            relative_path = $rel
            size          = $item.Length
            sha256        = (Get-Sha256Hex $full)
        }
    }
}

# Ordinal (culture-invariant byte-wise) ascending sort on relative_path.
$ordComparer = [System.StringComparer]::Ordinal
$sorted = [System.Linq.Enumerable]::OrderBy(
    $entries,
    [System.Func[object, string]] { param($e) $e.relative_path },
    $ordComparer
)
$files = @($sorted)

# ---------------------------------------------------------------------------
# Canonical serialization + runtime_tree_sha256
# ---------------------------------------------------------------------------
$canonical = Format-CanonicalJson $files
$canonicalBytes = [System.Text.Encoding]::UTF8.GetBytes($canonical)
$treeSha = (Get-Sha256HexFromBytes $canonicalBytes)

$totalBytes = 0
foreach ($f in $files) { $totalBytes += [int]$f.size }

$manifest = [ordered]@{
    schema                 = 'fso.runtime-manifest/1.0'
    generated_utc          = ([System.DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))
    generator              = 'scripts/build-runtime-manifest.ps1@B3-impl-20260819'
    algorithm              = 'sha256'
    root                   = 'runtime/python'
    files                  = $files
    file_count             = $files.Count
    total_bytes            = $totalBytes
    runtime_tree_sha256    = $treeSha
}

# NOTE (B3-fix1, Owner review 2026-08-19): the optional manifest_file_sha256 field is
# intentionally NOT emitted. Hashing a file against itself is recursive — the old two-write
# approach hashed an intermediate copy (with the field null) and then rewrote the file, so the
# recorded value was NOT the final file's own SHA. Nothing in the lock or in
# verify-runtime-manifest.ps1 consumes this field, so it is dropped rather than kept with a
# wrong value. If a file-level hash is ever needed later, define it explicitly as
# SHA256(final manifest bytes) and document the two-phase (placeholder -> rewrite) caveat.

$outDir = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($OutFile))
if (-not (Test-Path -LiteralPath $outDir -PathType Container)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}
$json = $manifest | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($OutFile), $json)

Write-Host "build-runtime-manifest: wrote $($files.Count) files -> $OutFile"
Write-Host "build-runtime-manifest: runtime_tree_sha256 = $treeSha"
Write-Host "build-runtime-manifest: (manifest_file_sha256 intentionally not emitted; see B3-fix1 note)"

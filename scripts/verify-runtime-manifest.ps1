<#
.SYNOPSIS
    FSO Runtime Provisioning Closure — verify a runtime/python tree against its manifest and lock.

.DESCRIPTION
    Re-enumerates the actual -RuntimeRoot, recomputes every file's size + SHA-256, and checks:
      * no missing / extra / modified files vs the manifest;
      * recomputed runtime_tree_sha256 == manifest.runtime_tree_sha256;
      * manifest.runtime_tree_sha256 == lock.runtime_tree_manifest_sha256 (unless -IgnoreLockMismatch);
      * python.exe SHA == lock.python_executable_sha256;
      * (if -WheelCache given) pip + 7 dependency wheel SHAs match the lock and no undeclared
        wheels are present in the cache.

    On the FIRST inconsistency it prints path / expected / actual and exits with a non-zero code.
    -IgnoreLockMismatch relaxes only the lock<->manifest tree-hash comparison (used before the
    lock is updated after the first B4 build, per the gated update process).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$RuntimeRoot = 'runtime/python',

    [Parameter(Mandatory = $true)]
    [string]$ManifestFile,

    [Parameter(Mandatory = $false)]
    [string]$LockFile,

    [Parameter(Mandatory = $false)]
    [string]$WheelCache,

    [switch]$IgnoreLockMismatch
)

$ErrorActionPreference = 'Stop'

# Robust default resolution for the harness/re-hosting case where $PSScriptRoot is empty.
if (-not $LockFile) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $LockFile = Join-Path $scriptDir '..\engine\locks\python-runtime.lock.json'
}

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

function Fail {
    param([string]$Path, [string]$Expected, [string]$Actual, [string]$Kind)
    Write-Error "VERIFY FAILED [$Kind]: $Path`n  expected: $Expected`n  actual:   $Actual"
    exit 1
}

# ---------------------------------------------------------------------------
# Load manifest + lock
# ---------------------------------------------------------------------------
function Assert-Cond { param([bool]$C, [string]$M) if (-not $C) { throw "verify-runtime-manifest: $M" } }

Assert-Cond (Test-Path -LiteralPath $ManifestFile -PathType Leaf) "manifest not found: $ManifestFile"
$manifest = Get-Content -LiteralPath $ManifestFile -Raw | ConvertFrom-Json
Assert-Cond (Test-Path -LiteralPath $LockFile -PathType Leaf) "lock not found: $LockFile"
$lock = Get-Content -LiteralPath $LockFile -Raw | ConvertFrom-Json

# ---------------------------------------------------------------------------
# Re-enumerate the actual tree
# ---------------------------------------------------------------------------
$rootFull = [System.IO.Path]::GetFullPath($RuntimeRoot)
Assert-Cond (Test-Path -LiteralPath $rootFull -PathType Container) "RuntimeRoot not found: $rootFull"

$actual = @{}
$lowerSeen = @{}

# Manual walk: reject reparse points / symlinks / junctions BEFORE descending.
$stack = New-Object System.Collections.Stack
$stack.Push($rootFull)
while ($stack.Count -gt 0) {
    $dir = $stack.Pop()
    foreach ($item in Get-ChildItem -LiteralPath $dir -Force) {
        $full = $item.FullName
        $attr = [System.IO.File]::GetAttributes($full)
        if ($attr.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
            Fail $full '<no reparse point>' '<reparse point>' 'REPARSE_POINT'
        }
        if ($item.PSIsContainer) { $stack.Push($full); continue }

        $rel = $full.Substring($rootFull.Length).TrimStart('\', '/').Replace('\', '/')
        if (Test-Excluded $rel) { continue }
        $low = $rel.ToLowerInvariant()
        if ($lowerSeen.ContainsKey($low) -and $lowerSeen[$low] -ne $rel) {
            Fail $rel "unique case" "case conflict with $($lowerSeen[$low])" 'CASE_CONFLICT'
        }
        $lowerSeen[$low] = $rel
        $actual[$rel] = [PSCustomObject]@{ relative_path = $rel; size = $item.Length; sha256 = (Get-Sha256Hex $full) }
    }
}

# ---------------------------------------------------------------------------
# Compare against manifest
# ---------------------------------------------------------------------------
$manifestFiles = @{}
foreach ($mf in $manifest.files) { $manifestFiles[$mf.relative_path] = $mf }

foreach ($rel in $manifestFiles.Keys) {
    if (-not $actual.ContainsKey($rel)) { Fail $rel '<present>' '<missing>' 'MISSING_FILE' }
    $a = $actual[$rel]; $m = $manifestFiles[$rel]
    if ([int]$a.size -ne [int]$m.size) { Fail $rel $m.size $a.size 'SIZE_MISMATCH' }
    if ($a.sha256 -ne $m.sha256) { Fail $rel $m.sha256 $a.sha256 'SHA_MISMATCH' }
}
foreach ($rel in $actual.Keys) {
    if (-not $manifestFiles.ContainsKey($rel)) { Fail $rel '<absent>' '<extra>' 'EXTRA_FILE' }
}

# Recompute runtime_tree_sha256 and compare to manifest
$ordComparer = [System.StringComparer]::Ordinal
$sorted = [System.Linq.Enumerable]::OrderBy(
    @($actual.Values),
    [System.Func[object, string]] { param($e) $e.relative_path },
    $ordComparer
)
$actualFiles = @($sorted)
$canonical = Format-CanonicalJson $actualFiles
$canonicalBytes = [System.Text.Encoding]::UTF8.GetBytes($canonical)
$recomputedTreeSha = (Get-Sha256HexFromBytes $canonicalBytes)

if ($recomputedTreeSha -ne $manifest.runtime_tree_sha256) {
    Fail 'runtime_tree_sha256' $manifest.runtime_tree_sha256 $recomputedTreeSha 'TREE_HASH_MISMATCH'
}

# Compare to lock (unless relaxed)
if (-not $IgnoreLockMismatch) {
    if ($manifest.runtime_tree_sha256 -ne $lock.runtime_tree_manifest_sha256) {
        Fail 'lock.runtime_tree_manifest_sha256' $lock.runtime_tree_manifest_sha256 $manifest.runtime_tree_sha256 'LOCK_TREE_HASH_MISMATCH'
    }
}

# python.exe SHA
$pyExe = Join-Path $rootFull 'python.exe'
Assert-Cond (Test-Path -LiteralPath $pyExe -PathType Leaf) "python.exe missing in runtime: $pyExe"
$pyExeSha = (Get-Sha256Hex $pyExe)
if ($pyExeSha -ne $lock.python_executable_sha256.ToLowerInvariant()) {
    Fail 'python.exe' $lock.python_executable_sha256 $pyExeSha 'PYEXE_SHA_MISMATCH'
}

# Optional wheel input SHAs
if ($WheelCache) {
    Assert-Cond (Test-Path -LiteralPath $WheelCache -PathType Container) "-WheelCache not found: $WheelCache"
    $expected = New-Object 'System.Collections.Generic.HashSet[string]'
    $null = $expected.Add($lock.pip.wheel)
    foreach ($dep in $lock.dependencies) { $null = $expected.Add($dep.wheel) }

    function Check-Wheel {
        param([string]$Name, [string]$ExpectedSha)
        $p = Join-Path $WheelCache $Name
        if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { Fail $Name '<present>' '<missing>' 'WHEEL_MISSING' }
        $sha = (Get-Sha256Hex $p)
        if ($sha -ne $ExpectedSha.ToLowerInvariant()) { Fail $Name $ExpectedSha $sha 'WHEEL_SHA_MISMATCH' }
    }
    Check-Wheel $lock.pip.wheel $lock.pip.sha256
    foreach ($dep in $lock.dependencies) { Check-Wheel $dep.wheel $dep.sha256 }

    foreach ($w in @(Get-ChildItem -LiteralPath $WheelCache -Filter *.whl -File | ForEach-Object { $_.Name })) {
        if (-not $expected.Contains($w)) { Fail $w '<absent>' '<undeclared>' 'WHEEL_UNDECLARED' }
    }
}

Write-Host "verify-runtime-manifest: PASS (files=$($actualFiles.Count), tree_sha=$recomputedTreeSha)"
exit 0

<#
.SYNOPSIS
    M2-FIX-03 / Runtime Provisioning Closure (Option B) — provision a self-contained Python
    runtime into the Observer release package, fully offline.

.DESCRIPTION
    Builds <Destination>/runtime/python from a pre-extracted CPython 3.12.8 embeddable tree
    (-PythonSource), then:

      1. Validates ALL fixed inputs against python-runtime.lock.json BEFORE touching anything
         (python.exe SHA, optional archive name+SHA, pip wheel name/size/SHA, 7 dependency
         wheels name/version/SHA, and that the wheel cache contains no undeclared wheels).
      2. Offline-seeds pip by expanding pip-24.3.1-py3-none-any.whl into
         runtime/python/Lib/site-packages via an explicit .NET ZIP API (never Expand-Archive,
         never a network call).
      3. Offline-installs numpy + jsonschema (and their 5 transitive deps) from -WheelCache
         using only the target interpreter, --no-index --find-links --only-binary :all:.
      4. Deletes non-deterministic artifacts (__pycache__, *.pyc, direct_url.json, pip caches).

    The build happens in a STAGING directory; the live runtime/python is replaced only after
    every verification passes. On failure the staging dir is removed and any existing valid
    runtime is left untouched. This script NEVER modifies the lock file and NEVER sets
    FSP_PRIVATE_PYTHON.

    This script performs NO network access. It requires pip-24.3.1-py3-none-any.whl (and the
    7 dependency wheels) to already be present in -WheelCache.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PythonSource,

    [Parameter(Mandatory = $false)]
    [string]$PythonArchive,

    [Parameter(Mandatory = $true)]
    [string]$WheelCache,

    [Parameter(Mandatory = $true)]
    [string]$Destination,

    [Parameter(Mandatory = $false)]
    [string]$LockFile
)

$ErrorActionPreference = "Stop"

# Determinism: never write bytecode caches. MUST be set BEFORE the first python invocation
# (the earliest runs are `python --version` and `python -m pip --version`, which otherwise
# compile imports and leave __pycache__/*.pyc behind in the artifact).
$env:PYTHONDONTWRITEBYTECODE = '1'

# Robust default resolution: $PSScriptRoot is normally set for -File invocation; fall back to
# the script's own path when it is absent (e.g. some re-hosting harnesses).
if (-not $LockFile) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $LockFile = Join-Path $scriptDir '..\engine\locks\python-runtime.lock.json'
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Get-Sha256Hex {
    param([string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try { $bytes = $sha.ComputeHash($stream) }
        finally { $stream.Dispose() }
    } finally { $sha.Dispose() }
    return [BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
}

function Assert-Condition {
    param([bool]$Cond, [string]$Message)
    if (-not $Cond) { throw "provision-runtime-python: $Message" }
}

function Get-ExpectedWheel {
    param([string]$CacheDir, [string]$WheelName)
    $candidate = Join-Path $CacheDir $WheelName
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $null }
    return $candidate
}

# Extract a .whl (a zip) into $SitePackages using an explicit .NET ZIP API.
# Guards against path traversal and refuses to overwrite any pre-existing file.
function Expand-WhlToSitePackages {
    param([string]$WhlPath, [string]$SitePackages)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $siteRoot = [System.IO.Path]::GetFullPath($SitePackages)
    if (-not (Test-Path -LiteralPath $siteRoot -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $siteRoot | Out-Null
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($WhlPath)
    try {
        foreach ($entry in $archive.Entries) {
            # Only file entries are extracted (directory entries are implicit).
            if ([string]::IsNullOrEmpty($entry.Name) -and $entry.FullName.EndsWith('/')) { continue }

            $normalized = $entry.FullName.Replace('\', '/')
            # Path traversal guard.
            if ($normalized.StartsWith('/') -or
                $normalized.StartsWith('../') -or
                $normalized.Contains('/../') -or
                $normalized.Contains('..\')) {
                throw "provision-runtime-python: unsafe zip entry rejected: $($entry.FullName)"
            }

            $targetRel = Join-Path $siteRoot $normalized
            $targetFull = [System.IO.Path]::GetFullPath($targetRel)
            # Ensure the resolved target stays inside the site-packages root.
            if (-not ($targetFull.StartsWith($siteRoot + [System.IO.Path]::DirectorySeparatorChar) -or
                      $targetFull -eq $siteRoot)) {
                throw "provision-runtime-python: zip entry escapes site-packages: $($entry.FullName)"
            }
            # Refuse to overwrite anything not produced by this seed (atomicity / safety).
            if (Test-Path -LiteralPath $targetFull) {
                throw "provision-runtime-python: refusing to overwrite existing file during seed: $targetFull"
            }

            $targetDir = [System.IO.Path]::GetDirectoryName($targetFull)
            if (-not (Test-Path -LiteralPath $targetDir -PathType Container)) {
                New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
            }
            $out = [System.IO.File]::Create($targetFull)
            try {
                $in = $entry.Open()
                try { $in.CopyTo($out) } finally { $in.Dispose() }
            } finally { $out.Dispose() }
        }
    } finally {
        $archive.Dispose()
    }
}

# ---------------------------------------------------------------------------
# 0. Load + sanity-check the lock
# ---------------------------------------------------------------------------
Assert-Condition (Test-Path -LiteralPath $LockFile -PathType Leaf) "lock file not found: $LockFile"
$lock = Get-Content -LiteralPath $LockFile -Raw | ConvertFrom-Json
Assert-Condition ($lock.version -eq '3.12.8') "lock.version is '$($lock.version)', expected 3.12.8"
$expectedPyExeSha = $lock.python_executable_sha256.ToLowerInvariant()

# ---------------------------------------------------------------------------
# 1. Validate all fixed inputs (fail fast: no copy, no install on mismatch)
# ---------------------------------------------------------------------------
Write-Host "provision-runtime-python: [1/4] validating fixed inputs against lock"
Assert-Condition (Test-Path -LiteralPath $PythonSource -PathType Container) "-PythonSource directory not found: $PythonSource"
$srcExe = Join-Path $PythonSource 'python.exe'
Assert-Condition (Test-Path -LiteralPath $srcExe -PathType Leaf) "python.exe not found in -PythonSource: $srcExe"

# Python version + python.exe SHA
$pyVer = & $srcExe --version 2>&1 | Out-String
Assert-Condition ($pyVer -match '3\.12\.8') "python --version is '$pyVer' (expected 3.12.8)"
$srcExeSha = (Get-Sha256Hex $srcExe)
Assert-Condition ($srcExeSha -eq $expectedPyExeSha) "
  python.exe SHA mismatch.
    expected (lock): $expectedPyExeSha
    actual   (source): $srcExeSha"

# Optional explicit archive validation (never guessed from adjacent paths)
if ($PythonArchive) {
    Assert-Condition (Test-Path -LiteralPath $PythonArchive -PathType Leaf) "-PythonArchive not found: $PythonArchive"
    $archiveName = [System.IO.Path]::GetFileName($PythonArchive)
    Assert-Condition ($archiveName -eq $lock.distribution.archive) "
      Python archive filename mismatch.
        expected (lock): $($lock.distribution.archive)
        actual:           $archiveName"
    $archiveSha = (Get-Sha256Hex $PythonArchive)
    Assert-Condition ($archiveSha -eq $lock.distribution.archive_sha256.ToLowerInvariant()) "
      Python archive SHA mismatch.
        expected (lock): $($lock.distribution.archive_sha256)
        actual:           $archiveSha"
    Write-Host "provision-runtime-python: archive $archiveName validated (SHA OK)"
}

# Wheel cache must exist
Assert-Condition (Test-Path -LiteralPath $WheelCache -PathType Container) "-WheelCache directory not found: $WheelCache"

# Build expected wheel name set from lock.
$expectedWheels = New-Object 'System.Collections.Generic.HashSet[string]'
$null = $expectedWheels.Add($lock.pip.wheel)
foreach ($dep in $lock.dependencies) { $null = $expectedWheels.Add($dep.wheel) }

# Validate pip wheel
$pipWhl = Get-ExpectedWheel $WheelCache $lock.pip.wheel
Assert-Condition ($null -ne $pipWhl) "pip wheel '$($lock.pip.wheel)' not found in -WheelCache"
$pipSha = (Get-Sha256Hex $pipWhl); $pipSize = (Get-Item -LiteralPath $pipWhl).Length
Assert-Condition ($pipSha -eq $lock.pip.sha256.ToLowerInvariant()) "
  pip wheel SHA mismatch.
    expected (lock): $($lock.pip.sha256)
    actual:           $pipSha"
Assert-Condition ($pipSize -eq $lock.pip.size) "
  pip wheel size mismatch.
    expected (lock): $($lock.pip.size)
    actual:           $pipSize"

# Validate 7 dependency wheels
foreach ($dep in $lock.dependencies) {
    $depWhl = Get-ExpectedWheel $WheelCache $dep.wheel
    Assert-Condition ($null -ne $depWhl) "dependency wheel '$($dep.wheel)' ($( $dep.name )) not found in -WheelCache"
    $depSha = (Get-Sha256Hex $depWhl); $depSize = (Get-Item -LiteralPath $depWhl).Length
    Assert-Condition ($depSha -eq $dep.sha256.ToLowerInvariant()) "
      dependency '$($dep.name)' wheel SHA mismatch.
        expected (lock): $($dep.sha256)
        actual:           $depSha"
    Assert-Condition ($depSize -eq $dep.size) "
      dependency '$($dep.name)' wheel size mismatch.
        expected (lock): $($dep.size)
        actual:           $depSize"
}

# No undeclared wheels allowed in the cache
$actualWheels = @(Get-ChildItem -LiteralPath $WheelCache -Filter *.whl -File | ForEach-Object { $_.Name })
foreach ($w in $actualWheels) {
    Assert-Condition ($expectedWheels.Contains($w)) "undeclared wheel present in -WheelCache (not in lock): $w"
}
Write-Host "provision-runtime-python: all inputs validated (python.exe, pip, 7 deps, cache clean)"

# ---------------------------------------------------------------------------
# 2. Stage build (atomic: existing runtime untouched until all checks pass)
# ---------------------------------------------------------------------------
$runtimeDir = Join-Path $Destination 'runtime'
$stagingDir = Join-Path $runtimeDir ('_staging_python_' + $PID)
if (Test-Path -LiteralPath $stagingDir) { Remove-Item -LiteralPath $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null

try {
    Write-Host "provision-runtime-python: [2/4] copying Python tree into staging $stagingDir"
    Copy-Item -Path (Join-Path $PythonSource '*') -Destination $stagingDir -Recurse -Force

    $stagingExe = Join-Path $stagingDir 'python.exe'
    Assert-Condition (Test-Path -LiteralPath $stagingExe -PathType Leaf) "copy failed; python.exe missing at $stagingExe"

    # Enable 'import site' in the embeddable _pth so site-packages is importable.
    $pthPath = Join-Path $stagingDir 'python312._pth'
    if (Test-Path -LiteralPath $pthPath -PathType Leaf) {
        $pth = [System.IO.File]::ReadAllText($pthPath)
        $pth = [regex]::Replace($pth, '(?m)^\s*#\s*import\s+site\s*\r?$', "import site" + [Environment]::NewLine)
        if (-not [regex]::IsMatch($pth, '(?m)^\s*Lib[\\/]site-packages\s*$')) {
            if (-not $pth.EndsWith([Environment]::NewLine)) { $pth += [Environment]::NewLine }
            $pth += "Lib/site-packages" + [Environment]::NewLine
        }
        [System.IO.File]::WriteAllText($pthPath, $pth)
        Write-Host "provision-runtime-python: enabled 'import site' in python312._pth"
    } else {
        throw "python312._pth not found at $pthPath; site-packages would be unavailable"
    }

    # ---- 2a. Offline seed pip via explicit .NET ZIP API ----
    Write-Host "provision-runtime-python: seeding pip from $pipWhl"
    $sitePkgs = Join-Path $stagingDir 'Lib/site-packages'
    Expand-WhlToSitePackages -WhlPath $pipWhl -SitePackages $sitePkgs

    $pipInit = Join-Path $sitePkgs 'pip/__init__.py'
    $pipMeta = Join-Path $sitePkgs "pip-$($lock.pip.version).dist-info/METADATA"
    Assert-Condition (Test-Path -LiteralPath $pipInit -PathType Leaf) "pip seed failed: $pipInit missing"
    Assert-Condition (Test-Path -LiteralPath $pipMeta -PathType Leaf) "pip seed failed: $pipMeta missing"

    $pipVerOut = & $stagingExe -m pip --version 2>&1 | Out-String
    Assert-Condition ($pipVerOut -match [regex]::Escape($lock.pip.version)) "
      pip --version did not report $($lock.pip.version).
        output: $pipVerOut"
    Write-Host "provision-runtime-python: pip seeded -> $($lock.pip.version)"

    # ---- 2b. Offline install dependencies (target interpreter only) ----
    $env:PYTHONDONTWRITEBYTECODE = '1'
    Write-Host "provision-runtime-python: offline pip install numpy==1.26.4 jsonschema==4.26.0"
    $installOut = & $stagingExe -m pip install --no-index --find-links $WheelCache --only-binary :all: --no-warn-script-location numpy==1.26.4 jsonschema==4.26.0 2>&1
    $installExit = $LASTEXITCODE
    $installOut | ForEach-Object { Write-Host $_ }
    Assert-Condition ($installExit -eq 0) "pip install failed (exit $installExit)"

    # Version sanity for numpy, jsonschema, and the 5 transitive deps.
    # NOTE: keep this python script SINGLE-QUOTED ONLY. PowerShell 5.1 strips embedded double
    # quotes when passing native arguments (CommandLineToArgvW quote-swallowing), which corrupts
    # `python -c` scripts; single-quoted strings pass through intact.
    $verifyScript = @'
import importlib.metadata as m
want = {
    'numpy': '1.26.4',
    'jsonschema': '4.26.0',
    'attrs': '26.1.0',
    'jsonschema_specifications': '2025.9.1',
    'referencing': '0.37.0',
    'rpds_py': '2026.6.3',
    'typing_extensions': '4.16.0',
}
bad = []
for name, ver in want.items():
    try:
        got = m.version(name)
    except Exception as e:
        bad.append(f'{name}: missing ({e})')
        continue
    if got != ver:
        bad.append(f'{name}: expected {ver}, got {got}')
if bad:
    raise SystemExit('VERSION_MISMATCH:' + ';'.join(bad))
print('ALL_DEPS_OK')
'@
    $oldEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # keep full stderr text; decide on the real exit code
    $verifyOut = & $stagingExe -c $verifyScript 2>&1 | Out-String
    $verifyExit = $LASTEXITCODE
    $ErrorActionPreference = $oldEap
    Assert-Condition ($verifyExit -eq 0) "post-install dependency version check failed (exit $verifyExit): $verifyOut"
    Assert-Condition ($verifyOut -match 'ALL_DEPS_OK') "post-install dependency version check did not report OK: $verifyOut"
    Write-Host "provision-runtime-python: deps installed -> numpy 1.26.4, jsonschema 4.26.0, +5 transitive"

    # ---- 2c. Determinism cleanup ----
    Write-Host "provision-runtime-python: removing non-deterministic artifacts"
    Get-ChildItem -LiteralPath $stagingDir -Recurse -Directory -Filter __pycache__ -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
    Get-ChildItem -LiteralPath $stagingDir -Recurse -File -Filter *.pyc -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    Get-ChildItem -LiteralPath $stagingDir -Recurse -File -Filter direct_url.json -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

    # Console-script launchers (Scripts/*.exe) embed the interpreter's ABSOLUTE path at install
    # time (sys.executable = the PID-suffixed staging dir), so they are non-deterministic across
    # builds and would poison RECORD + the tree hash (B1 §3.4 / Owner review: delete
    # non-deterministic files rather than masking them). Observer only imports numpy/jsonschema
    # as libraries and never invokes these launchers, so prune Scripts/ and rewrite each RECORD
    # to drop the Scripts/ lines, keeping the artifact truthful and reproducible.
    $scriptsDir = Join-Path $stagingDir 'Scripts'
    if (Test-Path -LiteralPath $scriptsDir -PathType Container) {
        Remove-Item -LiteralPath $scriptsDir -Recurse -Force
        Write-Host "provision-runtime-python: pruned Scripts/ (path-embedding launchers)"
    }
    Get-ChildItem -LiteralPath $stagingDir -Recurse -File -Filter RECORD -ErrorAction SilentlyContinue |
        ForEach-Object {
            $recordPath = $_.FullName
            $orig = [System.IO.File]::ReadAllLines($recordPath)
            # RECORD launcher entries look like "../../Scripts/f2py.exe,sha256=...,size" — match
            # "Scripts/" at any position (line start or after ../ segments).
            $kept = @($orig | Where-Object { -not ($_ -match '(^|/)Scripts/') })
            if ($kept.Count -ne $orig.Count) {
                [System.IO.File]::WriteAllLines($recordPath, $kept)
                Write-Host "provision-runtime-python: rewrote RECORD (removed Scripts/ refs): $recordPath"
            }
        }

    Write-Host "provision-runtime-python: [3/4] staging build complete; ready to swap"
} catch {
    # Atomicity: remove staging, leave any existing runtime untouched.
    if (Test-Path -LiteralPath $stagingDir) { Remove-Item -LiteralPath $stagingDir -Recurse -Force }
    throw "provision-runtime-python: staging failed -> $($_.Exception.Message)"
}

# ---------------------------------------------------------------------------
# 4. Atomic swap (only after all verifications passed)
# ---------------------------------------------------------------------------
$destPython = Join-Path $runtimeDir 'python'
if (Test-Path -LiteralPath $destPython) { Remove-Item -LiteralPath $destPython -Recurse -Force }
Move-Item -LiteralPath $stagingDir -Destination $destPython

Write-Host "provision-runtime-python: [4/4] OK -> $destPython (python $($lock.version), pip $($lock.pip.version))"
Write-Host "provision-runtime-python: NOTE: runtime_tree_manifest_sha256 is regenerated separately by build-runtime-manifest.ps1; this script does NOT modify the lock."

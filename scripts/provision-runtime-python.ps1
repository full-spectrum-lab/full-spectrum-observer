<#
.SYNOPSIS
    M2-FIX-03 — provision a self-contained Python runtime into the Observer release package.

.DESCRIPTION
    Copies a pre-built Python distribution (supplied by CI / WorkBuddy via -PythonSource) into
    <Destination>/runtime/python, then performs an OFFLINE `pip install` of numpy + jsonschema
    from the wheel cache (-WheelCache) so the formal package needs no network egress at publish
    time. The interpreter version is whatever the source provides (must be 3.12+).

    Idempotent: re-running overwrites the runtime tree and re-runs pip (a no-op for satisfied
    packages). Fails clearly with a non-zero exit when the source or the wheel cache is missing.

    This script NEVER performs a network download — it only copies a local tree and installs
    from a local wheel cache.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PythonSource,

    [Parameter(Mandatory = $true)]
    [string]$WheelCache,

    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = "Stop"

# --- Validate inputs -------------------------------------------------------
if (-not (Test-Path -LiteralPath $PythonSource -PathType Container)) {
    throw "provision-runtime-python: -PythonSource directory not found: $PythonSource"
}
$srcExe = Join-Path $PythonSource "python.exe"
if (-not (Test-Path -LiteralPath $srcExe -PathType Leaf)) {
    throw "provision-runtime-python: python.exe not found in -PythonSource: $srcExe"
}
if (-not (Test-Path -LiteralPath $WheelCache -PathType Container)) {
    throw "provision-runtime-python: -WheelCache directory not found: $WheelCache"
}
$wheels = @(Get-ChildItem -LiteralPath $WheelCache -Filter *.whl -File)
if ($wheels.Count -eq 0) {
    throw "provision-runtime-python: no .whl files found in -WheelCache: $WheelCache"
}

# --- Copy the Python tree --------------------------------------------------
$runtimeDir = Join-Path $Destination "runtime"
$pythonDir = Join-Path $runtimeDir "python"
if (Test-Path -LiteralPath $pythonDir) {
    Remove-Item -LiteralPath $pythonDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $pythonDir | Out-Null

Write-Host "provision-runtime-python: copying Python tree from $PythonSource into $pythonDir"
Copy-Item -Path (Join-Path $PythonSource "*") -Destination $pythonDir -Recurse -Force

$destExe = Join-Path $pythonDir "python.exe"
if (-not (Test-Path -LiteralPath $destExe -PathType Leaf)) {
    throw "provision-runtime-python: copy failed; python.exe missing at $destExe"
}

# --- Fix the embeddable interpreter's _pth so site-packages is importable ----------------
# The official Python 3.12.x embeddable package ships python312._pth with `import site` commented
# out, which disables the site-packages search path. numpy/jsonschema live in site-packages, so we
# must enable it (uncomment `import site`) for the formal runtime to `import numpy; import jsonschema`.
$pthPath = Join-Path $pythonDir "python312._pth"
if (Test-Path -LiteralPath $pthPath -PathType Leaf) {
    $pth = [System.IO.File]::ReadAllText($pthPath)
    # Enable site.main() — handles both `#import site` and `# import site` forms. The match includes
    # the optional trailing CR so the replacement keeps a clean CRLF line ending; the comment line's
    # newline must NOT be swallowed, otherwise `import site` and the next line would merge.
    $pth = [regex]::Replace($pth, '(?m)^\s*#\s*import\s+site\s*\r?$', "import site" + [Environment]::NewLine)
    # Defensive: guarantee the standard site-packages directory is on the search path.
    if (-not [regex]::IsMatch($pth, '(?m)^\s*Lib[\\/]site-packages\s*$')) {
        if (-not $pth.EndsWith([Environment]::NewLine)) {
            $pth += [Environment]::NewLine
        }
        $pth += "Lib/site-packages" + [Environment]::NewLine
    }
    # WriteAllText uses UTF-8 without BOM, which the embeddable interpreter expects for _pth.
    [System.IO.File]::WriteAllText($pthPath, $pth)
    Write-Host "provision-runtime-python: enabled 'import site' in python312._pth"
} else {
    Write-Warning "provision-runtime-python: python312._pth not found at $pthPath; site-packages may be unavailable"
}

# --- Offline install of declared dependencies ------------------------------
Write-Host "provision-runtime-python: offline pip install numpy==1.26.4 jsonschema==4.26.0 from $WheelCache"
& $destExe -m pip install --no-index --find-links $WheelCache numpy==1.26.4 jsonschema==4.26.0
if ($LASTEXITCODE -ne 0) {
    throw "provision-runtime-python: pip install failed (exit $LASTEXITCODE)"
}

# --- Sanity: numpy + jsonschema must import from the provisioned runtime -------------------
$raw = & $destExe -c "import numpy, jsonschema; print(numpy.__version__); print(jsonschema.__version__)" | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "provision-runtime-python: post-install import check failed (exit $LASTEXITCODE)"
}
$lines = ($raw -split '\r?\n' | Where-Object { $_.Trim() -ne '' }).Trim()
$numpyVer = $lines[0]
$jsonschemaVer = $lines[1]
if ($numpyVer -ne '1.26.4') {
    throw "provision-runtime-python: unexpected numpy version '$numpyVer' (expected 1.26.4)"
}
if ($jsonschemaVer -ne '4.26.0') {
    throw "provision-runtime-python: unexpected jsonschema version '$jsonschemaVer' (expected 4.26.0)"
}
Write-Host "provision-runtime-python: OK -> $destExe (numpy $numpyVer, jsonschema $jsonschemaVer)"

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

# --- Offline install of declared dependencies ------------------------------
Write-Host "provision-runtime-python: offline pip install numpy==1.26.4 jsonschema==4.26.0 from $WheelCache"
& $destExe -m pip install --no-index --find-links $WheelCache numpy==1.26.4 jsonschema==4.26.0
if ($LASTEXITCODE -ne 0) {
    throw "provision-runtime-python: pip install failed (exit $LASTEXITCODE)"
}

# --- Sanity: numpy must import from the provisioned runtime ----------------
$verify = & $destExe -c "import numpy, jsonschema; print('numpy', numpy.__version__)"
if ($LASTEXITCODE -ne 0) {
    throw "provision-runtime-python: post-install import check failed (exit $LASTEXITCODE)"
}
Write-Host "provision-runtime-python: OK -> $destExe ($verify)"

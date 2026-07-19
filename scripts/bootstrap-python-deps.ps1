<#
.SYNOPSIS
    Bootstrap the repository's formal Python test/verification dependencies.

.DESCRIPTION
    Installs the dependencies declared in scripts/requirements.txt into the
    pinned private Python interpreter. This is the "fresh clone auto-recovery"
    entry point: scripts/test.ps1 dot-sources this file and calls
    Install-FspPythonDeps so a clean environment self-installs jsonschema
    (and any future declared dep) before the gate scripts run.

    Safe to run repeatedly: pip is a no-op for already-satisfied packages.
#>
[CmdletBinding()]
param(
    [string]$Python = $env:FSP_PRIVATE_PYTHON
)

function Install-FspPythonDeps {
    param(
        [string]$Python
    )
    $ErrorActionPreference = "Stop"
    if (-not $Python -or -not (Test-Path -LiteralPath $Python -PathType Leaf)) {
        Write-Error "Install-FspPythonDeps: a concrete Python executable is required (pass -Python or set FSP_PRIVATE_PYTHON)."
        exit 3
    }
    $Req = Join-Path $PSScriptRoot "requirements.txt"
    if (-not (Test-Path -LiteralPath $Req)) {
        Write-Error "Install-FspPythonDeps: requirements.txt not found at $Req"
        exit 3
    }
    Write-Host "Bootstrap: installing Python test/verification deps from $Req via $Python"
    & $Python -m pip install -r $Req --quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Bootstrap: pip install returned exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
    Write-Host "Bootstrap: Python test/verification deps satisfied."
}

# When dot-sourced (`. script.ps1`) only define the function; the caller invokes it.
# When executed directly, run it immediately.
if ($MyInvocation.InvocationName -ne '.') {
    Install-FspPythonDeps -Python $Python
}

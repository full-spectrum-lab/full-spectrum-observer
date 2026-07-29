<#
.SYNOPSIS
    Bootstrap the repository's formal Python test/verification dependencies.

.DESCRIPTION
    Installs the dependencies declared in scripts/requirements.txt into the
    pinned private Python interpreter. This is the "fresh clone auto-recovery"
    entry point: scripts/test.ps1 dot-sources this file and calls
    Install-FspPythonDeps so a clean environment self-installs jsonschema (and
    numpy for the Engine v1.5.0 worker) before the gate scripts run.

    Safe to run repeatedly: pip is a no-op for already-satisfied packages.

    CP936 fix (M2-FIX-03): PYTHONUTF8=1 is forced on the current process AND on
    the spawned pip process environment BEFORE `pip install -r`, so reading the
    UTF-8 requirements file on Chinese-Windows (code page 936) no longer raises
    UnicodeDecodeError.
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

    # M2-FIX-03: force UTF-8 mode on this shell so the spawned pip process inherits
    # PYTHONUTF8=1 (the child process inherits the parent's environment block). On
    # CP936 (Chinese Windows) this prevents `UnicodeDecodeError` when pip reads the
    # UTF-8 requirements.txt. We also pass it explicitly into the pip process env.
    $env:PYTHONUTF8 = "1"

    Write-Host "Bootstrap: installing Python test/verification deps from $Req via $Python"
    Write-Host "Bootstrap: PYTHONUTF8=$([Environment]::GetEnvironmentVariable('PYTHONUTF8')) (UTF-8 mode enforced)"

    # Spawn pip with an explicit PYTHONUTF8=1 in its own environment block rather than
    # relying solely on inheritance — this is the belt-and-suspenders guarantee the
    # design calls for ("set before pip AND pass into the spawned pip process env").
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Python
    $psi.Arguments = "-m pip install -r `"$Req`" --quiet"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $false
    $psi.RedirectStandardError = $false
    if (-not $psi.Environment.ContainsKey("PYTHONUTF8")) {
        $psi.Environment["PYTHONUTF8"] = "1"
    }
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    if (-not $proc.Start()) {
        Write-Error "Bootstrap: failed to start pip process."
        exit 3
    }
    $proc.WaitForExit()
    if ($proc.ExitCode -ne 0) {
        Write-Warning "Bootstrap: pip install returned exit code $($proc.ExitCode)"
        exit $proc.ExitCode
    }
    Write-Host "Bootstrap: Python test/verification deps satisfied."
}

# When dot-sourced (`. script.ps1`) only define the function; the caller invokes it.
# When executed directly, run it immediately.
if ($MyInvocation.InvocationName -ne '.') {
    Install-FspPythonDeps -Python $Python
}

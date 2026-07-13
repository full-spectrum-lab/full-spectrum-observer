[CmdletBinding()]
param(
    [ValidateSet("IG1","IG2","IG3","IG4","IG5","IG6","IG7","IG8")]
    [string]$Gate = "IG1",
    [string]$PrivatePython = $env:FSP_PRIVATE_PYTHON,
    [string]$SqliteNativeDirectory = $env:FSP_SQLITE_NATIVE_DIR
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Invoke-Ig2Prerequisites {
    & dotnet run --project (Join-Path $RepoRoot "tests/Observer.Tests.Unit/Observer.Tests.Unit.csproj") --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & dotnet run --project (Join-Path $RepoRoot "tests/Observer.Tests.Contract/Observer.Tests.Contract.csproj") --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $PrivatePython (Join-Path $PSScriptRoot "verify-architecture.py")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $PrivatePython (Join-Path $PSScriptRoot "ig2-reference-validator.py")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Require-PrivatePython {
    $IsAbsoluteWindowsPath = -not [string]::IsNullOrWhiteSpace($PrivatePython) -and
        $PrivatePython -match '^(?:[A-Za-z]:[\\/]|\\\\)'
    if (-not $IsAbsoluteWindowsPath -or -not (Test-Path -LiteralPath $PrivatePython -PathType Leaf)) {
        Write-Error "FSP_PRIVATE_PYTHON/PrivatePython must be an absolute path to the pinned private Python executable."
        exit 3
    }
}

& (Join-Path $PSScriptRoot "build.ps1") -Configuration Release -Locked
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Gate -eq "IG1") { exit 0 }

Require-PrivatePython
Invoke-Ig2Prerequisites
if ($Gate -eq "IG2") { exit 0 }

if ($Gate -eq "IG3") {
    if ([string]::IsNullOrWhiteSpace($SqliteNativeDirectory) -or -not (Test-Path -LiteralPath $SqliteNativeDirectory -PathType Container)) {
        Write-Error "FSP_SQLITE_NATIVE_DIR/SqliteNativeDirectory must contain the pinned win-x64 sqlite3.dll."
        exit 3
    }
    $env:PATH = (Resolve-Path $SqliteNativeDirectory).Path + [IO.Path]::PathSeparator + $env:PATH
    $env:FSP_TEST_SCOPE = "IG3"
    & dotnet run --project (Join-Path $RepoRoot "tests/Observer.Tests.Integration/Observer.Tests.Integration.csproj") --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $env:FSP_FORMAL_GATE_CONTEXT = "IG3"
    & $PrivatePython (Join-Path $PSScriptRoot "ig3-reference-validator.py")
    exit $LASTEXITCODE
}

if ($Gate -eq "IG4") {
    $env:FSP_PRIVATE_PYTHON = (Resolve-Path $PrivatePython).Path
    $env:FSP_TEST_SCOPE = "IG4"
    & $PrivatePython (Join-Path $PSScriptRoot "ig4-worker-smoke.py")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & dotnet run --project (Join-Path $RepoRoot "tests/Observer.Tests.Integration/Observer.Tests.Integration.csproj") --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $env:FSP_FORMAL_GATE_CONTEXT = "IG4"
    & $PrivatePython (Join-Path $PSScriptRoot "ig4-worker-smoke.py")
    exit $LASTEXITCODE
}

if ($Gate -eq "IG5") {
    if ([string]::IsNullOrWhiteSpace($SqliteNativeDirectory) -or -not (Test-Path -LiteralPath $SqliteNativeDirectory -PathType Container)) {
        Write-Error "FSP_SQLITE_NATIVE_DIR/SqliteNativeDirectory must contain the pinned win-x64 sqlite3.dll."
        exit 3
    }
    $env:PATH = (Resolve-Path $SqliteNativeDirectory).Path + [IO.Path]::PathSeparator + $env:PATH
    $env:FSP_PRIVATE_PYTHON = (Resolve-Path $PrivatePython).Path
    $env:FSP_TEST_SCOPE = "IG5"
    & dotnet run --project (Join-Path $RepoRoot "tests/Observer.Tests.Integration/Observer.Tests.Integration.csproj") --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $env:FSP_FORMAL_GATE_CONTEXT = "IG5"
    & $PrivatePython (Join-Path $PSScriptRoot "ig5-reference-pipeline.py")
    exit $LASTEXITCODE
}

Write-Error "$Gate test execution is not implemented in this source candidate."
exit 3

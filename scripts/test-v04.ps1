[CmdletBinding()]
param(
    [string]$PrivatePython = $env:FSP_PRIVATE_PYTHON,
    [string]$SqliteNativeDirectory = $env:FSP_SQLITE_NATIVE_DIR,
    [string]$EvidenceRoot = $env:FSP_EVIDENCE_ROOT
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($PrivatePython) -or -not (Test-Path -LiteralPath $PrivatePython -PathType Leaf)) {
    throw "PrivatePython/FSP_PRIVATE_PYTHON must name the pinned Python executable."
}
if ([string]::IsNullOrWhiteSpace($SqliteNativeDirectory) -or -not (Test-Path -LiteralPath $SqliteNativeDirectory -PathType Container)) {
    throw "SqliteNativeDirectory/FSP_SQLITE_NATIVE_DIR must name the pinned SQLite native directory."
}
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $env:TEMP ("observer-v04-evidence-" + [guid]::NewGuid().ToString("N"))
}

$BeforeTrackedDiff = (& git -C $RepoRoot diff --binary | Out-String)
$BeforeStagedDiff = (& git -C $RepoRoot diff --cached --binary | Out-String)

try {
    $env:PATH = (Resolve-Path $SqliteNativeDirectory).Path + [IO.Path]::PathSeparator + $env:PATH
    & dotnet test (Join-Path $RepoRoot "tests/Observer.Tests.Unit/Observer.Tests.Unit.csproj") `
        --configuration Release --no-restore `
        --filter "ScenarioPackLoaderTests|ScenarioPackPersistenceTests|ScenarioPackEngineRequestTests|ScenarioPackPilotLoopTests" `
        --verbosity minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $env:FSP_PRIVATE_PYTHON = (Resolve-Path $PrivatePython).Path
    & dotnet test (Join-Path $RepoRoot "tests/Observer.Tests.Integration/Observer.Tests.Integration.csproj") `
        --configuration Release --no-restore `
        --filter "ScenarioPackRealEngineTests" `
        --verbosity minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $output = Join-Path $EvidenceRoot "v04/scenario-pack-gate.json"
    & $PrivatePython (Join-Path $PSScriptRoot "v04-scenario-pack-gate.py") --output $output
    exit $LASTEXITCODE
}
finally {
    $AfterTrackedDiff = (& git -C $RepoRoot diff --binary | Out-String)
    $AfterStagedDiff = (& git -C $RepoRoot diff --cached --binary | Out-String)
    if ($BeforeTrackedDiff -cne $AfterTrackedDiff -or $BeforeStagedDiff -cne $AfterStagedDiff) {
        Write-Error "v0.4 gate changed tracked repository content."
        exit 4
    }
    Write-Host "v0.4 gate repository drift check: PASS"
}

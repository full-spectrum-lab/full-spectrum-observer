[CmdletBinding()]
param(
    [ValidateSet("Debug","Release")]
    [string]$Configuration = "Release",
    [switch]$Locked
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$EvidenceDir = Join-Path $RepoRoot "evidence/ig1"
$LogPath = Join-Path $EvidenceDir "build-log.txt"
New-Item -ItemType Directory -Path $EvidenceDir -Force | Out-Null

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:NUGET_PACKAGES = Join-Path $RepoRoot ".packages"

$RequiredSdk = "10.0.301"
$ActualSdk = (& dotnet --version).Trim()
if ($ActualSdk -ne $RequiredSdk) {
    throw "Required .NET SDK $RequiredSdk, actual $ActualSdk."
}

& (Join-Path $PSScriptRoot "verify-baseline.ps1")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$RestoreArgs = @(
    "restore",
    (Join-Path $RepoRoot "FullSpectrum.Observer.sln"),
    "--configfile", (Join-Path $RepoRoot "NuGet.Config")
)
if ($Locked) {
    $RestoreArgs += "--locked-mode"
    $env:LockedRestore = "true"
}

$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($LogPath, "dotnet $($RestoreArgs -join ' ')`n", $Utf8NoBom)
& dotnet @RestoreArgs 2>&1 | Tee-Object -FilePath $LogPath -Append
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$BuildArgs = @(
    "build",
    (Join-Path $RepoRoot "FullSpectrum.Observer.sln"),
    "--configuration", $Configuration,
    "--no-restore"
)
& dotnet @BuildArgs 2>&1 | Tee-Object -FilePath $LogPath -Append
exit $LASTEXITCODE

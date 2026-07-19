[CmdletBinding()]
param(
    [ValidateSet("Debug","Release")]
    [string]$Configuration = "Release",
    [switch]$Locked
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# Official entry point: route all run evidence to an external directory by default so a
# fresh clone's working tree stays clean. Honour an explicit FSP_EVIDENCE_ROOT if the
# operator set one; otherwise generate a unique out-of-repo location under TEMP. The
# variable is inherited by every child process (PowerShell / Python / dotnet).
if ([string]::IsNullOrWhiteSpace($env:FSP_EVIDENCE_ROOT)) {
    $env:FSP_EVIDENCE_ROOT = Join-Path $env:TEMP ("full-spectrum-observer/evidence/" + [System.Guid]::NewGuid().ToString("N").Substring(0,12))
}

$EvidenceDir = Join-Path $env:FSP_EVIDENCE_ROOT "ig1"
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

# Official build entry must work on a cold fresh clone (empty NuGet cache).
# NuGet.Config uses <clear/> (controlled/locked restore), so without an explicit
# source a cold `dotnet restore` fails with NU1100. Inject the canonical nuget.org
# v3 feed here so operators never need a manual `-s`.
$DefaultNuGetSource = "https://api.nuget.org/v3/index.json"
$RestoreArgs = @(
    "restore",
    (Join-Path $RepoRoot "FullSpectrum.Observer.sln"),
    "--configfile", (Join-Path $RepoRoot "NuGet.Config"),
    "-s", $DefaultNuGetSource
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

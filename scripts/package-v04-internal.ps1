[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PrivatePythonDirectory,
    [Parameter(Mandatory = $true)][string]$SqliteNativeDirectory,
    [string]$DotnetRoot = $env:DOTNET_ROOT,
    [string]$OutputDirectory = "artifacts/v0.4-internal-candidate",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$PrivatePythonDirectory = (Resolve-Path $PrivatePythonDirectory).Path
$SqliteNativeDirectory = (Resolve-Path $SqliteNativeDirectory).Path
if ([string]::IsNullOrWhiteSpace($DotnetRoot)) {
    $DotnetRoot = Split-Path (Get-Command dotnet -ErrorAction Stop).Source
}
$DotnetRoot = (Resolve-Path $DotnetRoot).Path
if (-not (Test-Path (Join-Path $PrivatePythonDirectory "python.exe") -PathType Leaf)) { throw "Private Python executable is missing." }
if (-not (Test-Path (Join-Path $SqliteNativeDirectory "sqlite3.dll") -PathType Leaf)) { throw "Pinned sqlite3.dll is missing." }

$Commit = (& git -C $RepoRoot rev-parse HEAD).Trim()
$ShortCommit = $Commit.Substring(0, 12)
$OutputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $RepoRoot $OutputDirectory }
$Staging = Join-Path $OutputRoot "observer-v0.4.0-beta-internal-candidate-$ShortCommit"
$Zip = "$Staging.zip"
if (Test-Path $Staging) { Remove-Item $Staging -Recurse -Force }
if (Test-Path $Zip) { Remove-Item $Zip -Force }
New-Item -ItemType Directory -Force -Path $Staging, (Join-Path $Staging "app"), (Join-Path $Staging "web"), (Join-Path $Staging "runtime"), (Join-Path $Staging "runtime/dotnet"), (Join-Path $Staging "tools") | Out-Null

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:NUGET_PACKAGES = Join-Path $RepoRoot ".packages"
& dotnet publish (Join-Path $RepoRoot "src/Observer.Host.Cli/Observer.Host.Cli.csproj") --configuration $Configuration --no-restore --self-contained false --output (Join-Path $Staging "app")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet publish (Join-Path $RepoRoot "src/Observer.Host.Web/Observer.Host.Web.csproj") --configuration $Configuration --no-restore --self-contained false --output (Join-Path $Staging "web")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not (Test-Path (Join-Path $Staging "web/Observer.Host.Web.dll") -PathType Leaf)) {
    throw "Web Host publish did not produce web/Observer.Host.Web.dll."
}

Copy-Item (Join-Path $DotnetRoot "dotnet.exe") (Join-Path $Staging "runtime/dotnet") -Force
Copy-Item (Join-Path $DotnetRoot "host") (Join-Path $Staging "runtime/dotnet/host") -Recurse
Copy-Item (Join-Path $DotnetRoot "shared") (Join-Path $Staging "runtime/dotnet/shared") -Recurse
Copy-Item $PrivatePythonDirectory (Join-Path $Staging "runtime/python") -Recurse
Copy-Item $SqliteNativeDirectory (Join-Path $Staging "runtime/sqlite") -Recurse
foreach ($directory in @("config", "engine", "packs", "schemas")) {
    Copy-Item (Join-Path $RepoRoot $directory) (Join-Path $Staging $directory) -Recurse
}
foreach ($file in @("baselines.lock.json", "LICENSE", "NOTICE", "SECURITY.md")) {
    Copy-Item (Join-Path $RepoRoot $file) $Staging
}
New-Item -ItemType Directory -Force -Path (Join-Path $Staging "docs") | Out-Null
Copy-Item (Join-Path $RepoRoot "docs/v0.4") (Join-Path $Staging "docs/v0.4") -Recurse
Copy-Item (Join-Path $RepoRoot "docs/acceptance/USER_ACCEPTANCE_GUIDE.md") (Join-Path $Staging "docs/USER_ACCEPTANCE_GUIDE.md")
Copy-Item (Join-Path $PSScriptRoot "verify-v04-internal-package.py") (Join-Path $Staging "tools/verify-v04-internal-package.py")
Copy-Item (Join-Path $PSScriptRoot "generate-v04-internal-manifest.py") (Join-Path $Staging "tools/generate-v04-internal-manifest.py")

$CandidateIdentity = [ordered]@{
    contract = "fs-observer/package-identity/1"
    system_version = "v0.4.0-beta-internal-candidate"
    implementation_gate = "INTERNAL_CANDIDATE"
    maturity = "NO_GO_UNTIL_EXTERNAL_GATES"
    release_authorized = $false
    source_commit = $Commit
    engine_version = "v1.5.0"
}
[IO.File]::WriteAllText(
    (Join-Path $Staging "internal-candidate-identity.json"),
    ($CandidateIdentity | ConvertTo-Json -Depth 4) + "`n",
    (New-Object Text.UTF8Encoding($false)))

$Launcher = @'
@echo off
setlocal
set "ROOT=%~dp0"
set "FS_OBSERVER_PACKAGE_ROOT=%ROOT%"
set "FS_OBSERVER_PACKAGE_IDENTITY=%ROOT%internal-candidate-identity.json"
set "DOTNET_ROOT=%ROOT%runtime\dotnet"
set "FSP_PRIVATE_PYTHON=%ROOT%runtime\python\python.exe"
set "PYTHONNOUSERSITE=1"
set "PATH=%ROOT%runtime\sqlite;%ROOT%runtime\dotnet;%PATH%"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
pushd "%ROOT%"
"%ROOT%runtime\dotnet\dotnet.exe" "%ROOT%app\FullSpectrum.Observer.Host.Cli.dll" %*
set "CODE=%ERRORLEVEL%"
popd
exit /b %CODE%
'@
[IO.File]::WriteAllText((Join-Path $Staging "observer.cmd"), $Launcher, (New-Object Text.UTF8Encoding($false)))

$Python = Join-Path $PrivatePythonDirectory "python.exe"
$BuiltAtUtc = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", [Globalization.CultureInfo]::InvariantCulture)
& $Python (Join-Path $PSScriptRoot "generate-v04-internal-manifest.py") --package-root $Staging --commit $Commit --built-at-utc $BuiltAtUtc
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $Python (Join-Path $Staging "tools/verify-v04-internal-package.py") --package-root $Staging
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Compress-Archive -Path (Join-Path $Staging "*") -DestinationPath $Zip -CompressionLevel Optimal
$ZipHash = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$Zip.sha256", "$ZipHash *$([IO.Path]::GetFileName($Zip))`n", (New-Object Text.UTF8Encoding($false)))
Write-Host "v0.4 internal candidate package: $Zip"
Write-Host "SHA-256: $ZipHash"

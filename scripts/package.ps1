[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PrivatePythonDirectory,
    [Parameter(Mandatory=$true)][string]$SqliteNativeDirectory,
    [string]$DotnetRoot = $env:DOTNET_ROOT,
    [string]$OutputDirectory = "artifacts/ig7",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$PrivatePythonDirectory = (Resolve-Path $PrivatePythonDirectory).Path
$SqliteNativeDirectory = (Resolve-Path $SqliteNativeDirectory).Path
if ([string]::IsNullOrWhiteSpace($DotnetRoot)) {
    $DotnetCommand = Get-Command dotnet -ErrorAction Stop
    $DotnetRoot = Split-Path $DotnetCommand.Source
}
$DotnetRoot = (Resolve-Path $DotnetRoot).Path
$Python = Join-Path $PrivatePythonDirectory "python.exe"
$Sqlite = Join-Path $SqliteNativeDirectory "sqlite3.dll"
if (-not (Test-Path $Python -PathType Leaf)) { throw "Private Python executable is missing." }
if (-not (Test-Path $Sqlite -PathType Leaf)) { throw "Pinned sqlite3.dll is missing." }

$OutputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $RepoRoot $OutputDirectory }
$Staging = Join-Path $OutputRoot "full-spectrum-observer-v0.1.0-alpha-ig7"
$Zip = "$Staging.zip"
if (Test-Path $Staging) { Remove-Item $Staging -Recurse -Force }
if (Test-Path $Zip) { Remove-Item $Zip -Force }
New-Item -ItemType Directory -Force -Path $Staging,(Join-Path $Staging "app"),(Join-Path $Staging "runtime") | Out-Null

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:NUGET_PACKAGES = Join-Path $RepoRoot ".packages"
& dotnet publish (Join-Path $RepoRoot "src/Observer.Host.Cli/Observer.Host.Cli.csproj") `
    --configuration $Configuration --no-restore --self-contained false `
    --output (Join-Path $Staging "app")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$DotnetPackage = Join-Path $Staging "runtime/dotnet"
New-Item -ItemType Directory -Force -Path $DotnetPackage | Out-Null
Copy-Item (Join-Path $DotnetRoot "dotnet.exe") $DotnetPackage
Copy-Item (Join-Path $DotnetRoot "host") $DotnetPackage -Recurse
Copy-Item (Join-Path $DotnetRoot "shared") $DotnetPackage -Recurse
Copy-Item $PrivatePythonDirectory (Join-Path $Staging "runtime/python") -Recurse
New-Item -ItemType Directory -Force -Path (Join-Path $Staging "runtime/sqlite") | Out-Null
Copy-Item $Sqlite (Join-Path $Staging "runtime/sqlite/sqlite3.dll")

foreach ($Directory in @("engine","packs","schemas")) {
    Copy-Item (Join-Path $RepoRoot $Directory) (Join-Path $Staging $Directory) -Recurse
}
Copy-Item (Join-Path $RepoRoot "baselines.lock.json") $Staging
Copy-Item (Join-Path $RepoRoot "LICENSE") $Staging
Copy-Item (Join-Path $RepoRoot "NOTICE") $Staging
Copy-Item (Join-Path $RepoRoot "SECURITY.md") $Staging
New-Item -ItemType Directory -Force -Path (Join-Path $Staging "docs"),(Join-Path $Staging "tools") | Out-Null
Copy-Item (Join-Path $RepoRoot "docs/acceptance/USER_ACCEPTANCE_GUIDE.md") (Join-Path $Staging "docs/USER_ACCEPTANCE_GUIDE.md")
Copy-Item (Join-Path $RepoRoot "docs/testing/IG6_TEST_EXECUTION_REPORT.md") (Join-Path $Staging "docs/IG6_TEST_EXECUTION_REPORT.md")
Copy-Item (Join-Path $PSScriptRoot "verify-package.py") (Join-Path $Staging "tools/verify-package.py")

$Launcher = @'
@echo off
setlocal
set "ROOT=%~dp0"
set "DOTNET_ROOT=%ROOT%runtime\dotnet"
set "FSP_PRIVATE_PYTHON=%ROOT%runtime\python\python.exe"
set "PATH=%ROOT%runtime\sqlite;%ROOT%runtime\dotnet;%PATH%"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "PYTHONNOUSERSITE=1"
pushd "%ROOT%"
"%ROOT%runtime\dotnet\dotnet.exe" "%ROOT%app\FullSpectrum.Observer.Host.Cli.dll" %*
set "CODE=%ERRORLEVEL%"
popd
exit /b %CODE%
'@
[IO.File]::WriteAllText((Join-Path $Staging "observer.cmd"), $Launcher, (New-Object Text.UTF8Encoding($false)))

$Commit = (& git -C $RepoRoot rev-parse HEAD).Trim()
& $Python (Join-Path $PSScriptRoot "generate-release-metadata.py") `
    --package-root $Staging --release-commit $Commit
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Compress-Archive -Path (Join-Path $Staging "*") -DestinationPath $Zip -CompressionLevel Optimal
$ZipHash = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$Zip.sha256", "$ZipHash *$([IO.Path]::GetFileName($Zip))`n", (New-Object Text.UTF8Encoding($false)))
Write-Host "IG7 package: $Zip"
Write-Host "SHA-256: $ZipHash"

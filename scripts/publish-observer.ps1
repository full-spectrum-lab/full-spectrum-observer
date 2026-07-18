<#
.SYNOPSIS
    M2-RUN-01 formal, deterministic publish entry for the Observer `serve` product.

.DESCRIPTION
    Produces a self-contained, movable product directory from a fresh clone with ONE command:

        1. restore the solution (NuGetAudit default ON; NuGet.Config is <clear/> so an explicit feed is required)
        2. publish Observer.Host.Web  -> <OutputDirectory>/web
        3. publish Observer.Host.Cli  -> <OutputDirectory>  (product root)
        4. assemble CLI + Web + approved native SQLite runtime + config + dependencies
        5. assert web/Observer.Host.Web.exe exists; the publish FAILS (non-zero exit) if the
           Web artifact is missing, so a partial package is never produced.

    The CLI no longer ProjectReferences the Web host; the Web host is published separately into
    the product's `web/` subdirectory and `serve` resolves it via AppContext.BaseDirectory.
    No manual copy of the Web host or sqlite3.dll is required. A stale `web/` from a previous run
    is always removed first, so the composition is regenerated from source every time.

.EXAMPLE
    # from a fresh clone, any current working directory:
    $env:DOTNET_ROOT = "C:\Users\wangjian0926\.dotnet10"
    pwsh scripts/publish-observer.ps1 -OutputDirectory C:\tmp\publish\observer
    # then:
    cd C:\some\unrelated\dir
    & "C:\tmp\publish\observer\FullSpectrum.Observer.Host.Cli.exe" serve
#>
[CmdletBinding()]
param(
    [string]$DotnetRoot = $env:DOTNET_ROOT,
    [string]$OutputDirectory = "publish/observer",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$NuGetSource = "https://api.nuget.org/v3/index.json"
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# Resolve the dotnet host (prefer the isolated SDK from DOTNET_ROOT, else PATH).
if ([string]::IsNullOrWhiteSpace($DotnetRoot)) {
    $DotnetCommand = Get-Command dotnet -ErrorAction Stop
    $DotnetRoot = Split-Path $DotnetCommand.Source
}
$DotnetExe = Join-Path $DotnetRoot "dotnet.exe"
if (-not (Test-Path $DotnetExe -PathType Leaf)) { throw "dotnet not found at: $DotnetExe" }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

$OutputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $RepoRoot $OutputDirectory }
$WebOut = Join-Path $OutputRoot "web"
$CliOut = $OutputRoot

# Always regenerate from source. In delete-protected environments a hard Remove-Item can be
# intercepted and fail closed, so we MOVE any prior output aside (rename) instead of deleting.
# A fresh clone has no prior output, so this is a no-op there; either way the full product
# directory is always rebuilt from source (including a stale web/ subdir).
if (Test-Path $OutputRoot) {
    $stale = "$OutputRoot.stale." + (Get-Date -Format "yyyyMMddHHmmss")
    Move-Item -Path $OutputRoot -Destination $stale -Force
}
New-Item -ItemType Directory -Force -Path $CliOut, $WebOut | Out-Null

$Sln     = Join-Path $RepoRoot "FullSpectrum.Observer.sln"
$WebProj = Join-Path $RepoRoot "src/Observer.Host.Web/Observer.Host.Web.csproj"
$CliProj = Join-Path $RepoRoot "src/Observer.Host.Cli/Observer.Host.Cli.csproj"

Write-Host "=== [publish-observer] repo: $RepoRoot ==="
Write-Host "=== [publish-observer] dotnet: $DotnetExe ==="
Write-Host "=== [publish-observer] output: $OutputRoot ==="

Write-Host "=== Restore (NuGetAudit default ON; -r $Runtime so publish --no-restore has the RID asset target) ==="
& $DotnetExe restore $Sln -s $NuGetSource -r $Runtime
if ($LASTEXITCODE -ne 0) { throw "restore failed (exit $LASTEXITCODE)" }

Write-Host "=== Publish Web Host -> web/ ==="
& $DotnetExe publish $WebProj -c $Configuration -r $Runtime --no-restore -o $WebOut
if ($LASTEXITCODE -ne 0) { throw "Web host publish failed (exit $LASTEXITCODE)" }

Write-Host "=== Publish CLI -> product root ==="
& $DotnetExe publish $CliProj -c $Configuration -r $Runtime --no-restore -o $CliOut
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed (exit $LASTEXITCODE)" }

Write-Host "=== Validate composition ==="
$webExe  = Join-Path $WebOut  "Observer.Host.Web.exe"
$cliExe  = Join-Path $CliOut  "FullSpectrum.Observer.Host.Cli.exe"
$nativeCli = Join-Path $CliOut "e_sqlite3.dll"
$nativeWeb = Join-Path $WebOut "e_sqlite3.dll"

if (-not (Test-Path $webExe -PathType Leaf)) {
    throw "MISSING Web Host artifact: $webExe -- refusing to produce a partial package. Re-run this script; do not copy files manually."
}
if (-not (Test-Path $cliExe -PathType Leaf)) {
    throw "MISSING CLI artifact: $cliExe"
}
if (-not (Test-Path $nativeCli -PathType Leaf)) {
    throw "MISSING approved native SQLite runtime in CLI output: $nativeCli (publish did not deploy e_sqlite3.dll)"
}
if (-not (Test-Path $nativeWeb -PathType Leaf)) {
    throw "MISSING approved native SQLite runtime in Web output: $nativeWeb (publish did not deploy e_sqlite3.dll)"
}

Write-Host "=== Publish package complete ==="
Write-Host "  CLI    : $cliExe"
Write-Host "  Web    : $webExe"
Write-Host "  Native : $nativeCli"
Write-Host "  Native : $nativeWeb"
Write-Host "=== [publish-observer] OK ==="

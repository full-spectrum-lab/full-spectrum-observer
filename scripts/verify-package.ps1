[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PackagePath,
    [string]$VerificationPython = $env:FSP_PRIVATE_PYTHON,
    [string]$EvidencePath = "evidence/ig7/IG7_Result.json",
    [switch]$TamperTest
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$PackagePath = (Resolve-Path $PackagePath).Path
if ([string]::IsNullOrWhiteSpace($VerificationPython)) { throw "VerificationPython/FSP_PRIVATE_PYTHON is required." }
$VerificationPython = (Resolve-Path $VerificationPython).Path
$Temp = Join-Path ([IO.Path]::GetTempPath()) ("observer-ig7-verify-" + [Guid]::NewGuid().ToString("N"))
$Extract = Join-Path $Temp "package"
New-Item -ItemType Directory -Force -Path $Extract | Out-Null
try {
    Expand-Archive $PackagePath $Extract
    & $VerificationPython (Join-Path $PSScriptRoot "verify-package.py") --package-root $Extract
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $VersionOutput = & (Join-Path $Extract "observer.cmd") version --json
    if ($LASTEXITCODE -ne 0) { throw "Packaged observer version command failed." }
    $Data = Join-Path $Temp "data"
    $AnalyzeOutput = & (Join-Path $Extract "observer.cmd") analyze --case CASE005_KNOWLEDGE_CONFLICT --data-dir $Data --json
    if ($LASTEXITCODE -ne 0) { throw "Packaged observer analyze command failed." }
    $AuditOutput = & (Join-Path $Extract "observer.cmd") verify-audit --from 1 --data-dir $Data --json
    if ($LASTEXITCODE -ne 0) { throw "Packaged observer audit verification failed." }

    $TamperDetected = $null
    if ($TamperTest) {
        $Target = Join-Path $Extract "engine/worker/worker.py"
        Add-Content -LiteralPath $Target -Value "# tamper"
        & $VerificationPython (Join-Path $PSScriptRoot "verify-package.py") --package-root $Extract *> $null
        $TamperDetected = $LASTEXITCODE -ne 0
        if (-not $TamperDetected) { throw "Package tamper was not detected." }
    }

    $Report = [ordered]@{
        report_id = "IG7-PACKAGE-VERIFICATION-1"
        status = "PASS"
        formal_gate = "PASSED"
        package = [IO.Path]::GetFileName($PackagePath)
        package_sha256 = (Get-FileHash $PackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
        manifest_verified = $true
        sha256sums_verified = $true
        version_command = $true
        analyze_case005 = $true
        audit_verified = $true
        tamper_detected = $TamperDetected
        engine_version = "v1.0.0"
        engine_1_4_supported = $false
    }
    $Destination = if ([IO.Path]::IsPathRooted($EvidencePath)) { $EvidencePath } else { Join-Path $RepoRoot $EvidencePath }
    New-Item -ItemType Directory -Force -Path (Split-Path $Destination) | Out-Null
    [IO.File]::WriteAllText($Destination, ($Report | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    $Report | ConvertTo-Json -Depth 8
}
finally {
    if (Test-Path $Temp) { Remove-Item $Temp -Recurse -Force }
}


[CmdletBinding()]
param(
    [string]$OutputPath = "evidence/ig0/baseline-verify.json"
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$LockPath = Join-Path $RepoRoot "baselines.lock.json"
$Lock = Get-Content -Raw -Encoding UTF8 -LiteralPath $LockPath | ConvertFrom-Json

$Results = @()
$Passed = $true

foreach ($File in $Lock.files) {
    $FullPath = Join-Path $RepoRoot ($File.path -replace "/", [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $FullPath -PathType Leaf)) {
        $Results += [ordered]@{
            path = $File.path
            status = "MISSING"
            expected_sha256 = $File.sha256
            actual_sha256 = $null
            expected_size = $File.size_bytes
            actual_size = $null
        }
        $Passed = $false
        continue
    }

    $Item = Get-Item -LiteralPath $FullPath
    $ActualHash = (Get-FileHash -LiteralPath $FullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $Status = if (($ActualHash -eq $File.sha256) -and ($Item.Length -eq $File.size_bytes)) {
        "PASS"
    } else {
        "MISMATCH"
    }

    if ($Status -ne "PASS") {
        $Passed = $false
    }

    $Results += [ordered]@{
        path = $File.path
        status = $Status
        expected_sha256 = $File.sha256
        actual_sha256 = $ActualHash
        expected_size = $File.size_bytes
        actual_size = $Item.Length
    }
}

$GlobalJson = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $RepoRoot "global.json") | ConvertFrom-Json
$SdkStatus = if ($GlobalJson.sdk.version -eq $Lock.sdk.version -and
                 $GlobalJson.sdk.rollForward -eq "disable") { "PASS" } else { "FAIL" }
if ($SdkStatus -ne "PASS") { $Passed = $false }

$Report = [ordered]@{
    report_id = "EVD-IMP-001"
    gate = "IG0"
    status = if ($Passed) { "PASS" } else { "FAIL" }
    generated_at_utc = [DateTimeOffset]::UtcNow.ToString("o")
    lock_id = $Lock.lock_id
    sdk_status = $SdkStatus
    engine = $Lock.engine
    checked_files = $Results.Count
    pass_count = @($Results | Where-Object status -eq "PASS").Count
    failure_count = @($Results | Where-Object status -ne "PASS").Count
    files = $Results
}

$Destination = Join-Path $RepoRoot ($OutputPath -replace "/", [IO.Path]::DirectorySeparatorChar)
New-Item -ItemType Directory -Path (Split-Path $Destination) -Force | Out-Null
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($Destination, ($Report | ConvertTo-Json -Depth 8), $Utf8NoBom)

Write-Host "IG0 baseline verification: $($Report.status)"
Write-Host "Files: $($Report.pass_count)/$($Report.checked_files) PASS"
if (-not $Passed) { exit 1 }

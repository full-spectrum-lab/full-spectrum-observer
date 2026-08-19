<#
.SYNOPSIS
    Data-directory isolation verification for the Observer Console (plan §3.3 + §5.2).

.DESCRIPTION
    Proves the Launcher propagation chain: a published, UNCOMPRESSED CLI Launcher started with
    `serve --data-dir <isolated>` actually opens `<isolated>/observer_console.db` and exposes that
    path on `/system`, while the DEFAULT per-user data directory (`%LOCALAPPDATA%/full-spectrum-observer/data`)
    is left completely unchanged.

    The verification runs a DUAL CYCLE (S0 -> S6):
      S0  snapshot the default data directory (existence / SHA256 / mtime / per-table row counts)
      S1  prepare a fresh absolute isolated directory under %TEMP%
      S2  first  `serve --data-dir $ISO` (async) -> assert <ISO>/observer_console.db created + schema present
      S3  offline seed <ISO>/observer_console.db with one Active + one Draft + one Retired subject version
      S4  second `serve --data-dir $ISO` -> fetch /system and /new-analysis prerendered HTML and assert
      S5  snapshot the default data directory again; compare with S0 (must be identical)
      S6  assert no residual Launcher/Web-Host processes and the instance lock is released

    EVIDENCE FLAGS (split, ALWAYS written to datadir-evidence.json):
      DATA_DIR_RESOLUTION             = PASS  (isolated db created; /system shows <ISO>)            [hard assertion]
      DEFAULT_DIR_UNCHANGED           = PASS  (default dir SHA256 + mtime + row counts identical before/after)
      NEW_ANALYSIS_RENDER             = PASS  (only when /new-analysis returns 200 AND the Active-only filter holds)
      WEB_UI_HBG                      = PASS  (mirrors NEW_ANALYSIS_RENDER; FAIL when /new-analysis != 200)
      WEB_UI_DATA_WRITE_TO_ISOLATED_DB = NOT_PROVEN  (no browser interaction / CLI analyze authorized this round)
      FULL_VALIDATION                 = INCOMPLETE
      OVERALL_STATUS                  = PASS only if ALL of the above are PASS
    /new-analysis returning non-200 is a HARD FAIL: it is recorded as FAIL_HTTP_500 and is NEVER
    downgraded to PASS by other sub-items passing. ROOT_CAUSE stays NOT_PROVEN (this script alone
    does not confirm a product defect versus a seed-fixture defect).

    PREREQUISITES:
      * A published, UNCOMPRESSED layout produced by `scripts/publish-observer.ps1` (run WITHOUT
        -Release). The layout must contain `FullSpectrum.Observer.Host.Cli.exe` at its root plus a
        `web/` subfolder and a bundled `runtime/dotnet/dotnet.exe`.
      * No other Observer Console instance may be running on this machine: the single-instance lock
        uses a machine-global mutex (Global\FullSpectrum.Observer.Console) independent of the data
        directory, so a concurrently running instance would make `serve` refuse to start.

    HOW TO RUN (from the published layout's parent, or pass the exe explicitly):
        # from any directory, pointing at the published CLI:
        pwsh scripts/verify-data-dir-isolation.ps1 `
            -LauncherExe "C:\obs-verify-publish\FullSpectrum.Observer.Host.Cli.exe" `
            -ResultsDir "C:\obs-verify-publish\evidence"

        # or rely on $env:OBS_PUBLISH_ROOT:
        $env:OBS_PUBLISH_ROOT = "C:\obs-verify-publish"
        pwsh scripts/verify-data-dir-isolation.ps1

    EVIDENCE WRITTEN TO -ResultsDir:
        datadir-before.json / datadir-after.json  (default-dir snapshots)
        datadir-evidence.json                     (process PIDs, ports, log paths, lock path, mechanism)
        system-page.html / new-analysis-page.html (prerendered HTML captures)
        host-first-stdout.log / host-second-stdout.log (Launcher+Web Host stdout)

    SQLITE MECHANISM (plan §5, no new packages): the script auto-selects the simplest available
    mechanism and records which one was used in datadir-evidence.json (`sqlite_mechanism`):
      1) `sqlite3.exe` found on PATH, in $env:FSP_SQLITE_NATIVE_DIR, in the published layout's
         `<publish-root>/runtime/sqlite/` directory, or in the repo `<repo>/.runtime/sqlite/` directory;
      2) otherwise it loads `Microsoft.Data.Sqlite.dll` (shipped inside the published layout,
         alongside its native `e_sqlite3.dll`) via PowerShell `Add-Type -Path` and runs the
         INSERT / COUNT statements through `Microsoft.Data.Sqlite.SqliteConnection`.
    On the development machine used for this implementation, `sqlite3.exe` is placed in the
    published layout's `<publish-root>/runtime/sqlite/` directory (downloaded from sqlite.org), so
    mechanism (1) is the one exercised here; mechanism (2) remains available as a fallback.
#>
[CmdletBinding()]
param(
    # Absolute path to the published, uncompressed CLI Launcher exe. Resolved from
    # $env:OBS_PUBLISH_ROOT or a couple of conventional locations when omitted.
    [string]$LauncherExe = "",
    # Directory where evidence files are written (defaults to the current directory).
    [string]$ResultsDir = ""
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Resolve the Launcher exe (must come from an EXTERNAL uncompressed publish layout)
# ---------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($LauncherExe) -and $env:OBS_PUBLISH_ROOT) {
    $LauncherExe = Join-Path $env:OBS_PUBLISH_ROOT "FullSpectrum.Observer.Host.Cli.exe"
}
if ([string]::IsNullOrWhiteSpace($LauncherExe)) {
    $candidates = @(
        "C:\obs-verify-publish\FullSpectrum.Observer.Host.Cli.exe",
        (Join-Path $PSScriptRoot "..\publish\observer\FullSpectrum.Observer.Host.Cli.exe")
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c -PathType Leaf) { $LauncherExe = (Resolve-Path $c).Path; break }
    }
}
if (-not (Test-Path -LiteralPath $LauncherExe -PathType Leaf)) {
    throw "LauncherExe not found. Pass -LauncherExe '<publish-root>\FullSpectrum.Observer.Host.Cli.exe' " +
          "pointing at the UNCOMPRESSED published layout produced by publish-observer.ps1 (without -Release)."
}
$script:LauncherExe = $LauncherExe
$script:PublishRoot = Split-Path $LauncherExe

if ([string]::IsNullOrWhiteSpace($ResultsDir)) { $ResultsDir = $PWD.Path }
$script:ResultsDir = $ResultsDir
New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------
$script:ISO = Join-Path $env:TEMP ("obs-iso-" + (Get-Date -Format yyyyMMddHHmmss))
$script:DefaultDataDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "full-spectrum-observer\data"
$script:READY_TIMEOUT_MS = 30000
$script:CHILD_TIMEOUT_MS  = 15000
$script:LOCK_WAIT_MS      = 15000

Write-Host "=== [verify-data-dir-isolation] ==="
Write-Host "Launcher : $($script:LauncherExe)"
Write-Host "ISO dir  : $($script:ISO)"
Write-Host "Default  : $($script:DefaultDataDir)"

# ---------------------------------------------------------------------------
# sqlite mechanism resolution (plan §5: no new packages)
# ---------------------------------------------------------------------------
$script:SqliteCli = $null
$script:SqliteAsmPath = $null
$script:SqliteAsmLoaded = $false
$script:SqliteMechanism = $null

function Resolve-SqliteMechanism {
    # 1) sqlite3.exe from PATH / FSP_SQLITE_NATIVE_DIR / published layout runtime\sqlite / repo .runtime\sqlite
    $cliCandidates = @()
    if ($env:FSP_SQLITE_NATIVE_DIR) { $cliCandidates += Join-Path $env:FSP_SQLITE_NATIVE_DIR "sqlite3.exe" }
    if ($script:PublishRoot) {
        $cliCandidates += Join-Path $script:PublishRoot "runtime\sqlite\sqlite3.exe"
        $cliCandidates += Join-Path $script:PublishRoot "sqlite3.exe"
    }
    $cliCandidates += Join-Path (Join-Path $PSScriptRoot "..") ".runtime\sqlite\sqlite3.exe"
    $cliCandidates += Join-Path $PSScriptRoot "..\.runtime\sqlite\sqlite3.exe"
    foreach ($c in $cliCandidates) {
        if (Test-Path -LiteralPath $c -PathType Leaf) {
            $script:SqliteCli = (Resolve-Path $c).Path
            $script:SqliteMechanism = "sqlite3.exe ($c)"
            return
        }
    }
    try {
        $cmd = Get-Command sqlite3.exe -ErrorAction SilentlyContinue
        if ($cmd) {
            $script:SqliteCli = $cmd.Source
            $script:SqliteMechanism = "sqlite3.exe (PATH)"
            return
        }
    } catch { }

    # 2) Microsoft.Data.Sqlite.dll shipped inside the published layout
    $sqliteDllRoot = Join-Path $script:PublishRoot "Microsoft.Data.Sqlite.dll"
    $sqliteDllWeb  = Join-Path $script:PublishRoot "web\Microsoft.Data.Sqlite.dll"
    $asmCandidates = @($sqliteDllRoot, $sqliteDllWeb)
    foreach ($c in $asmCandidates) {
        if (Test-Path -LiteralPath $c -PathType Leaf) {
            $script:SqliteAsmPath = (Resolve-Path $c).Path
            $script:SqliteMechanism = "Microsoft.Data.Sqlite.dll ($c)"
            return
        }
    }
    throw "No sqlite mechanism available: sqlite3.exe not found and Microsoft.Data.Sqlite.dll not resolved from the publish layout."
}

function Invoke-SqliteNonQuery {
    param([string]$DbPath, [string]$Sql)
    if ($script:SqliteCli) {
        & $script:SqliteCli $DbPath $Sql 2>$null
        if ($LASTEXITCODE -ne 0) { throw "sqlite3 failed (exit $LASTEXITCODE) on '$DbPath': $Sql" }
        return
    }
    if (-not $script:SqliteAsmLoaded) { Add-Type -Path $script:SqliteAsmPath; $script:SqliteAsmLoaded = $true }
    $conn = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$DbPath")
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand(); $cmd.CommandText = $Sql
        [void]$cmd.ExecuteNonQuery()
    } finally {
        $conn.Close()
    }
}

function Get-DbRowCounts {
    param([string]$DbPath)
    $tables = @('subjects','subject_versions','knowledge_sources','knowledge_source_versions',
                'analysis_tasks','analysis_results','runtime_snapshots','evidence_bundles',
                'conflict_observations','audit_records')
    $result = [ordered]@{}
    if ($script:SqliteCli) {
        foreach ($t in $tables) {
            $out = & $script:SqliteCli $DbPath "SELECT COUNT(*) FROM $t;" 2>$null
            $result[$t] = [int]($out -join '')
        }
        return $result
    }
    if (-not $script:SqliteAsmLoaded) { Add-Type -Path $script:SqliteAsmPath; $script:SqliteAsmLoaded = $true }
    $conn = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$DbPath")
    try {
        $conn.Open()
        foreach ($t in $tables) {
            $cmd = $conn.CreateCommand(); $cmd.CommandText = "SELECT COUNT(*) FROM $t;"
            $result[$t] = [int]$cmd.ExecuteScalar()
        }
    } finally {
        $conn.Close()
    }
    return $result
}

function Get-DbTableList {
    param([string]$DbPath)
    if ($script:SqliteCli) {
        $out = & $script:SqliteCli $DbPath "SELECT name FROM sqlite_master WHERE type='table';" 2>$null
        return @($out)
    }
    if (-not $script:SqliteAsmLoaded) { Add-Type -Path $script:SqliteAsmPath; $script:SqliteAsmLoaded = $true }
    $conn = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$DbPath")
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand(); $cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';"
        $list = @()
        $rdr = $cmd.ExecuteReader()
        while ($rdr.Read()) { $list += $rdr.GetString(0) }
        $rdr.Dispose()
        return $list
    } finally {
        $conn.Close()
    }
}

# ---------------------------------------------------------------------------
# Serve-cycle orchestration (plan §5.2, with function-level cleanup)
# ---------------------------------------------------------------------------
function Stop-ServeCycleInternal {
    param([int]$LauncherPid, [int]$WebPid)
    if ($WebPid -and (Get-Process -Id $WebPid -ErrorAction SilentlyContinue)) {
        try { Stop-Process -Id $WebPid -Force -ErrorAction SilentlyContinue } catch { }
    }
    $p = Get-Process -Id $LauncherPid -ErrorAction SilentlyContinue
    if ($p) { $null = $p.WaitForExit(15000) }
    if (Get-Process -Id $LauncherPid -ErrorAction SilentlyContinue) {
        try { Stop-Process -Id $LauncherPid -Force -ErrorAction SilentlyContinue } catch { }
    }
}

function Start-ServeCycle {
    param([string]$IsoDir, [string]$StdoutFile = 'host-stdout.log')
    # Launch the Launcher asynchronously; it spawns a Web Host child (dotnet ... Observer.Host.Web.dll
    # --urls http://127.0.0.1:<port> ...) and sets OBSERVER_DATA_DIRECTORY to $IsoDir.
    $p = Start-Process -FilePath $script:LauncherExe -ArgumentList "serve","--data-dir",$IsoDir `
        -PassThru -RedirectStandardOutput $StdoutFile -NoNewWindow
    $launcherPid = $p.Id
    $webPid = 0
    try {
        # Locate the Web Host child process deterministically (no race).
        $web = $null
        $childDeadline = [datetime]::Now.AddMilliseconds($script:CHILD_TIMEOUT_MS)
        while ([datetime]::Now -lt $childDeadline) {
            if (-not (Get-Process -Id $launcherPid -ErrorAction SilentlyContinue)) {
                throw "Launcher $launcherPid exited early (Web Host not started). See $StdoutFile"
            }
            $web = Get-CimInstance Win32_Process -Filter "Name LIKE '%dotnet%'" |
                   Where-Object { $_.ParentProcessId -eq $launcherPid -and $_.CommandLine -like '*Observer.Host.Web.dll*' } |
                   Select-Object -First 1
            if ($web) { $webPid = $web.ProcessId; break }
            Start-Sleep -Milliseconds 300
        }
        if (-not $web) {
            throw "Timed out locating Web Host child (ParentProcessId=$launcherPid, CommandLine contains Observer.Host.Web.dll)"
        }
        # Parse the actual port the Launcher chose (random loopback port; no hardcoded placeholder).
        $port = [regex]::Match($web.CommandLine, '(?:--urls\s+)?http://127\.0\.0\.1:(\d+)').Groups[1].Value
        if (-not $port) {
            throw "Could not parse --urls port from Web Host command line: $($web.CommandLine)"
        }
        # Poll /system for readiness (200) within the timeout; any failure throws (no false pass).
        $ready = $false
        $readyDeadline = [datetime]::Now.AddMilliseconds($script:READY_TIMEOUT_MS)
        while ([datetime]::Now -lt $readyDeadline) {
            try {
                $r = Invoke-WebRequest "http://127.0.0.1:$port/system" -UseBasicParsing -TimeoutSec 2 -ErrorAction SilentlyContinue
                if ($r.StatusCode -eq 200) { $ready = $true; break }
            } catch { }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) {
            throw "Host readiness timeout (port $port not returning 200 within $($script:READY_TIMEOUT_MS) ms)"
        }
        return [pscustomobject]@{ LauncherPid = $launcherPid; WebPid = $webPid; Port = $port }
    } catch {
        # Function-level cleanup so we never leak a process even before the caller holds the context.
        Stop-ServeCycleInternal $launcherPid $webPid
        throw
    }
}

function Stop-ServeCycle { param($ctx); if ($ctx) { Stop-ServeCycleInternal $ctx.LauncherPid $ctx.WebPid } }

function Wait-InstanceLockReleased {
    param([string]$IsoDir, [int]$TimeoutMs = 15000)
    $lock = Join-Path $IsoDir ".observer-instance.lock"
    $deadline = [datetime]::Now.AddMilliseconds($TimeoutMs)
    while ([datetime]::Now -lt $deadline) {
        if (-not (Test-Path -LiteralPath $lock)) { return $true }
        try {
            $fs = [System.IO.File]::Open($lock, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            $fs.Close()
            return $true
        } catch [System.IO.IOException] {
            Start-Sleep -Milliseconds 300
        }
    }
    throw "INSTANCE_LOCK_NOT_RELEASED within ${TimeoutMs} ms: $lock"
}

# ---------------------------------------------------------------------------
# Default-dir snapshot + invariance
# ---------------------------------------------------------------------------
function Get-DefaultDirSnapshot {
    param([string]$DefaultDataDir)
    $db = Join-Path $DefaultDataDir "observer_console.db"
    $snap = [ordered]@{}
    $snap.Exists = Test-Path -LiteralPath $db -PathType Leaf
    if ($snap.Exists) {
        $snap.Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $db).Hash
        $snap.LastWriteTimeUtc = (Get-Item -LiteralPath $db).LastWriteTimeUtc.ToString("O")
        try { $snap.RowCounts = Get-DbRowCounts -DbPath $db } catch { $snap.RowCounts = "UNREADABLE" }
    } else {
        $snap.Sha256 = $null
        $snap.LastWriteTimeUtc = $null
        $snap.RowCounts = $null
    }
    return $snap
}

function Assert-DefaultDirInvariance {
    param($Before, $After)
    if ($Before.Exists -ne $After.Exists) {
        throw "DEFAULT_DIR_CHANGED: existence of observer_console.db changed (before=$($Before.Exists), after=$($After.Exists))"
    }
    if ($Before.Exists) {
        if ($Before.Sha256 -ne $After.Sha256) { throw "DEFAULT_DIR_CHANGED: SHA256 of observer_console.db differs" }
        if ($Before.LastWriteTimeUtc -ne $After.LastWriteTimeUtc) { throw "DEFAULT_DIR_CHANGED: LastWriteTimeUtc of observer_console.db differs" }
        $b = $Before.RowCounts; $a = $After.RowCounts
        if ($b -is [string] -or $a -is [string]) {
            Write-Warning "Row-count comparison skipped (unreadable): before='$b' after='$a'"
        } else {
            foreach ($k in $b.Keys) {
                if ([int]$b[$k] -ne [int]$a[$k]) {
                    throw "DEFAULT_DIR_CHANGED: row count for '$k' differs (before=$($b[$k]), after=$($a[$k]))"
                }
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Offline seed of the isolated DB (S3) — one Active + one Draft + one Retired subject version
# (only the Active version is expected to surface on /new-analysis).
# ---------------------------------------------------------------------------
function Seed-IsoDatabase {
    param([string]$IsoDir)
    $db = Join-Path $IsoDir "observer_console.db"
    $now = [DateTime]::UtcNow.ToString("O")
    $stmts = @(
        "INSERT INTO subjects (local_subject_id, subject_type, mode, concentration_tier, created_at) VALUES ('S-AO-ISO','PERSON','OBSERVE',NULL,'$now');",
        "INSERT INTO subject_versions (version_id, subject_id, status, seq, payload, schema_version, created_at, active_from, retired_at) VALUES ('SV-AO-ACTIVE','S-AO-ISO','Active',1,'{}','1.0.0','$now','$now',NULL);",
        "INSERT INTO subject_versions (version_id, subject_id, status, seq, payload, schema_version, created_at, active_from, retired_at) VALUES ('SV-AO-DRAFT','S-AO-ISO','Draft',2,'{}','1.0.0','$now',NULL,NULL);",
        "INSERT INTO subject_versions (version_id, subject_id, status, seq, payload, schema_version, created_at, active_from, retired_at) VALUES ('SV-AO-RETIRED','S-AO-ISO','Retired',3,'{}','1.0.0','$now',NULL,'$now');"
    )
    foreach ($s in $stmts) { Invoke-SqliteNonQuery -DbPath $db -Sql $s }
}

# ---------------------------------------------------------------------------
# Main dual-cycle (S0 -> S6). All catch/finally blocks RE-THROW the original exception; a cleanup
# success is NEVER turned into a test pass.
# ---------------------------------------------------------------------------
Resolve-SqliteMechanism
Write-Host "sqlite mechanism: $($script:SqliteMechanism)"

# S0: default-dir BEFORE snapshot (surrounds the whole dual cycle)
$before = Get-DefaultDirSnapshot -DefaultDataDir $script:DefaultDataDir
$before | ConvertTo-Json | Set-Content -Path (Join-Path $script:ResultsDir "datadir-before.json") -Encoding utf8

$first = $null
$second = $null
try {
    # S1: fresh absolute isolated directory (relative paths are rejected by ObserverDataDirectory)
    New-Item -ItemType Directory -Force -Path $script:ISO | Out-Null

    # S2: first serve -> create the isolated DB + schema
    try {
        $first = Start-ServeCycle $script:ISO -StdoutFile (Join-Path $script:ResultsDir "host-first-stdout.log")
        $isoDb = Join-Path $script:ISO "observer_console.db"
        if (-not (Test-Path -LiteralPath $isoDb -PathType Leaf)) {
            throw "ISO db not created after first serve: $isoDb"
        }
        $tables = Get-DbTableList -DbPath $isoDb
        if ($tables -notcontains 'subject_versions' -or $tables -notcontains 'analysis_tasks') {
            throw "Expected tables missing in ISO db: $(($tables | Out-String).Trim())"
        }
        Write-Host "S2 OK: isolated db created with schema at $isoDb"
    } finally {
        if ($first) { Stop-ServeCycle $first }
    }
    # Ensure the instance lock is released before the next cycle re-acquires it.
    Wait-InstanceLockReleased -IsoDir $script:ISO

    # S3: offline seed (host fully stopped; lock released)
    Seed-IsoDatabase -IsoDir $script:ISO
    Write-Host "S3 OK: seeded Active/Draft/Retired subject versions into $isoDb"

    # S4: second serve -> capture /system and /new-analysis prerendered HTML
    try {
        $second = Start-ServeCycle $script:ISO -StdoutFile (Join-Path $script:ResultsDir "host-second-stdout.log")
        $port = $second.Port

        # /system is a HARD assertion for DATA_DIR_RESOLUTION (it passed in prior runs).
        $sys = Invoke-WebRequest "http://127.0.0.1:$port/system" -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
        $sys.Content | Out-File -Encoding utf8 (Join-Path $script:ResultsDir "system-page.html")
        if ($sys.Content -notlike "*$($script:ISO)*") {
            throw "/system page does not contain the isolated data directory path '$($script:ISO)' (DATA_DIR_RESOLUTION failed)"
        }
        $script:DataDirResolution = "PASS"
        Write-Host "S4a OK: /system shows <ISO>"

        # /new-analysis is the CORE assertion of this script (Active-only UI render).
        # A non-200 is a HARD FAILURE: recorded as FAIL and MUST NOT be downgraded to PASS,
        # but we capture the raw response body and CONTINUE to S5/S6 so the full evidence set
        # (including the failure) is always produced.
        try {
            $na = $null
            try {
                $na = Invoke-WebRequest "http://127.0.0.1:$port/new-analysis" -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
            } catch {
                # 4xx/5xx: capture the RAW response body from the WebException (honest evidence).
                $resp = $_.Exception.Response
                if ($resp) {
                    $script:NewAnalysisHttp = [int]$resp.StatusCode
                    $origMsg = $_.Exception.Message
                    $body = ""
                    try {
                        $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
                        $body = $sr.ReadToEnd(); $sr.Close()
                    } catch { }
                    if ([string]::IsNullOrWhiteSpace($body)) {
                        # Production configuration returns a bare 500 (no dev exception page).
                        $body = "HTTP $($script:NewAnalysisHttp) from /new-analysis`nOriginal client exception: $origMsg`n(Body empty: server returned a bare 500 in production configuration; enable ASPNETCORE_DETAILEDERRORS=1 + ASPNETCORE_ENVIRONMENT=Development to capture the server-side stack.)"
                    }
                    $body | Out-File -Encoding utf8 (Join-Path $script:ResultsDir "new-analysis-page.html")
                    $script:NewAnalysisError = "HTTP $($script:NewAnalysisHttp) from /new-analysis :: $origMsg"
                    throw $script:NewAnalysisError
                }
                throw
            }
            if ($na) {
                $script:NewAnalysisHttp = $na.StatusCode
                $na.Content | Out-File -Encoding utf8 (Join-Path $script:ResultsDir "new-analysis-page.html")
            }
            if ($script:NewAnalysisHttp -ne 200) { throw "HTTP $($script:NewAnalysisHttp) from /new-analysis" }
            if ($na.Content -notlike "*SV-AO-ACTIVE*")   { throw "/new-analysis does not list the Active version SV-AO-ACTIVE (filter broken)" }
            if ($na.Content -like  "*SV-AO-DRAFT*")      { throw "/new-analysis incorrectly lists the Draft version SV-AO-DRAFT (Active-only UI filter failed)" }
            if ($na.Content -like  "*SV-AO-RETIRED*")    { throw "/new-analysis incorrectly lists the Retired version SV-AO-RETIRED (Active-only UI filter failed)" }
            $script:NewAnalysisRender = "PASS"
            Write-Host "S4b OK: /new-analysis shows Active, hides Draft/Retired"
        } catch {
            # Hard failure recorded; DO NOT rethrow -> S5/S6 continue (evidence still written).
            $script:NewAnalysisRender = "FAIL_HTTP_500"
            $script:NewAnalysisFailed = $true
            $script:NewAnalysisError  = $_.Exception.Message
            Write-Host "S4b FAIL: /new-analysis -> $($script:NewAnalysisHttp): $($_.Exception.Message)"
        }
    } finally {
        if ($second) { Stop-ServeCycle $second }
    }

    # S5: default-dir AFTER snapshot + compare with S0
    $after = Get-DefaultDirSnapshot -DefaultDataDir $script:DefaultDataDir
    $after | ConvertTo-Json | Set-Content -Path (Join-Path $script:ResultsDir "datadir-after.json") -Encoding utf8
    Assert-DefaultDirInvariance $before $after
    $script:DefaultDirUnchanged = "PASS"
    Write-Host "S5 OK: default data directory unchanged (SHA256 + mtime + row counts identical)"

    # S6: no residual processes + instance lock released
    # NOTE: use $processId, NOT $pid -- $PID is a read-only automatic variable (case-insensitive),
    # so `foreach ($pid in ...)` would throw "Cannot overwrite variable PID".
    foreach ($processId in @($first.LauncherPid, $first.WebPid, $second.LauncherPid, $second.WebPid)) {
        if ($processId -and (Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
            throw "RESIDUAL_PROCESS: PID $processId still alive after both cycles"
        }
    }
    $lock = Join-Path $script:ISO ".observer-instance.lock"
    if (Test-Path -LiteralPath $lock) {
        try {
            $fs = [System.IO.File]::Open($lock, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            $fs.Close()   # openable => lock already released (handle closed; file may remain, not a false positive)
        } catch [System.IO.IOException] {
            throw "INSTANCE_LOCK_STILL_HELD: $lock could not be opened with FileShare.None"
        }
    }
    Write-Host "S6 OK: no residual processes; instance lock released"

    # S5/S6 completed above. All phase results are tracked in script-scoped variables and
    # finalized in the `finally` block below, which ALWAYS writes the split-flag evidence.
} catch {
    # Best-effort cleanup so we never leave a host running; the finally block still writes evidence.
    if ($first)  { try { Stop-ServeCycle $first }  catch { } }
    if ($second) { try { Stop-ServeCycle $second } catch { } }
    Write-Error "DATA_DIR_ISOLATION: FAIL - $($_.Exception.Message)"
} finally {
    # ---- Always emit split-flag evidence, even on a hard failure or /new-analysis 500 ----
    # A /new-analysis non-200 is a HARD FAIL: recorded as FAIL and NEVER downgraded to PASS by
    # S5/S6 success or by cleanup. ROOT_CAUSE stays NOT_PROVEN (this script alone does not
    # confirm a product defect versus a seed-fixture defect).
    $ov = "FAIL"
    if ($script:DataDirResolution -eq "PASS" -and $script:DefaultDirUnchanged -eq "PASS" -and $script:NewAnalysisRender -eq "PASS") { $ov = "PASS" }
    $ev = [ordered]@{
        status             = $ov
        sqlite_mechanism   = $script:SqliteMechanism
        launcher_exe       = $script:LauncherExe
        iso_dir            = $script:ISO
        default_data_dir   = $script:DefaultDataDir
        new_analysis_http  = $script:NewAnalysisHttp
        new_analysis_error = $script:NewAnalysisError
        root_cause         = "NOT_PROVEN"
        product_defect     = "NOT_YET_CONFIRMED"
        seed_fixture_defect= "NOT_YET_CONFIRMED"
        first_cycle  = if ($first)  { @{ launcher_pid = $first.LauncherPid;  web_pid = $first.WebPid;  port = $first.Port;  stdout = (Join-Path $script:ResultsDir "host-first-stdout.log") } }  else { $null }
        second_cycle = if ($second) { @{ launcher_pid = $second.LauncherPid; web_pid = $second.WebPid; port = $second.Port; stdout = (Join-Path $script:ResultsDir "host-second-stdout.log") } } else { $null }
        lock_file          = (Join-Path $script:ISO ".observer-instance.lock")
        system_page        = (Join-Path $script:ResultsDir "system-page.html")
        new_analysis_page  = (Join-Path $script:ResultsDir "new-analysis-page.html")
        flags = [ordered]@{
            DATA_DIR_RESOLUTION             = if ($script:DataDirResolution)         { $script:DataDirResolution }         else { "NOT_COMPLETED" }
            DEFAULT_DIR_UNCHANGED           = if ($script:DefaultDirUnchanged)       { $script:DefaultDirUnchanged }       else { "NOT_COMPLETED" }
            NEW_ANALYSIS_RENDER             = if ($script:NewAnalysisRender)         { $script:NewAnalysisRender }         else { "NOT_COMPLETED" }
            WEB_UI_HBG                      = if ($script:NewAnalysisRender -eq "PASS") { "PASS" } else { "FAIL" }
            WEB_UI_DATA_WRITE_TO_ISOLATED_DB = "NOT_PROVEN"
            FULL_VALIDATION                 = "INCOMPLETE"
            OVERALL_STATUS                  = $ov
        }
    }
    if ($script:SqliteCli -and (Test-Path -LiteralPath $script:SqliteCli -PathType Leaf)) {
        $ev.sqlite3_cli = [ordered]@{
            path   = $script:SqliteCli
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $script:SqliteCli).Hash
            source = "https://www.sqlite.org/2024/sqlite-tools-win-x64-3460100.zip"
            note   = "Out-of-repo verification artifact; must NOT be added to the product repository."
        }
    }
    try {
        $ev | ConvertTo-Json | Set-Content -Path (Join-Path $script:ResultsDir "datadir-evidence.json") -Encoding utf8
    } catch { Write-Warning "Failed to write datadir-evidence.json: $_" }
    if ($ov -eq "PASS") { Write-Host "=== DATA_DIR_ISOLATION: PASS ===" }
    else                { Write-Error "=== DATA_DIR_ISOLATION: FAIL ===  NEW_ANALYSIS_HTTP=$($script:NewAnalysisHttp)  DETAIL=$($script:NewAnalysisError)" }
    Write-Host "evidence: $(Join-Path $script:ResultsDir 'datadir-evidence.json')"
    if ($ov -ne "PASS") { exit 1 }
}

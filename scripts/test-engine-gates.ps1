<#
.SYNOPSIS
    Positive + negative + Gate-3 regression tests for the M2-ENG-01 dual-digest release gates.

.DESCRIPTION
    -Default (no switch): runs Test-EngineReleaseGates against the real repo (expect PASS).
    -Negative: copies engine/ to a temp dir, mutates one vendored file (appends a newline +
       comment via Set-Content), runs the gate (expect FAIL, caught by Gate 4), then cleans up.
       Proves the gate is sensitive, not a no-op.
    -EolCanonical: copies engine/ to a temp dir, asserts a traceable text file's on-disk raw
       SHA-256 differs from its ZIP entry raw SHA-256 (proving EOL divergence exists) AND that
       Test-EngineReleaseGates still PASSES (Gate 3 text_lf_canonical normalizes CRLF<->LF).
    -NonNewlineMutation: copies engine/ to a temp dir, flips one NON-newline byte of a traceable
       text file (byte-exact WriteAllBytes), runs the gate expecting it to throw (FAIL).
    -BinaryMutation: synthesizes a minimal byte_exact scenario (binary file + matching ZIP entry
       + manifest), asserts the unmutated tree PASSES, then mutates one byte and asserts FAIL.
    -Gate3IsolatedNegative: runs two ISOLATED Gate-3 negative scenarios (text non-newline
       mutation + synthetic binary byte_exact mutation) where Gate 4 is kept GREEN and Gate 3
       MUST FAIL with GATE3_SOURCE_TRACE_MISMATCH. Operates on temp copies only; engine/ untouched.
    -AllGate3: runs -EolCanonical, -NonNewlineMutation, -BinaryMutation and -Gate3IsolatedNegative.

    Only this test script is modified; engine-release-gates.ps1 business logic is untouched.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [switch]$Negative,
    [switch]$EolCanonical,
    [switch]$NonNewlineMutation,
    [switch]$BinaryMutation,
    [switch]$Gate3IsolatedNegative,
    [switch]$AllGate3
)
$ErrorActionPreference = "Stop"
if (-not $RepoRoot) { $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path }
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
. (Join-Path $PSScriptRoot "engine-release-gates.ps1")
$BaselinePath = Join-Path $RepoRoot "engine/engine-baseline.json"

<#
.SYNOPSIS
    Test 1: EOL canonicalization positive.
    ZIP entries are CRLF, on-disk runtime files are LF; content is identical after normalization.
    Proves Gate 3 (text_lf_canonical) still PASSES despite the EOL-only divergence.
#>
function Run-EolCanonicalTest {
    $tmp = Join-Path $env:TEMP ("rpm_eol_" + [System.Guid]::NewGuid().ToString("N"))
    Copy-Item -Path (Join-Path $RepoRoot "engine") -Destination (Join-Path $tmp "engine") -Recurse -Force
    try {
        $victimRel = "engine/vendor/full-spectrum-engine/simulate.py"
        $victim    = Join-Path $tmp $victimRel
        $zipName   = "engine-v1.5.0-88493007.zip"
        $zipPath   = Join-Path (Join-Path $tmp "engine") $zipName
        $entryName = "engine-v1.5.0-88493007/simulate.py"

        # 1) Prove the EOL divergence is real: on-disk raw SHA != ZIP entry raw SHA.
        $diskSha = Get-FileSha256 $victim
        $zipSha  = Get-ZipEntrySha256 $zipPath $entryName
        $diverge = ($diskSha -ne $zipSha)

        # 2) Gate 3 must still PASS via text_lf_canonical normalization.
        $passed = $false
        try {
            $null = Test-EngineReleaseGates -RepoRoot $tmp -BaselinePath (Join-Path (Join-Path $tmp "engine") "engine-baseline.json")
            $passed = $true
        } catch {
            Write-Host ("EOL CANONICAL: gate threw unexpectedly: " + $_.Exception.Message)
        }

        if (-not $diverge) {
            Write-Host "EOL CANONICAL TEST FAILED: on-disk SHA == ZIP entry SHA (no EOL divergence detected)"
            exit 1
        }
        if (-not $passed) {
            Write-Host "EOL CANONICAL TEST FAILED: gate did NOT PASS on CRLF/LF-divergent but content-equal tree"
            exit 1
        }
        Write-Host "EOL CANONICAL TEST PASSED"
    } finally {
        if (Test-Path $tmp) { Remove-Item -Path $tmp -Recurse -Force }
    }
}

<#
.SYNOPSIS
    Test 2: non-newline byte mutation of a traceable text file must make the gate FAIL.
    Modifies exactly one non-newline byte (byte-exact WriteAllBytes, no BOM / no EOL change),
    then expects Test-EngineReleaseGates to throw (Gate 3 or Gate 4 may catch it - either is a FAIL).
#>
function Run-NonNewlineMutationTest {
    $tmp = Join-Path $env:TEMP ("rpm_nonnl_" + [System.Guid]::NewGuid().ToString("N"))
    Copy-Item -Path (Join-Path $RepoRoot "engine") -Destination (Join-Path $tmp "engine") -Recurse -Force
    try {
        $victim = Join-Path $tmp "engine/vendor/full-spectrum-engine/simulate.py"
        $bytes  = [System.IO.File]::ReadAllBytes($victim)

        # Find first byte that is neither LF (0x0A) nor CR (0x0D).
        $idx = -1
        for ($i = 0; $i -lt $bytes.Length; $i++) {
            if ($bytes[$i] -ne 0x0A -and $bytes[$i] -ne 0x0D) { $idx = $i; break }
        }
        if ($idx -lt 0) { Write-Host "NON-NEWLINE MUTATION TEST FAILED: no non-newline byte found"; exit 1 }

        # Flip it to a DIFFERENT non-newline byte (XOR 0x20; avoid landing on 0x0A/0x0D).
        $old = $bytes[$idx]
        $x   = $old -band 0xFF -bxor 0x20
        if ($x -eq 0x0A -or $x -eq 0x0D) { $x = ($old + 1) -band 0xFF }
        if ($x -eq 0x0A -or $x -eq 0x0D) { $x = ($old - 1) -band 0xFF }
        if ($x -eq $old) { $x = ($old + 1) -band 0xFF }
        $bytes[$idx] = [byte]$x
        # Byte-exact write: never use Set-Content (would add BOM / alter newlines).
        [System.IO.File]::WriteAllBytes($victim, $bytes)

        $passed = $true
        try {
            Test-EngineReleaseGates -RepoRoot $tmp -BaselinePath (Join-Path (Join-Path $tmp "engine") "engine-baseline.json")
        } catch {
            $passed = $false
            Write-Host ("NON-NEWLINE MUTATION: gate caught the mutation (GOOD): " + $_.Exception.Message)
        }
        if ($passed) {
            Write-Host "NON-NEWLINE MUTATION TEST FAILED: gate passed on mutated tree (BAD)"
            exit 1
        }
        Write-Host "NON-NEWLINE MUTATION TEST PASSED"
    } finally {
        if (Test-Path $tmp) { Remove-Item -Path $tmp -Recurse -Force }
    }
}

<#
.SYNOPSIS
    Test 3: synthetic byte_exact binary scenario.
    Builds a minimal repo: a binary file traced to a source-artifact ZIP entry via byte_exact.
    Sanity: unmutated tree PASSES. Then one byte of the binary is flipped -> gate must FAIL.
#>
function Run-BinaryMutationTest {
    $tmp       = Join-Path $env:TEMP ("rpm_bin_" + [System.Guid]::NewGuid().ToString("N"))
    $tmpEngine = Join-Path $tmp "engine"
    New-Item -ItemType Directory -Path $tmpEngine -Force | Out-Null
    try {
        # Copy the real baseline (we will rewrite the two digest fields below).
        Copy-Item -Path (Join-Path $RepoRoot "engine/engine-baseline.json") -Destination (Join-Path $tmpEngine "engine-baseline.json") -Force

        # The synthetic binary payload.
        $binDir  = Join-Path $tmpEngine "vendor/full-spectrum-engine"
        New-Item -ItemType Directory -Path $binDir -Force | Out-Null
        $binRel  = "engine/vendor/full-spectrum-engine/_test_bin.bin"
        $binPath = Join-Path $tmpEngine "vendor/full-spectrum-engine/_test_bin.bin"
        $binBytes = [byte[]]@(1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16)
        [System.IO.File]::WriteAllBytes($binPath, $binBytes)

        # Derive ZIP name + entry name from the baseline.
        $base = Get-Content (Join-Path $tmpEngine "engine-baseline.json") -Raw | ConvertFrom-Json
        $zipName     = [string]$base.source_artifact_filename        # engine-v1.5.0-88493007.zip
        $entryPrefix = [string]$base.source_artifact_entry_prefix     # engine-v1.5.0-88493007/
        $entryName   = $entryPrefix + "_test_bin.bin"                 # engine-v1.5.0-88493007/_test_bin.bin
        $zipPath     = Join-Path $tmpEngine $zipName

        # Package the binary as a ZIP entry (byte-for-byte, no compression altering content bytes).
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        $zf = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
        $ze = $zf.CreateEntry($entryName)
        $zs = $ze.Open()
        $zs.Write($binBytes, 0, $binBytes.Length)
        $zs.Dispose()
        $zf.Dispose()
        $zipSha = Get-FileSha256 $zipPath

        # Update baseline: source artifact digest = this ZIP's digest.
        $base.engine_source_artifact_sha256 = $zipSha
        [System.IO.File]::WriteAllText((Join-Path $tmpEngine "engine-baseline.json"),
            ($base | ConvertTo-Json -Depth 10),
            (New-Object System.Text.UTF8Encoding($false)))

        # Borrow top-level fields from the real manifest; build a single-entry files array.
        $real = Get-Content (Join-Path $RepoRoot "engine/runtime-payload-manifest.json") -Raw | ConvertFrom-Json
        $binSha = Get-FileSha256 $binPath
        $entry = [PSCustomObject]@{
            path                      = $binRel
            sha256                    = $binSha
            source_artifact_entry     = $entryName
            traces_to_source_artifact = $true
            source_trace_mode         = "byte_exact"
            source_trace_sha256      = $binSha
        }
        $manifest = [PSCustomObject]@{
            schema_version                  = $real.schema_version
            engine_id                       = $real.engine_id
            engine_version                  = $real.engine_version
            engine_tag                      = $real.engine_tag
            engine_commit                   = $real.engine_commit
            source_artifact_filename        = $real.source_artifact_filename
            source_artifact_sha256          = $real.source_artifact_sha256
            source_artifact_entry_prefix    = $real.source_artifact_entry_prefix
            status                          = "RUNTIME_PAYLOAD_RECONCILED"
            runtime_payload_manifest_sha256 = ""
            _source_of_truth                = "engine/engine-baseline.json"
            files                           = @($entry)
            generated_at                    = $real.generated_at
        }

        # Compute the manifest digest with the EXACT same formula as engine-release-gates.ps1
        # (path|sha256|source_trace_mode|source_trace_sha256, sorted by path, joined by "`n", SHA-256).
        $lines  = ($manifest.files | Sort-Object path | ForEach-Object { "$($_.path)|$($_.sha256)|$($_.source_trace_mode)|$($_.source_trace_sha256)" }) -join "`n"
        $lbytes = [System.Text.Encoding]::UTF8.GetBytes($lines)
        $digest = (Get-FileHash -Algorithm SHA256 -InputStream ([System.IO.MemoryStream]::new($lbytes))).Hash.ToLowerInvariant()
        $manifest.runtime_payload_manifest_sha256 = $digest
        [System.IO.File]::WriteAllText((Join-Path $tmpEngine "runtime-payload-manifest.json"),
            ($manifest | ConvertTo-Json -Depth 10),
            (New-Object System.Text.UTF8Encoding($false)))

        # Baseline must also carry the matching manifest digest (Gate 4).
        $base2 = Get-Content (Join-Path $tmpEngine "engine-baseline.json") -Raw | ConvertFrom-Json
        $base2.engine_runtime_payload_manifest_sha256 = $digest
        [System.IO.File]::WriteAllText((Join-Path $tmpEngine "engine-baseline.json"),
            ($base2 | ConvertTo-Json -Depth 10),
            (New-Object System.Text.UTF8Encoding($false)))

        # Step 6: sanity - unmutated byte_exact tree MUST PASS (no worker.py present -> Gate 5 skipped).
        $sanityPass = $false
        try {
            $null = Test-EngineReleaseGates -RepoRoot $tmp -BaselinePath (Join-Path $tmpEngine "engine-baseline.json")
            $sanityPass = $true
        } catch {
            Write-Host ("BINARY MUTATION: sanity (unmutated) gate threw (BAD): " + $_.Exception.Message)
        }

        # Step 7: flip one byte of the binary -> gate MUST FAIL (byte_exact catches the change).
        $binBytes2 = [byte[]]@(1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,17)  # last byte 16 -> 17
        [System.IO.File]::WriteAllBytes($binPath, $binBytes2)
        $mutPass = $true
        try {
            Test-EngineReleaseGates -RepoRoot $tmp -BaselinePath (Join-Path $tmpEngine "engine-baseline.json")
        } catch {
            $mutPass = $false
            Write-Host ("BINARY MUTATION: gate caught the byte change (GOOD): " + $_.Exception.Message)
        }

        if (-not $sanityPass) { Write-Host "BINARY MUTATION TEST FAILED: sanity gate did not PASS on unmutated tree"; exit 1 }
        if ($mutPass)         { Write-Host "BINARY MUTATION TEST FAILED: gate passed on mutated binary (BAD)"; exit 1 }
        Write-Host "BINARY MUTATION TEST PASSED"
    } finally {
        if (Test-Path $tmp) { Remove-Item -Path $tmp -Recurse -Force }
    }
}

<#
.SYNOPSIS
    ISOLATED negative tests for Gate 3 (source-artifact content traceability).
    Each scenario keeps Gate 4 (raw byte integrity) GREEN and forces Gate 3 to FAIL, proving
    the failure is isolated to Gate 3 and not merely caught early by Gate 4.

    Scenario A (text, non-newline mutation):
      Flip ONE non-newline byte of a text_lf_canonical traceable file on disk, then re-stamp the
      manifest entry.sha256 AND the manifest digest (Gate 4 stays green). Keep the frozen
      source_trace_sha256 and ZIP entry untouched -> Gate 3 sees canonical(runtime) !=
      source_trace_sha256 and throws GATE3_SOURCE_TRACE_MISMATCH.

    Scenario B (binary, byte_exact mutation):
      Synthesize a byte_exact traceable entry: a binary file on disk + a matching entry appended to
      a COPY of the frozen ZIP + a manifest entry. Re-stamp the manifest digest and the ZIP digest
      (temp copies only) so Gates 1/2/4 stay green. Then flip ONE byte of the disk binary and
      re-stamp its manifest sha256 (Gate 4 stays green). Gate 3 byte_exact sees
      raw(runtime) != source_trace_sha256 and throws GATE3_SOURCE_TRACE_MISMATCH.

    All mutations happen inside a temp copy; the official engine/ tree is never touched.
#>
function Assert-Gate3IsolatedOutcome {
    param(
        [string]$RepoRoot,
        [string]$BaselinePath
    )
    $threw = $false; $msg = ""
    try {
        Test-EngineReleaseGates -RepoRoot $RepoRoot -BaselinePath $BaselinePath
    } catch {
        $threw = $true; $msg = $_.Exception.Message
    }
    if (-not $threw) {
        return @{ RawPass = $false; SourceFail = $false; Code = "NO_THROW"; Msg = "gate returned without throwing (unexpected PASS)" }
    }
    if ($msg -like "*manifest != actual files*") {
        return @{ RawPass = $false; SourceFail = $false; Code = "GATE4_MANIFEST_ITEM_MISMATCH"; Msg = $msg }
    }
    if ($msg -like "*runtime-payload-manifest digest*") {
        return @{ RawPass = $false; SourceFail = $false; Code = "GATE4_DIGEST_MISMATCH"; Msg = $msg }
    }
    if ($msg -like "*recomputed ZIP digest*") {
        return @{ RawPass = $false; SourceFail = $false; Code = "GATE2_ZIP_DIGEST"; Msg = $msg }
    }
    if ($msg -like "*source_trace_sha256 != canonical*") {
        return @{ RawPass = $true; SourceFail = $true; Code = "GATE3_SOURCE_TRACE_MISMATCH"; Msg = $msg }
    }
    return @{ RawPass = $false; SourceFail = $false; Code = ("OTHER: " + $msg); Msg = $msg }
}

function Run-Gate3IsolatedTextTest {
    $parent = Join-Path $env:TEMP ("gate3-iso-" + $PID + "-t1")
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Copy-Item -Path (Join-Path $RepoRoot "engine") -Destination (Join-Path $parent "engine") -Recurse -Force
    try {
        $victimRel = "engine/vendor/full-spectrum-engine/simulate.py"
        $victim    = Join-Path $parent $victimRel
        $rpmPath   = Join-Path $parent "engine/runtime-payload-manifest.json"
        $basePath  = Join-Path $parent "engine/engine-baseline.json"
        $zipName   = "engine-v1.5.0-88493007.zip"
        $zipPath   = Join-Path (Join-Path $parent "engine") $zipName

        # Flip ONE non-newline byte (text content change only; EOL untouched).
        # Prefer an alphabetic byte so the change is a clean letter (case toggle via XOR 0x20),
        # which is guaranteed non-newline; fall back to the first non-newline byte otherwise.
        $bytes = [System.IO.File]::ReadAllBytes($victim)
        $idx = -1
        for ($i = 0; $i -lt $bytes.Length; $i++) {
            $c = $bytes[$i]
            if (($c -ge 0x41 -and $c -le 0x5A) -or ($c -ge 0x61 -and $c -le 0x7A)) { $idx = $i; break }
        }
        if ($idx -lt 0) {
            for ($i = 0; $i -lt $bytes.Length; $i++) {
                if ($bytes[$i] -ne 0x0A -and $bytes[$i] -ne 0x0D) { $idx = $i; break }
            }
        }
        if ($idx -lt 0) { Write-Host "GATE3 ISOLATED TEXT TEST FAILED: no mutable byte found"; exit 1 }
        $old = $bytes[$idx]
        $x = $old -band 0xFF -bxor 0x20
        if ($x -eq 0x0A -or $x -eq 0x0D) { $x = ($old + 1) -band 0xFF }
        if ($x -eq $old) { $x = ($old + 1) -band 0xFF }
        $bytes[$idx] = [byte]$x
        [System.IO.File]::WriteAllBytes($victim, $bytes)
        $newDiskSha = Get-FileSha256 $victim

        # Re-stamp manifest entry.sha256 (Gate 4 per-item passes) + manifest digest (Gate 4 digest passes).
        $m = Get-Content $rpmPath -Raw | ConvertFrom-Json
        $entry = $m.files | Where-Object { $_.path -eq $victimRel } | Select-Object -First 1
        if (-not $entry) { Write-Host "GATE3 ISOLATED TEXT TEST FAILED: simulate.py entry absent"; exit 1 }
        $entry.sha256 = $newDiskSha
        [System.IO.File]::WriteAllText($rpmPath, ($m | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))
        $digest = Get-RuntimePayloadManifestDigest $rpmPath
        $b = Get-Content $basePath -Raw | ConvertFrom-Json
        $b.engine_runtime_payload_manifest_sha256 = $digest
        [System.IO.File]::WriteAllText($basePath, ($b | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))

        # ZIP + source_trace_sha256 intentionally UNCHANGED -> Gate 3 must now fail.
        $r = Assert-Gate3IsolatedOutcome -RepoRoot $parent -BaselinePath $basePath
        Write-Host "=== GATE3 ISOLATED TEST 1: text non-newline mutation ==="
        Write-Host ("  victim file       : " + $victimRel)
        Write-Host ("  flipped byte @idx : " + $idx + "  (" + [string]::Format("0x{0:X2}", $old) + " -> " + [string]::Format("0x{0:X2}", $x) + ")")
        Write-Host ("  RAW_BYTE_GATE     = " + $(if ($r.RawPass) { "PASS" } else { "FAIL" }))
        Write-Host ("  SOURCE_TRACE_GATE = " + $(if ($r.SourceFail) { "FAIL_EXPECTED" } else { "FAIL" }))
        Write-Host ("  FAILURE_CODE      = " + $r.Code)
        if (-not ($r.RawPass -and $r.SourceFail -and $r.Code -eq "GATE3_SOURCE_TRACE_MISMATCH")) {
            Write-Host ("  UNEXPECTED: " + $r.Msg); exit 1
        }
        Write-Host "GATE3 ISOLATED TEXT TEST PASSED"
    } finally {
        if (Test-Path $parent) { Remove-Item -Path $parent -Recurse -Force }
    }
}

function Run-Gate3IsolatedBinaryTest {
    $parent = Join-Path $env:TEMP ("gate3-iso-" + $PID + "-t2")
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Copy-Item -Path (Join-Path $RepoRoot "engine") -Destination (Join-Path $parent "engine") -Recurse -Force
    try {
        $binRel  = "engine/vendor/full-spectrum-engine/__isolated_bin_test.bin"
        $binPath = Join-Path $parent $binRel
        $binDir  = Split-Path $binPath -Parent
        New-Item -ItemType Directory -Path $binDir -Force | Out-Null
        $rpmPath  = Join-Path $parent "engine/runtime-payload-manifest.json"
        $basePath = Join-Path $parent "engine/engine-baseline.json"
        $zipName  = "engine-v1.5.0-88493007.zip"
        $zipPath  = Join-Path (Join-Path $parent "engine") $zipName
        $entryPrefix = "engine-v1.5.0-88493007/"
        $entryName   = $entryPrefix + "__isolated_bin_test.bin"

        # 32 random bytes on disk.
        $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
        $binBytes = New-Object byte[] 32
        $rng.GetBytes($binBytes)
        [System.IO.File]::WriteAllBytes($binPath, $binBytes)
        $diskSha = Get-FileSha256 $binPath

        # Rebuild the ZIP copy: copy every original entry byte-for-byte, then append the binary entry.
        $zf1 = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
        $orig = @()
        foreach ($e in $zf1.Entries) {
            $ms = New-Object System.IO.MemoryStream
            $e.Open().CopyTo($ms)
            $orig += [PSCustomObject]@{ Name = $e.FullName; Data = $ms.ToArray() }
        }
        $zf1.Dispose()
        # ZipFile.Open(..., Create) refuses to overwrite an existing file, so remove the copy first.
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        $zf2 = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
        foreach ($oe in $orig) {
            $ne = $zf2.CreateEntry($oe.Name)
            $dst = $ne.Open(); $dst.Write($oe.Data, 0, $oe.Data.Length); $dst.Dispose()
        }
        $ne = $zf2.CreateEntry($entryName)
        $dst = $ne.Open(); $dst.Write($binBytes, 0, $binBytes.Length); $dst.Dispose()
        $zf2.Dispose()
        $zipSha = Get-FileSha256 $zipPath

        # Append a byte_exact traceable manifest entry. source_trace_sha256 == ZIP entry SHA (identical bytes).
        $m = Get-Content $rpmPath -Raw | ConvertFrom-Json
        $newEntry = [PSCustomObject]@{
            path                      = $binRel
            sha256                    = $diskSha
            source_artifact_entry     = $entryName
            traces_to_source_artifact = $true
            source_trace_mode         = "byte_exact"
            source_trace_sha256       = $diskSha
        }
        $m.files = @($m.files) + @($newEntry)
        [System.IO.File]::WriteAllText($rpmPath, ($m | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))
        $digest = Get-RuntimePayloadManifestDigest $rpmPath
        $b = Get-Content $basePath -Raw | ConvertFrom-Json
        $b.engine_runtime_payload_manifest_sha256 = $digest
        $b.engine_source_artifact_sha256 = $zipSha
        [System.IO.File]::WriteAllText($basePath, ($b | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))

        # Sanity: unmutated synthetic byte_exact tree MUST PASS.
        $sanity = $true
        try { $null = Test-EngineReleaseGates -RepoRoot $parent -BaselinePath $basePath }
        catch { $sanity = $false; Write-Host ("  SANITY threw (BAD): " + $_.Exception.Message) }
        if (-not $sanity) { Write-Host "GATE3 ISOLATED BINARY TEST FAILED: sanity gate did not PASS"; exit 1 }

        # Mutate disk binary: flip ONE byte; re-stamp manifest sha256 (Gate 4 stays green).
        $mut = New-Object byte[] $binBytes.Length
        [Array]::Copy($binBytes, $mut, $binBytes.Length)
        $flipIdx = 0
        $oldB = $mut[$flipIdx]
        $newB = ($oldB + 1) -band 0xFF
        if ($newB -eq $oldB) { $newB = ($oldB - 1) -band 0xFF }
        $mut[$flipIdx] = [byte]$newB
        [System.IO.File]::WriteAllBytes($binPath, $mut)
        $mutDiskSha = Get-FileSha256 $binPath

        $m = Get-Content $rpmPath -Raw | ConvertFrom-Json
        $be = $m.files | Where-Object { $_.path -eq $binRel } | Select-Object -First 1
        $be.sha256 = $mutDiskSha
        [System.IO.File]::WriteAllText($rpmPath, ($m | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))
        $digest = Get-RuntimePayloadManifestDigest $rpmPath
        $b = Get-Content $basePath -Raw | ConvertFrom-Json
        $b.engine_runtime_payload_manifest_sha256 = $digest
        # engine_source_artifact_sha256 stays = $zipSha (ZIP unchanged after mutation).
        [System.IO.File]::WriteAllText($basePath, ($b | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))

        # source_trace_sha256 + ZIP entry unchanged -> Gate 3 byte_exact must fail.
        $r = Assert-Gate3IsolatedOutcome -RepoRoot $parent -BaselinePath $basePath
        Write-Host "=== GATE3 ISOLATED TEST 2: binary byte_exact mutation ==="
        Write-Host ("  synthetic file    : " + $binRel)
        Write-Host ("  flipped byte @idx : " + $flipIdx + "  (" + [string]::Format("0x{0:X2}", $oldB) + " -> " + [string]::Format("0x{0:X2}", $newB) + ")")
        Write-Host ("  RAW_BYTE_GATE     = " + $(if ($r.RawPass) { "PASS" } else { "FAIL" }))
        Write-Host ("  SOURCE_TRACE_GATE = " + $(if ($r.SourceFail) { "FAIL_EXPECTED" } else { "FAIL" }))
        Write-Host ("  FAILURE_CODE      = " + $r.Code)
        if (-not ($r.RawPass -and $r.SourceFail -and $r.Code -eq "GATE3_SOURCE_TRACE_MISMATCH")) {
            Write-Host ("  UNEXPECTED: " + $r.Msg); exit 1
        }
        Write-Host "GATE3 ISOLATED BINARY TEST PASSED"
    } finally {
        if (Test-Path $parent) { Remove-Item -Path $parent -Recurse -Force }
    }
}

function Run-Gate3IsolatedNegativeTests {
    Run-Gate3IsolatedTextTest
    Run-Gate3IsolatedBinaryTest
}

# ---- dispatch ----
if ($EolCanonical) {
    Run-EolCanonicalTest
} elseif ($NonNewlineMutation) {
    Run-NonNewlineMutationTest
} elseif ($BinaryMutation) {
    Run-BinaryMutationTest
} elseif ($Gate3IsolatedNegative) {
    Run-Gate3IsolatedNegativeTests
} elseif ($AllGate3) {
    Run-EolCanonicalTest
    Run-NonNewlineMutationTest
    Run-BinaryMutationTest
    Run-Gate3IsolatedNegativeTests
} elseif ($Negative) {
    $tmp = Join-Path $env:TEMP ("rpm_neg_" + [System.Guid]::NewGuid().ToString("N"))
    Copy-Item -Path (Join-Path $RepoRoot "engine") -Destination (Join-Path $tmp "engine") -Recurse -Force
    $victim = Join-Path $tmp "engine/vendor/full-spectrum-engine/simulate.py"
    $c = Get-Content $victim -Raw
    Set-Content -Path $victim -Value ($c + "`n# mutated for negative test`n") -Encoding utf8
    $tmpBaseline = Join-Path $tmp "engine/engine-baseline.json"
    $passed = $false
    try {
        Test-EngineReleaseGates -RepoRoot $tmp -BaselinePath $tmpBaseline
        $passed = $true
    } catch {
        Write-Host ("NEGATIVE EXPECTED FAIL: " + $_.Exception.Message)
    }
    Remove-Item -Path $tmp -Recurse -Force
    if ($passed) { Write-Host "NEGATIVE TEST FAILED: gate passed on mutated tree (BAD)"; exit 1 }
    Write-Host "NEGATIVE TEST PASSED: gate caught the mutation (GOOD)"
} else {
    try {
        $r = Test-EngineReleaseGates -RepoRoot $RepoRoot -BaselinePath $BaselinePath
        Write-Host ("POSITIVE TEST PASSED: gates green (result=$r)")
    } catch {
        Write-Host ("POSITIVE TEST FAILED: " + $_.Exception.Message); exit 1
    }
}

# M2-SEC-01 — SQLite Native Runtime Security Verification: Evidence

## Why this exists
NU1903 / CVE-2025-6965 (GHSA-2m69-gcr7-jv3q): aggregate-terms buffer overflow in SQLite **< 3.50.2**.
Severity=High, CVSS v4.0=7.2, **Patched=None**. `Microsoft.Data.Sqlite` 8.0.10 transitively pulls
`SQLitePCLRaw.lib.e_sqlite3` ≤ 2.1.11 (affected range). Upgrading only `Microsoft.Data.Sqlite`
cannot clear it (all M.D.S 8/9/10 pull ≤ 2.1.11).

M2-SEC-01 **empirically verifies** a safe native runtime and determines the adoption approach.
No conclusion is taken from docs — every claim below was produced by running code.

## Harness
`tools/SqliteRuntimeVerify` (standalone exe, **excluded from the 14-project solution** so it never
changes product build semantics). It opens a real SQLite store via `Microsoft.Data.Sqlite` and proves:
1. actual loaded engine `sqlite_version()` ≥ 3.50.2
2. old `e_sqlite3` (≤2.1.11) NOT loaded (native module enumeration + file-scan cross-check)
3. no native DLL conflict (exactly one patched module)
4. CRUD  5. Transactions  6. Schema migration  7. Restart recovery
(#8 NuGetAudit is proven by `restore`/`build` with default audit ON — see below)

## Candidate A — SourceGear.sqlite3  (RECOMMENDED / ADOPTED)
Packages: `Microsoft.Data.Sqlite.Core` 8.0.10 + `SQLitePCLRaw.core` 2.1.6 +
`SQLitePCLRaw.provider.e_sqlite3` 2.1.6 + `SourceGear.sqlite3` (csproj `SourceGearVersion=3.53.3`).
The custom provider replaces the default bundle so the patched SourceGear native lib is used instead
of the (absent) vulnerable `SQLitePCLRaw.lib.e_sqlite3`.

**Lead independently re-ran (clean build), result 7/7 PASS:**
```
  [PASS] 1. Loaded sqlite engine >= 3.50.2 :: sqlite_version() = '3.53.3' (>= 3,50,2)
  Loaded native sqlite modules:
    - e_sqlite3.DLL  => ...\bin\Release\net10.0\win-x64\e_sqlite3.DLL
  [PASS] 2. Old e_sqlite3 (<=2.1.11) not loaded :: module(s)=1; from vulnerable cache=False; engine='3.53.3'; file-scan='3.53.3'
  [PASS] 3. No native DLL conflict :: distinct modules = 1 (patched SourceGear lib)
  [PASS] 4. CRUD         [PASS] 5. Transactions
  [PASS] 6. Schema migration        [PASS] 7. Restart recovery
  RESULT: 7/7 checks passed, 0 failed.
```
**#8 NuGetAudit (default ON):** `dotnet restore` of the harness (Candidate A) emits **NU1903 = 0**;
transitive graph contains **NO `SQLitePCLRaw.lib.e_sqlite3`** (vulnerable package removed by construction)
→ audit clean. Build: **0 errors**.

## Candidate B — pin SQLitePCLRaw.lib.e_sqlite3 2.1.12
Packages: `Microsoft.Data.Sqlite` 8.0.10 + `SQLitePCLRaw.lib.e_sqlite3` 2.1.12.
Measured in an **independent clean temp project** (no stale-dll contamination):
```
A-verify (... + SQLitePCLRaw.lib.e_sqlite3 2.1.12) sqlite_version() = '3.53.3'
  loaded native: e_sqlite3.DLL => ...\runtimes\win-x64\native\e_sqlite3.DLL
```
=> Empirically, **2.1.12 also bundles a patched engine (3.53.3 ≥ 3.50.2)**, so it technically
remediates the CVE and is NuGetAudit-clean (outside the advisory range > 2.1.11). CRUD/txn/migration/
recovery all pass.

**NOT ADOPTED:** advisory GHSA-2m69-gcr7-jv3q lists this package line as **"Patched=None"** (no
sanctioned fix version). A strict HBG / RC security gate will not accept an unsanctioned pin;
SourceGear (a dedicated patched SQLite distribution) is the advisory-aligned remediation.

## Determined approach
**Adopt Candidate A (SourceGear.sqlite3).** M2-RUN-01 bakes SourceGear into the product's
SQLite-consuming projects and fixes publish/launch so the patched native lib is shipped automatically.
Candidate B was empirically validated as a viable, lower-surface alternative but is held in reserve
because the advisory does not sanction the 2.1.12 pin.

## Caveat: harness candidate switching
The `-p:Candidate=` command-line switch does **not** reliably re-resolve the restore graph (restore
caches assets, and the property is not always forwarded to restore evaluation). To compare candidates
in the harness, **edit the `<Candidate>` element** in `SqliteRuntimeVerify.csproj`, then
`dotnet clean` + `dotnet restore -s <feed>` + `dotnet build`. Default is `A`.

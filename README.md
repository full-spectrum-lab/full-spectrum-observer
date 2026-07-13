# Full Spectrum Observer

Observer System `0.1.0-alpha` Foundation Kernel repository bootstrap.

## Current implementation status

```text
VG1 Scope Baseline:        FS-OBS-V010-SCOPE-BL-1.0 — FROZEN
VG2 Design Baseline:       FS-OBS-V010-DES-BL-1.0 — FROZEN
Implementation Baseline:   FS-OBS-V010-IMP-BL-1.0 — FROZEN
IG0 Baseline Verified:     PASS
IG1 Locked .NET Build:     PASS (formal repository)
IG2 Contracts/Schemas:     PASS
IG3 Evidence Core:         PASS (native SQLite win-x64)
IG4 Engine Bridge:         PASS (private Python 3.11)
WP-04 Execution Source:    IMPLEMENTED
WP-05 CLI Source:          IMPLEMENTED
IG5 Minimum Loop:          PASS
IG6/IG7/IG8:               NOT IMPLEMENTED / NOT EXECUTED
```

The formal repository has migrated and repaired the IG5 source candidate. IG1
through IG5 pass locally, but this is still an implementation candidate: IG6
automation, IG7 packaging and IG8 independent clean-machine reproduction remain
release blockers.

## Fixed toolchain

- .NET SDK: `10.0.301`
- Target framework: `net10.0`
- Release target RID: `win-x64`
- Engine source: `v1.0.0` / `09062bae2c7608bda79ee4bfde5779109e8e6197`
- Schema baseline: `FS-OBS-V010-SCHEMA-BL-1.0`

The exact .NET 10 SDK is pinned by `global.json`. The build scripts do not download
SDKs, packages, Python, or Engine files.

## First commands on Windows

```powershell
pwsh ./scripts/verify-baseline.ps1
pwsh ./scripts/build.ps1 -Configuration Release -Locked
pwsh ./scripts/test.ps1 -Gate IG1
```

Run the remaining gates with explicitly pinned runtime paths:

```powershell
$env:FSP_PRIVATE_PYTHON = "C:\\path\\to\\private-python-3.11\\python.exe"
$env:FSP_SQLITE_NATIVE_DIR = "C:\\path\\to\\pinned-sqlite-win-x64"
pwsh ./scripts/test.ps1 -Gate IG3
pwsh ./scripts/test.ps1 -Gate IG4
```

## Boundary

Only `Observer.EngineFacade` may start the Python worker. No project in this
bootstrap implements FSHI, Risk, ESS, Gate, UNKNOWN, Explanation, or Runestone
calculation logic.

## IG2 candidate branch

The branch `feat/IMP-0101-ig2-contracts-candidate` contains the contract implementation candidate. It must not
be merged into `main` until the exact .NET build and IG2 C# runners pass. See
`IG2_候选执行报告.md`.

## IG3 Evidence Core source candidate

The `feat/IMP-0201-evidence-core-candidate` branch implements the SQLite schema, native C API adapter, Artifact Store, operations, idempotency, immutable Runtime Snapshot, Observation finalization and GLOBAL Audit Hash Chain. The Python reference oracle passes, but C# build and win-x64 native sqlite runtime tests are pending.

## IG4 Engine Bridge source candidate

The `feat/IMP-0301-engine-bridge-candidate` branch vendors the fixed Engine dependency, implements the one-line Python Worker protocol and the C# process Facade. The actual Worker smoke executes the pinned CASE005 golden successfully. The C# build, private Python 3.11 bundle and Windows process-tree tests remain pending.

## IG3/IG4 source integration candidate

The current `integration/IG3-IG4-source-candidate` branch contains both independently developed candidates. It is a source-compatibility branch only; it does not start WP-04 or claim IG3/IG4 Gate approval.


# Full Spectrum Observer

Observer System `0.2.0-alpha` — Engine v1.0/v1.5 Compatibility Adapter: a Python `src/compat/` layer layered on the v0.1 Foundation Kernel.

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
IG6 Security/Fault Gate:   PASS
IG7 Offline Package Gate:  PASS (R2, 1961/1961 payload checks)
IG8 Independent Repro:     PASS (clean extracted package)
```

IG0 through IG6 pass. The first IG7 package passed functional verification, but
independent IG8 review found a blocking LICENSE/SBOM contradiction. The owner has
now selected `MulanPSL-2.0 OR Apache-2.0`; packaging records that expression,
inventories every payload file and includes all bundled Python distributions.
The corrected R2 package passed complete payload verification and clean-directory
IG8 replay. The `v0.2.0-alpha` tag denotes this Engine v1.0/v1.5 Compatibility Adapter release
(Python `src/compat/` layer); it does not re-execute or re-claim the v0.1 Foundation
Kernel .NET gates (IG0–IG8), which remain authoritative from the v0.1 release.

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

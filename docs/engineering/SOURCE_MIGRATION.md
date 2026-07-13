# Source migration record

Date: 2026-07-13

## Source

- Package: `全频谱观察者系统_v0.1.0-alpha_Foundation_Kernel_IG5最小闭环候选源代码包_v1.0.zip`
- Package SHA-256: `fc13bfd227037056eecaec99a4b1f82f4b97750127cd7a7181fe0a53ec8de482`
- Imported candidate commit: `5960af6`
- Imported branch identity: `integration/IG5-source-candidate`
- Destination: Gitee `full-spectrum/full-spectrum-observer`

The complete tracked tree of candidate commit `5960af6` was imported. ZIP extraction on Windows removed executable bits from six Python scripts but did not change their bytes. The formal repository records subsequent engineering fixes in its own commit history.

## Findings closed during migration

1. Replaced PowerShell 7-only `utf8NoBOM` usage with deterministic UTF-8-no-BOM writes compatible with Windows PowerShell 5.1 and PowerShell 7.
2. Repaired mojibake paths in `baselines.lock.json` by matching the frozen SHA-256 values to the actual baseline files; IG0 now verifies 51/51 files.
3. Regenerated stale NuGet lock files against the current project graph, then verified locked restore.
4. Updated .NET 10 API compatibility (`InvalidDataException` inheritance and span-based JSON parsing).
5. Resolved analyzer findings without renaming wire-level reason codes or weakening warnings-as-errors.
6. Corrected the Engine Facade timeout parameter contract and culture-dependent integration timestamps.
7. Made the private-Python path check compatible with Windows PowerShell 5.1.
8. Changed IG3–IG5 evidence to distinguish standalone reference-oracle execution from a complete formal Gate invocation.

## Verified toolchain

- .NET SDK: 10.0.301
- Target: net10.0 / win-x64
- Python: CPython 3.11.9 x64
- NumPy: 1.26.4
- JSON Schema validator: jsonschema 4.25.1
- SQLite runtime: 3.50.4 win-x64 official binary
- Engine: v1.0.0 / `09062bae2c7608bda79ee4bfde5779109e8e6197`

This record proves source provenance and local Gate execution. It is not an IG7 release manifest and does not claim an offline redistributable package.


# Observer Engine 2 Offline Adapter Evidence

The Observer-owned adapter is implemented locally in `src/observer_engine2/`.
It constructs the frozen request, calls an injected Engine 2 port, preserves
the exact result/error fields, retains the request SnapshotBinding in an
Observer projection record, and fails closed on mutation or audit handoff
failure. The legacy `worker.py -> simulate.py::run_simulation` path remains
separate and unchanged.

Contract adapter tests: PASS (15/15). Fake Engine tests: PASS (3/3). Pinned
runtime tests: PASS (3/3), with Engine runtime commit
`11c6f83593a327457940c6b5832aa03aa50713ee` actually loaded from an external
exact-commit checkout. Full Python regression: PASS (100/100). Architecture
check: PASS. Baseline check: PASS (51/51). The isolated Observer environment
completed .NET build and IG1-IG5 foundation gates with SDK 10.0.301, 0
warnings, and 0 errors.

Observer implementation commit: `10b7f76f4bde0dd4dc88731dcd57c98936a72c1e`.
The manifest content digest excludes the manifest file itself; the manifest
file's own SHA-256 is `BA166B4EA108ED6F910D05B3BA667F3E9498A02863881642D37949AA394DE9AA`.

This is single-repository offline evidence only. It does not establish
Observer-Engine or Observer-KG compatibility, cross-repository E2E, network
implementation, or production readiness.

# Schema Generation and Dependency Policy v1

- The 12 Foundation Kernel schemas are self-contained at runtime.
- Every schema has a stable `$id`.
- Runtime external `$ref` is prohibited in v0.1.0-alpha.
- Repeated common fragments are governed by `FS-OBS-COMMON-1` and checked for consistency.
- Dependency direction is conceptual and acyclic:
  Common Types -> Request/Validation/Snapshot -> Facade/Output -> Observation/Audit -> Manifests.
- Schema status is `APPROVED_DESIGN_BASELINE`; it is not a public STABLE protocol.

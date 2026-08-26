# v0.4 Internal Candidate Package Contract

Created at: 2026-08-26 16:00 Beijing time (UTC+8)
Last updated at: 2026-08-26 16:00 Beijing time (UTC+8)

## Status

This document defines a local audit artifact only. Its status is
`INTERNAL_CANDIDATE`; it is not a GitHub Release, a production deployment, or
evidence that any external pilot gate has passed.

The candidate package is bound to a specific source commit and must state all
of the following at runtime:

```json
{
  "system_version": "v0.4.0-beta-internal-candidate",
  "implementation_gate": "INTERNAL_CANDIDATE",
  "maturity": "NO_GO_UNTIL_EXTERNAL_GATES",
  "release_authorized": false,
  "engine_version": "v1.5.0"
}
```

## Construction Boundary

`scripts/package-v04-internal.ps1` is separate from the historical
`scripts/package.ps1`. It publishes the CLI with an embedded .NET runtime,
private Python, pinned SQLite native library, Engine v1.5, scenario-pack trust
roots, locks, packs, schemas, and v0.4 evidence documents.

The package launcher provides the CLI a fixed package-root identity file. The
identity file is included in both `SHA256SUMS.txt` and the internal-candidate
manifest. The verifier rejects a package whose identity is missing, altered,
or reports a released v0.3 identity.

## Required Local Verification

1. Run `tools/verify-v04-internal-package.py --package-root <extracted-root>`.
2. Extract the ZIP to a fresh temporary directory and run
   `observer.cmd version --json` from an unrelated working directory.
3. Run a valid synthetic Scenario Pack twice using distinct authorization,
   redaction, and deletion references; the second run must preserve idempotent
   behavior.
4. Alter one packaged file and repeat step 1; the verifier must fail.

These tests establish package integrity and local executable behavior only.
They do not replace target-user acceptance, authorization, redaction,
deletion/exit evidence, or production signing authority.

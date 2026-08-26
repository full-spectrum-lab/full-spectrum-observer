# RP-001 structured external-input pilot record

Date: 2026-08-27 (Beijing time, UTC+8)

Status: `AUTHORIZED_PREPARATION / EXECUTION_NO_GO`

This record describes a local engineering exercise against the Owner-provided narrow sample. It
does not upgrade the sample to an independently redacted, target-user accepted or production-valid
pilot.

## Boundary

- Pack: `observer.case.knowledge-conflict.rp001@1.0.0`
- Pack digest: `7b650c8f58e417e4256006e9317c596b897f795518c5552f206728c58ac29ce7`
- Trust purpose: `OWNER_LOCAL_PILOT:RP-001`
- Trust-store digest: `a26a2049e786ccbf2a47af3061a457041a6fe1043711b27963bcd74774fdc6e2`
- Processing root: `C:\obs-v04-real-pilot` (local only)
- Engine baseline: v1.5.0
- Observer source commit: `6f8d51f1bb8e3531f64980555ced4570406dddd6`
- Internal candidate ZIP SHA-256: `200080d311d175c137f4ba9927a47e4323830f62f95c480e2e0b9e52cb5d88d4`

The source, redacted sample, claim register and governance files remain outside the repository.
The private signing key is local-only and is excluded from the Pack, package, commit and reports.

## Observed result

The Runner completed one structured sample with:

- `candidate_count=1`
- `assertion_key=pilot_log_is_owner_real_evolution`
- `minority_evidence_survived=true`
- `minority_evidence_refs=[owner-correction@2026-08-20]`
- `unknown_context=[opposition_not_established:scan_manifest_available]`
- `human_review_required=true`
- `authorized_action=false`
- Engine-owned `ConflictObservation` rows: `0`

The same command repeated against the same data directory returned `idempotent=true`. The
candidate ledger contains the minority reference and the sample-level UNKNOWN context; claim text
is not persisted there.

## Evidence and limitations

Automated checks found no email, phone, credential, token or Windows-user-path pattern in the
prepared redacted sample. The independent human check is still pending, as are target-user
acceptance and deletion/exit evidence. The adapter accepts explicit structure only; it is not
evidence of general natural-language truth assessment, discovery quality or enterprise safety.

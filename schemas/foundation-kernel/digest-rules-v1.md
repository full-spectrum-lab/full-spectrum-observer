# Digest Rules v1

## FS-OBS-CANON-1

UTF-8 JSON, object keys sorted by Unicode code point, no insignificant whitespace,
UTC timestamps normalized by the producer, and NaN/Infinity prohibited.

## Self-containing digest fields

- `RuntimeConfigurationSnapshot.snapshot_sha256`:
  SHA-256(FS-OBS-CANON-1(snapshot object with `snapshot_sha256` removed)).
- `FoundationCasePackManifest.manifest_sha256`:
  SHA-256(FS-OBS-CANON-1(manifest object with `manifest_sha256` removed)).
- `ReleaseManifest.manifest_sha256`:
  SHA-256(FS-OBS-CANON-1(manifest object with `manifest_sha256` removed)).
- `AuditEvent.event_hash`:
  SHA-256(FS-OBS-CANON-1(event object with `event_hash` removed; `previous_hash` retained)).

A zero-filled or otherwise syntactically valid placeholder digest is prohibited.
VG2 may use `null` only where the schema explicitly marks a build artifact as `PENDING_VG3`.

CREATE TABLE IF NOT EXISTS schema_versions (
    schema_id TEXT PRIMARY KEY,
    applied_at_utc TEXT NOT NULL,
    checksum TEXT NOT NULL
) STRICT;

CREATE TABLE IF NOT EXISTS operations (
    operation_id TEXT PRIMARY KEY,
    request_id TEXT NOT NULL,
    trace_id TEXT NOT NULL,
    state TEXT NOT NULL,
    reason_json TEXT NOT NULL DEFAULT '[]',
    started_at_utc TEXT NULL,
    updated_at_utc TEXT NOT NULL,
    completed_at_utc TEXT NULL,
    timeout_seconds INTEGER NOT NULL CHECK(timeout_seconds BETWEEN 1 AND 300)
) STRICT;
CREATE INDEX IF NOT EXISTS ix_operations_request_id ON operations(request_id);
CREATE INDEX IF NOT EXISTS ix_operations_trace_id ON operations(trace_id);

CREATE TABLE IF NOT EXISTS idempotency_records (
    idempotency_key TEXT PRIMARY KEY,
    request_fingerprint TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    observation_id TEXT NULL,
    state TEXT NOT NULL CHECK(state IN ('RESERVED','COMPLETED')),
    created_at_utc TEXT NOT NULL,
    completed_at_utc TEXT NULL
) STRICT;

CREATE TABLE IF NOT EXISTS runtime_snapshots (
    snapshot_id TEXT PRIMARY KEY,
    snapshot_json TEXT NOT NULL,
    snapshot_sha256 TEXT NOT NULL UNIQUE,
    created_at_utc TEXT NOT NULL
) STRICT;

CREATE TABLE IF NOT EXISTS artifacts (
    artifact_id TEXT PRIMARY KEY,
    media_type TEXT NOT NULL,
    sha256 TEXT NOT NULL UNIQUE,
    size_bytes INTEGER NOT NULL CHECK(size_bytes >= 0),
    relative_path TEXT NOT NULL UNIQUE,
    classification TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
) STRICT;

CREATE TABLE IF NOT EXISTS observations (
    observation_id TEXT PRIMARY KEY,
    operation_id TEXT NOT NULL UNIQUE,
    request_id TEXT NOT NULL,
    trace_id TEXT NOT NULL,
    status TEXT NOT NULL,
    input_sha256 TEXT NOT NULL,
    canonical_context_sha256 TEXT NULL,
    runtime_snapshot_id TEXT NOT NULL,
    output_artifact_id TEXT NULL,
    audit_head TEXT NOT NULL,
    idempotency_key TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    FOREIGN KEY(runtime_snapshot_id) REFERENCES runtime_snapshots(snapshot_id),
    FOREIGN KEY(output_artifact_id) REFERENCES artifacts(artifact_id)
) STRICT;
CREATE INDEX IF NOT EXISTS ix_observations_request_id ON observations(request_id);
CREATE INDEX IF NOT EXISTS ix_observations_trace_id ON observations(trace_id);

CREATE TABLE IF NOT EXISTS audit_events (
    sequence_no INTEGER PRIMARY KEY,
    event_id TEXT NOT NULL UNIQUE,
    stream_id TEXT NOT NULL CHECK(stream_id = 'GLOBAL'),
    event_type TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    actor_json TEXT NOT NULL,
    observation_id TEXT NULL,
    operation_id TEXT NULL,
    trace_id TEXT NOT NULL,
    payload_digest TEXT NOT NULL,
    payload_media_type TEXT NOT NULL,
    serialization_id TEXT NOT NULL CHECK(serialization_id = 'FS-OBS-CANON-1'),
    previous_hash TEXT NOT NULL,
    event_hash TEXT NOT NULL UNIQUE
) STRICT;
CREATE INDEX IF NOT EXISTS ix_audit_events_observation_id ON audit_events(observation_id);
CREATE INDEX IF NOT EXISTS ix_audit_events_trace_id ON audit_events(trace_id);

CREATE TABLE IF NOT EXISTS instance_lock (
    lock_id INTEGER PRIMARY KEY CHECK(lock_id = 1),
    owner_pid INTEGER NOT NULL,
    started_at_utc TEXT NOT NULL,
    package_sha256 TEXT NOT NULL
) STRICT;

CREATE TRIGGER IF NOT EXISTS tr_audit_events_no_update
BEFORE UPDATE ON audit_events
BEGIN SELECT RAISE(ABORT, 'AUDIT_EVENT_IMMUTABLE'); END;

CREATE TRIGGER IF NOT EXISTS tr_audit_events_no_delete
BEFORE DELETE ON audit_events
BEGIN SELECT RAISE(ABORT, 'AUDIT_EVENT_IMMUTABLE'); END;

CREATE TRIGGER IF NOT EXISTS tr_runtime_snapshots_no_update
BEFORE UPDATE ON runtime_snapshots
BEGIN SELECT RAISE(ABORT, 'RUNTIME_SNAPSHOT_IMMUTABLE'); END;

CREATE TRIGGER IF NOT EXISTS tr_runtime_snapshots_no_delete
BEFORE DELETE ON runtime_snapshots
BEGIN SELECT RAISE(ABORT, 'RUNTIME_SNAPSHOT_IMMUTABLE'); END;

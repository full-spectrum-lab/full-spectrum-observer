-- Observer v0.3.0-beta Console local store schema (SQLite 3.9+).
-- Applied idempotently (CREATE TABLE IF NOT EXISTS) by ObserverStore.EnsureSchemaAsync().
-- Red-line aligned:
--   #1  subjects carry NO login/auth/session/token fields.
--   #7  audit_records is INSERT-only; the store exposes no UPDATE/DELETE for it.
--   #8  runtime_snapshots.engine_version pinned to '1.5.0'; evidence/input digests stored verbatim.
-- ADR-001 versioning: status/seq + partial unique index enforcing <=1 Active per subject/source.

CREATE TABLE IF NOT EXISTS subjects (
  local_subject_id   TEXT PRIMARY KEY,
  subject_type       TEXT NOT NULL,
  mode               TEXT NOT NULL,
  concentration_tier TEXT,
  created_at         TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS subject_versions (
  version_id    TEXT PRIMARY KEY,
  subject_id    TEXT NOT NULL REFERENCES subjects(local_subject_id) ON DELETE RESTRICT,
  status        TEXT NOT NULL CHECK (status IN ('Draft','Active','Retired')),
  seq           INTEGER NOT NULL,
  payload       TEXT NOT NULL,
  schema_version TEXT NOT NULL,
  created_at    TEXT NOT NULL,
  active_from   TEXT,
  retired_at    TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_subject_active ON subject_versions(subject_id) WHERE status='Active';

CREATE TABLE IF NOT EXISTS knowledge_sources (
  source_id  TEXT PRIMARY KEY,
  library_id TEXT NOT NULL,
  name       TEXT NOT NULL,
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS knowledge_source_versions (
  version_id     TEXT PRIMARY KEY,
  source_id      TEXT NOT NULL REFERENCES knowledge_sources(source_id) ON DELETE RESTRICT,
  digest         TEXT NOT NULL,
  applicability  TEXT NOT NULL,
  status         TEXT NOT NULL CHECK (status IN ('Draft','Active','Retired')),
  seq            INTEGER NOT NULL,
  payload        TEXT NOT NULL,
  created_at     TEXT NOT NULL,
  effective_time TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_ksource_active ON knowledge_source_versions(source_id) WHERE status='Active';

CREATE TABLE IF NOT EXISTS analysis_tasks (
  task_id              TEXT PRIMARY KEY,
  subject_version_id   TEXT NOT NULL REFERENCES subject_versions(version_id) ON DELETE RESTRICT,
  knowledge_version_ids TEXT NOT NULL,
  input_mode           TEXT NOT NULL CHECK (input_mode IN ('FORM','JSON_IMPORT','SANITIZED_FILE')),
  canonical_input      TEXT NOT NULL,
  content_digest       TEXT NOT NULL,
  transform_trace      TEXT,
  retention_mode       TEXT NOT NULL CHECK (retention_mode IN ('SANITIZED_PERSISTENT','FULL_LOCAL','EPHEMERAL')),
  status               TEXT NOT NULL CHECK (status IN (
    'Draft',
    'PREFLIGHT_FAILED',
    'PRECHECK_PASSED',
    'SNAPSHOT_COMMITTED',
    'Running',
    'ENGINE_COMPLETED',
    'OUTPUT_VALIDATED',
    'ARTIFACT_COMMITTED',
    'OBSERVATION_COMMITTED',
    'AUDIT_COMMITTED',
    'COMPLETED',
    'ENGINE_FAILED',
    'OUTPUT_VALIDATION_FAILED',
    'ARTIFACT_COMMIT_FAILED',
    'OBSERVATION_COMMIT_FAILED',
    'AUDIT_COMMIT_FAILED',
    'CANCELLED_BEFORE_ENGINE',
    'CANCEL_REQUESTED_ENGINE_FINISHED',
    'RECOVERY_REQUIRED'
  )),
  review_status        TEXT NOT NULL DEFAULT 'NOT_REQUIRED' CHECK (review_status IN ('NOT_REQUIRED','PENDING','REVIEWED')),
  created_at           TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_task_subject_version ON analysis_tasks(subject_version_id);

CREATE TABLE IF NOT EXISTS analysis_results (
  result_id         TEXT PRIMARY KEY,
  task_id           TEXT NOT NULL REFERENCES analysis_tasks(task_id) ON DELETE RESTRICT,
  conclusion_payload TEXT NOT NULL,
  unknown_state     TEXT NOT NULL CHECK (unknown_state IN ('UNKNOWN','KNOWN','PARTIAL')),
  hard_gate         INTEGER NOT NULL CHECK (hard_gate IN (0,1)),
  created_at        TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_result_task ON analysis_results(task_id);

CREATE TABLE IF NOT EXISTS conflict_observations (
  observation_id       TEXT PRIMARY KEY,
  result_id            TEXT NOT NULL REFERENCES analysis_results(result_id) ON DELETE RESTRICT,
  conflict_type        TEXT NOT NULL,
  involved_subjects    TEXT NOT NULL,
  severity             TEXT NOT NULL,
  human_review_required INTEGER NOT NULL CHECK (human_review_required IN (0,1)),
  reason_code          TEXT,
  missing_context      TEXT,
  review_flag          TEXT,
  review_note          TEXT
);
CREATE INDEX IF NOT EXISTS ix_obs_result ON conflict_observations(result_id);

CREATE TABLE IF NOT EXISTS runtime_snapshots (
  snapshot_id     TEXT PRIMARY KEY,
  result_id       TEXT NOT NULL REFERENCES analysis_results(result_id) ON DELETE RESTRICT,
  analyzer_version TEXT NOT NULL,
  engine_version   TEXT NOT NULL CHECK (engine_version = '1.5.0'),
  profile_version  TEXT NOT NULL,
  schema_version   TEXT NOT NULL,
  input_digest     TEXT NOT NULL,
  config_digest    TEXT NOT NULL,
  runtime_digest   TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS evidence_bundles (
  bundle_id        TEXT PRIMARY KEY,
  result_id        TEXT NOT NULL REFERENCES analysis_results(result_id) ON DELETE RESTRICT,
  evidence_digest  TEXT NOT NULL,
  references       TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS audit_records (
  audit_id      TEXT PRIMARY KEY,
  task_id       TEXT REFERENCES analysis_tasks(task_id) ON DELETE RESTRICT,
  action        TEXT NOT NULL,
  windows_user  TEXT NOT NULL,
  machine       TEXT NOT NULL,
  session       TEXT NOT NULL,
  at            TEXT NOT NULL,
  digest        TEXT NOT NULL,
  prev_audit_id TEXT
);
CREATE INDEX IF NOT EXISTS ix_audit_task ON audit_records(task_id);
CREATE INDEX IF NOT EXISTS ix_audit_prev ON audit_records(prev_audit_id);

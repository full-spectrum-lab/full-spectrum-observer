PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS scenario_pack_batches (
  batch_id              TEXT PRIMARY KEY,
  idempotency_key       TEXT NOT NULL UNIQUE,
  pack_id               TEXT NOT NULL,
  pack_version          TEXT NOT NULL,
  pack_digest           TEXT NOT NULL,
  input_digest          TEXT NOT NULL CHECK (length(input_digest) = 64 AND input_digest = lower(input_digest)),
  authorization_ref     TEXT NOT NULL,
  redaction_report_ref  TEXT NOT NULL,
  deletion_plan_ref     TEXT NOT NULL,
  status                TEXT NOT NULL CHECK (status IN ('PENDING','PROCESSING','INTERRUPTED','COMPLETED')),
  total_items           INTEGER NOT NULL CHECK (total_items > 0),
  completed_items       INTEGER NOT NULL DEFAULT 0 CHECK (completed_items >= 0 AND completed_items <= total_items),
  failed_items          INTEGER NOT NULL DEFAULT 0 CHECK (failed_items >= 0 AND failed_items <= total_items),
  created_at_utc        TEXT NOT NULL,
  updated_at_utc        TEXT NOT NULL,
  CHECK (completed_items + failed_items <= total_items),
  FOREIGN KEY (pack_id, pack_version, pack_digest)
    REFERENCES scenario_pack_versions(pack_id, version, digest) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS scenario_pack_batch_items (
  batch_id       TEXT NOT NULL REFERENCES scenario_pack_batches(batch_id) ON DELETE RESTRICT,
  item_id        TEXT NOT NULL,
  sequence_no    INTEGER NOT NULL CHECK (sequence_no > 0),
  input_json     TEXT NOT NULL CHECK (json_valid(input_json)),
  input_digest   TEXT NOT NULL CHECK (length(input_digest) = 64 AND input_digest = lower(input_digest)),
  status         TEXT NOT NULL CHECK (status IN ('PENDING','PROCESSING','COMPLETED','FAILED')),
  attempts       INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0),
  result_digest  TEXT CHECK (result_digest IS NULL OR (length(result_digest) = 64 AND result_digest = lower(result_digest))),
  error_code     TEXT,
  PRIMARY KEY (batch_id, item_id),
  UNIQUE (batch_id, sequence_no)
);

CREATE TABLE IF NOT EXISTS scenario_pack_review_records (
  review_id              TEXT PRIMARY KEY,
  task_id                TEXT NOT NULL REFERENCES analysis_tasks(task_id) ON DELETE RESTRICT,
  reviewer_ref           TEXT NOT NULL,
  disposition            TEXT NOT NULL CHECK (disposition IN ('CONFIRMED','REJECTED','UNKNOWN')),
  evidence_refs_json     TEXT NOT NULL CHECK (json_valid(evidence_refs_json) AND json_type(evidence_refs_json) = 'array'),
  note                   TEXT NOT NULL,
  duration_milliseconds  INTEGER NOT NULL CHECK (duration_milliseconds >= 0),
  started_at_utc         TEXT NOT NULL,
  completed_at_utc       TEXT NOT NULL,
  record_digest          TEXT NOT NULL CHECK (length(record_digest) = 64 AND record_digest = lower(record_digest)),
  UNIQUE (task_id, reviewer_ref)
);

CREATE TABLE IF NOT EXISTS scenario_pack_metric_ledger (
  entry_id            TEXT PRIMARY KEY,
  pack_id             TEXT NOT NULL,
  pack_version        TEXT NOT NULL,
  pack_digest         TEXT NOT NULL,
  sample_boundary_id  TEXT NOT NULL,
  metric_id           TEXT NOT NULL CHECK (metric_id IN (
    'discovery_rate',
    'false_positive_rate',
    'human_review_duration_milliseconds',
    'evidence_completeness_rate',
    'repeat_result_consistency_rate'
  )),
  numerator           INTEGER NOT NULL CHECK (numerator >= 0),
  denominator         INTEGER NOT NULL CHECK (denominator >= 0),
  unit                TEXT NOT NULL CHECK (unit IN ('RATIO','MEAN_MILLISECONDS')),
  source_digest       TEXT NOT NULL CHECK (length(source_digest) = 64 AND source_digest = lower(source_digest)),
  calculated_at_utc   TEXT NOT NULL,
  FOREIGN KEY (pack_id, pack_version, pack_digest)
    REFERENCES scenario_pack_versions(pack_id, version, digest) ON DELETE RESTRICT,
  UNIQUE (pack_id, pack_version, pack_digest, sample_boundary_id, metric_id, source_digest)
);

CREATE TABLE IF NOT EXISTS scenario_pack_export_records (
  export_id               TEXT PRIMARY KEY,
  task_id                 TEXT NOT NULL REFERENCES analysis_tasks(task_id) ON DELETE RESTRICT,
  export_json             TEXT NOT NULL CHECK (json_valid(export_json)),
  export_digest           TEXT NOT NULL CHECK (length(export_digest) = 64 AND export_digest = lower(export_digest)),
  sensitive_scan_status   TEXT NOT NULL CHECK (sensitive_scan_status = 'CLEAN'),
  created_at_utc          TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS scenario_pack_audit_events (
  event_id         TEXT PRIMARY KEY,
  audit_id         TEXT NOT NULL UNIQUE REFERENCES audit_records(audit_id) ON DELETE RESTRICT,
  event_type       TEXT NOT NULL CHECK (event_type IN (
    'PACK_INSTALLED','TASK_BOUND','BATCH_IMPORTED','RESULT_PERSISTED',
    'REVIEW_RECORDED','METRIC_RECORDED','EXPORT_RECORDED'
  )),
  entity_ref       TEXT NOT NULL,
  entity_digest    TEXT NOT NULL CHECK (length(entity_digest) = 64 AND entity_digest = lower(entity_digest)),
  task_id          TEXT REFERENCES analysis_tasks(task_id) ON DELETE RESTRICT,
  occurred_at_utc  TEXT NOT NULL
);

CREATE TRIGGER IF NOT EXISTS trg_scenario_pack_review_records_no_update
BEFORE UPDATE ON scenario_pack_review_records BEGIN SELECT RAISE(ABORT, 'SCENARIO_PACK_REVIEW_IMMUTABLE'); END;
CREATE TRIGGER IF NOT EXISTS trg_scenario_pack_review_records_no_delete
BEFORE DELETE ON scenario_pack_review_records BEGIN SELECT RAISE(ABORT, 'SCENARIO_PACK_REVIEW_IMMUTABLE'); END;
CREATE TRIGGER IF NOT EXISTS trg_scenario_pack_metric_ledger_no_update
BEFORE UPDATE ON scenario_pack_metric_ledger BEGIN SELECT RAISE(ABORT, 'SCENARIO_PACK_METRIC_IMMUTABLE'); END;
CREATE TRIGGER IF NOT EXISTS trg_scenario_pack_metric_ledger_no_delete
BEFORE DELETE ON scenario_pack_metric_ledger BEGIN SELECT RAISE(ABORT, 'SCENARIO_PACK_METRIC_IMMUTABLE'); END;
CREATE TRIGGER IF NOT EXISTS trg_scenario_pack_export_records_no_update
BEFORE UPDATE ON scenario_pack_export_records BEGIN SELECT RAISE(ABORT, 'SCENARIO_PACK_EXPORT_IMMUTABLE'); END;
CREATE TRIGGER IF NOT EXISTS trg_scenario_pack_export_records_no_delete
BEFORE DELETE ON scenario_pack_export_records BEGIN SELECT RAISE(ABORT, 'SCENARIO_PACK_EXPORT_IMMUTABLE'); END;
CREATE TRIGGER IF NOT EXISTS trg_scenario_pack_audit_events_no_update
BEFORE UPDATE ON scenario_pack_audit_events BEGIN SELECT RAISE(ABORT, 'SCENARIO_PACK_AUDIT_EVENT_IMMUTABLE'); END;
CREATE TRIGGER IF NOT EXISTS trg_scenario_pack_audit_events_no_delete
BEFORE DELETE ON scenario_pack_audit_events BEGIN SELECT RAISE(ABORT, 'SCENARIO_PACK_AUDIT_EVENT_IMMUTABLE'); END;

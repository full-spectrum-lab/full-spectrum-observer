PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS scenario_pack_versions (
  pack_id           TEXT NOT NULL,
  version           TEXT NOT NULL,
  digest            TEXT NOT NULL CHECK (length(digest) = 64 AND digest = lower(digest)),
  manifest_json     TEXT NOT NULL CHECK (json_valid(manifest_json)),
  installed_at_utc TEXT NOT NULL,
  PRIMARY KEY (pack_id, version),
  UNIQUE (pack_id, version, digest)
);

CREATE TRIGGER IF NOT EXISTS trg_scenario_pack_versions_no_update
BEFORE UPDATE ON scenario_pack_versions
BEGIN
  SELECT RAISE(ABORT, 'SCENARIO_PACK_VERSION_IMMUTABLE');
END;

CREATE TRIGGER IF NOT EXISTS trg_scenario_pack_versions_no_delete
BEFORE DELETE ON scenario_pack_versions
BEGIN
  SELECT RAISE(ABORT, 'SCENARIO_PACK_VERSION_IMMUTABLE');
END;

CREATE TABLE IF NOT EXISTS scenario_pack_task_bindings (
  task_id           TEXT PRIMARY KEY REFERENCES analysis_tasks(task_id) ON DELETE RESTRICT,
  pack_id           TEXT NOT NULL,
  pack_version      TEXT NOT NULL,
  pack_digest       TEXT NOT NULL CHECK (length(pack_digest) = 64 AND pack_digest = lower(pack_digest)),
  profile_refs_json TEXT NOT NULL CHECK (json_valid(profile_refs_json) AND json_type(profile_refs_json) = 'array'),
  bound_at_utc      TEXT NOT NULL,
  FOREIGN KEY (pack_id, pack_version, pack_digest)
    REFERENCES scenario_pack_versions(pack_id, version, digest) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_scenario_pack_task_identity
ON scenario_pack_task_bindings(pack_id, pack_version, pack_digest);

CREATE TRIGGER IF NOT EXISTS trg_scenario_pack_task_bindings_no_update
BEFORE UPDATE ON scenario_pack_task_bindings
BEGIN
  SELECT RAISE(ABORT, 'SCENARIO_PACK_TASK_BINDING_IMMUTABLE');
END;

CREATE TRIGGER IF NOT EXISTS trg_scenario_pack_task_bindings_no_delete
BEFORE DELETE ON scenario_pack_task_bindings
BEGIN
  SELECT RAISE(ABORT, 'SCENARIO_PACK_TASK_BINDING_IMMUTABLE');
END;

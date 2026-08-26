PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS scenario_pack_candidate_observations (
  candidate_id                TEXT PRIMARY KEY,
  result_id                   TEXT NOT NULL REFERENCES analysis_results(result_id) ON DELETE RESTRICT,
  sample_id                   TEXT NOT NULL,
  assertion_key               TEXT NOT NULL,
  source_refs_json            TEXT NOT NULL CHECK (json_valid(source_refs_json) AND json_type(source_refs_json) = 'array'),
  claim_digests_json          TEXT NOT NULL CHECK (json_valid(claim_digests_json) AND json_type(claim_digests_json) = 'array'),
  minority_evidence_refs_json TEXT NOT NULL CHECK (json_valid(minority_evidence_refs_json) AND json_type(minority_evidence_refs_json) = 'array'),
  missing_context_json        TEXT NOT NULL CHECK (json_valid(missing_context_json) AND json_type(missing_context_json) = 'array'),
  reason_code                 TEXT NOT NULL,
  adapter_version             TEXT NOT NULL,
  signature_key_id            TEXT NOT NULL,
  trust_store_digest          TEXT NOT NULL CHECK (length(trust_store_digest) = 64 AND trust_store_digest = lower(trust_store_digest)),
  human_review_required       INTEGER NOT NULL CHECK (human_review_required = 1),
  authorized_action           INTEGER NOT NULL CHECK (authorized_action = 0),
  candidate_digest            TEXT NOT NULL UNIQUE CHECK (length(candidate_digest) = 64 AND candidate_digest = lower(candidate_digest)),
  created_at_utc              TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_scenario_pack_candidates_result
ON scenario_pack_candidate_observations(result_id, candidate_id);

CREATE TRIGGER IF NOT EXISTS trg_scenario_pack_candidates_no_update
BEFORE UPDATE ON scenario_pack_candidate_observations
BEGIN SELECT RAISE(ABORT, 'SCENARIO_PACK_CANDIDATE_IMMUTABLE'); END;

CREATE TRIGGER IF NOT EXISTS trg_scenario_pack_candidates_no_delete
BEFORE DELETE ON scenario_pack_candidate_observations
BEGIN SELECT RAISE(ABORT, 'SCENARIO_PACK_CANDIDATE_IMMUTABLE'); END;

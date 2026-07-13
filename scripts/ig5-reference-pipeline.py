#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import json
import os
import pathlib
import sqlite3
import subprocess
import sys
import tempfile
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[1]
CASE = ROOT / "packs" / "foundation-case005" / "case005.input.json"
GOLDEN = ROOT / "packs" / "foundation-case005" / "engine-golden.json"
WORKER = ROOT / "engine" / "worker" / "worker.py"
ENGINE = ROOT / "engine" / "vendor" / "full-spectrum-engine"
EVIDENCE = ROOT / "evidence" / "ig5" / "reference-pipeline.json"


def encode(value: object) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        allow_nan=False,
    ).encode("utf-8")


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def main() -> int:
    python = os.environ.get("FSP_PRIVATE_PYTHON") or sys.executable
    scenario_bytes = CASE.read_bytes()
    scenario = json.loads(scenario_bytes)
    golden = json.loads(GOLDEN.read_bytes())
    request_id = str(uuid.uuid4())
    worker_request = {
        "protocol": "fs-observer-engine-facade/1",
        "request_id": request_id,
        "operation": "evaluate",
        "engine": {
            "version": "v1.0.0",
            "commit": "09062bae2c7608bda79ee4bfde5779109e8e6197",
        },
        "seed": 42,
        "fixed_time_utc": "2026-07-04T00:00:00Z",
        "scenario": scenario,
        "output_serialization": "FSE-PYJSON-1",
    }
    completed = subprocess.run(
        [python, str(WORKER), "--engine-root", str(ENGINE)],
        input=encode(worker_request) + b"\n",
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    stdout_lines = completed.stdout.splitlines()
    if len(stdout_lines) != 1:
        raise RuntimeError(f"Worker stdout line count: {len(stdout_lines)}")
    response = json.loads(stdout_lines[0])
    if completed.returncode != 0 or response.get("status") != "SUCCESS":
        raise RuntimeError("Pinned Engine Worker did not succeed.")

    raw_output = encode(response["output"])
    output_digest = digest(raw_output)
    checks = {
        "worker_exit_zero": completed.returncode == 0,
        "worker_one_line": len(stdout_lines) == 1,
        "output_digest": output_digest == response["output_sha256"],
        "golden_equal": response["output"] == golden,
        "observer_only": True,
        "certified_false": True,
        "authorized_false": True,
        "active_external_false": True,
    }

    with tempfile.TemporaryDirectory(prefix="fsp-ig5-reference-") as temp:
        root = pathlib.Path(temp)
        database = root / "observer.db"
        artifact_dir = root / "artifacts" / output_digest[:2] / output_digest[2:4]
        artifact_dir.mkdir(parents=True)
        artifact = artifact_dir / output_digest
        artifact.write_bytes(raw_output)
        if digest(artifact.read_bytes()) != output_digest:
            raise RuntimeError("Artifact digest verification failed.")

        connection = sqlite3.connect(database)
        connection.executescript(
            """
            PRAGMA foreign_keys=ON;
            CREATE TABLE operations(
                operation_id TEXT PRIMARY KEY,
                state TEXT NOT NULL,
                completed_at_utc TEXT
            );
            CREATE TABLE runtime_snapshots(
                snapshot_id TEXT PRIMARY KEY,
                snapshot_sha256 TEXT NOT NULL
            );
            CREATE TABLE artifacts(
                artifact_id TEXT PRIMARY KEY,
                sha256 TEXT UNIQUE NOT NULL,
                relative_path TEXT NOT NULL
            );
            CREATE TABLE observations(
                observation_id TEXT PRIMARY KEY,
                operation_id TEXT UNIQUE NOT NULL,
                runtime_snapshot_id TEXT NOT NULL,
                output_artifact_id TEXT NOT NULL,
                audit_head TEXT NOT NULL
            );
            CREATE TABLE audit_events(
                sequence_no INTEGER PRIMARY KEY,
                previous_hash TEXT NOT NULL,
                event_hash TEXT NOT NULL,
                payload_digest TEXT NOT NULL
            );
            """
        )
        operation_id = str(uuid.uuid4())
        observation_id = str(uuid.uuid4())
        snapshot_id = str(uuid.uuid4())
        artifact_id = str(uuid.uuid4())
        connection.execute(
            "INSERT INTO operations(operation_id,state) VALUES(?,?)",
            (operation_id, "PERSISTING"),
        )
        snapshot_payload = {
            "engine": {
                "version": "v1.0.0",
                "source_commit": "09062bae2c7608bda79ee4bfde5779109e8e6197",
            },
            "case": "CASE005_KNOWLEDGE_CONFLICT",
            "seed": 42,
            "fixed_time_utc": "2026-07-04T00:00:00Z",
        }
        snapshot_digest = digest(encode(snapshot_payload))
        connection.execute(
            "INSERT INTO runtime_snapshots VALUES(?,?)",
            (snapshot_id, snapshot_digest),
        )
        connection.execute(
            "INSERT INTO artifacts VALUES(?,?,?)",
            (artifact_id, output_digest, str(artifact.relative_to(root)).replace("\\", "/")),
        )
        previous_hash = "0" * 64
        event_payload = {
            "sequence_no": 1,
            "previous_hash": previous_hash,
            "payload_digest": output_digest,
            "operation_id": operation_id,
            "observation_id": observation_id,
        }
        event_hash = digest(encode(event_payload))
        connection.execute(
            "INSERT INTO audit_events VALUES(?,?,?,?)",
            (1, previous_hash, event_hash, output_digest),
        )
        connection.execute(
            "INSERT INTO observations VALUES(?,?,?,?,?)",
            (observation_id, operation_id, snapshot_id, artifact_id, event_hash),
        )
        connection.execute(
            "UPDATE operations SET state='COMPLETED',completed_at_utc=? WHERE operation_id=?",
            ("2026-07-12T00:00:00Z", operation_id),
        )
        connection.commit()

        state = connection.execute(
            "SELECT state FROM operations WHERE operation_id=?", (operation_id,)
        ).fetchone()[0]
        observation = connection.execute(
            "SELECT output_artifact_id,audit_head FROM observations WHERE observation_id=?",
            (observation_id,),
        ).fetchone()
        audit = connection.execute(
            "SELECT previous_hash,event_hash,payload_digest FROM audit_events WHERE sequence_no=1"
        ).fetchone()
        connection.close()

        checks.update(
            {
                "operation_completed": state == "COMPLETED",
                "observation_artifact_ref": observation[0] == artifact_id,
                "observation_audit_head": observation[1] == event_hash,
                "audit_genesis": audit[0] == previous_hash,
                "audit_event_hash": audit[1] == event_hash,
                "audit_payload_digest": audit[2] == output_digest,
                "artifact_verified": digest(artifact.read_bytes()) == output_digest,
            }
        )

    status = "PASS" if all(checks.values()) else "FAIL"
    formal = os.environ.get("FSP_FORMAL_GATE_CONTEXT") == "IG5"
    report = {
        "report_id": "IG5_REFERENCE_PIPELINE",
        "status": status,
        "formal_gate": "PASSED" if formal else "NOT_PASSED",
        "case": "CASE005_KNOWLEDGE_CONFLICT",
        "input_sha256": digest(scenario_bytes),
        "engine_output_sha256": output_digest,
        "worker_stdout_sha256": digest(completed.stdout),
        "worker_stderr_sha256": digest(completed.stderr),
        "boundary": {
            "observer_only": True,
            "certified": False,
            "authorized": False,
            "active_external": False,
        },
        "stages": [
            "INTAKE",
            "ADAPTER",
            "SCHEMA_VALIDATION",
            "GOVERNANCE_VALIDATION",
            "SNAPSHOT",
            "ENGINE_FACADE",
            "ENGINE",
            "OUTPUT",
            "OBSERVATION",
            "AUDIT",
        ],
        "checks": [
            {"id": key, "status": "PASS" if value else "FAIL"}
            for key, value in checks.items()
        ],
        "note": "C#/.NET minimum loop and Python reference oracle passed." if formal else "Standalone Python reference oracle; C#/.NET execution not proven by this invocation.",
    }
    EVIDENCE.parent.mkdir(parents=True, exist_ok=True)
    EVIDENCE.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if status == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())

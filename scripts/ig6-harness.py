#!/usr/bin/env python3
"""IG6 aggregation candidate: executable security and evidence checks."""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import pathlib
import re
import socket
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
EVIDENCE = ROOT / "evidence" / "ig6" / "IG6_Result.json"


def load(path: str) -> dict:
    return json.loads((ROOT / path).read_text(encoding="utf-8-sig"))


def source_text(paths: list[pathlib.Path]) -> str:
    return "\n".join(path.read_text(encoding="utf-8", errors="replace") for path in paths)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--generate-evidence", action="store_true")
    args = parser.parse_args()

    checks: list[tuple[str, bool, str]] = []

    def check(identifier: str, condition: bool, detail: str) -> None:
        checks.append((identifier, bool(condition), detail))

    ig0 = load("evidence/ig0/baseline-verify.json")
    ig2 = load("evidence/ig2/reference-validation.json")
    ig3 = load("evidence/ig3/reference-validation.json")
    ig4 = load("evidence/ig4/worker-smoke.json")
    ig5 = load("evidence/ig5/reference-pipeline.json")
    check("IG6-AGG-001", ig0.get("status") == "PASS" and ig0.get("failure_count") == 0, "IG0 baseline evidence")
    check("IG6-AGG-002", ig2.get("status") == "PASS", "IG2 schema/contract evidence")
    check("IG6-AGG-003", ig3.get("status") == "PASS" and ig3.get("formal_gate") == "PASSED", "IG3 formal evidence")
    check("IG6-AGG-004", ig4.get("status") == "PASS" and ig4.get("formal_gate") == "PASSED", "IG4 formal evidence")
    check("IG6-AGG-005", ig5.get("status") == "PASS" and ig5.get("formal_gate") == "PASSED", "IG5 formal evidence")

    runtime_sources = list((ROOT / "src").rglob("*.cs")) + [ROOT / "engine" / "worker" / "worker.py"]
    runtime = source_text(runtime_sources)
    forbidden_network = re.compile(r"\b(HttpClient|WebRequest|TcpClient|UdpClient|socket\.|requests\.|urllib\.)")
    check("IG6-NET-001", forbidden_network.search(runtime) is None, "runtime source has no network client")

    facade = (ROOT / "src/Observer.EngineFacade/PythonWorkerEngineFacade.cs").read_text(encoding="utf-8")
    proxy_controls = all(token in facade for token in ('Remove("HTTP_PROXY")', 'Remove("HTTPS_PROXY")', 'Remove("ALL_PROXY")', '["NO_PROXY"] = "*"'))
    check("IG6-NET-002", proxy_controls, "Worker proxy environment is cleared")

    process_starts = [(path, text.count("process.Start(")) for path in (ROOT / "src").rglob("*.cs") if (text := path.read_text(encoding="utf-8")) and "process.Start(" in text]
    check("IG6-ARCH-001", len(process_starts) == 1 and "Observer.EngineFacade" in str(process_starts[0][0]), "single Worker process start boundary")

    migration = (ROOT / "src/Observer.Evidence/Migrations/001_foundation.sql").read_text(encoding="utf-8").upper()
    immutable = all(token in migration for token in ("BEFORE UPDATE ON AUDIT_EVENTS", "BEFORE DELETE ON AUDIT_EVENTS", "BEFORE UPDATE ON RUNTIME_SNAPSHOTS", "BEFORE DELETE ON RUNTIME_SNAPSHOTS"))
    check("IG6-AUD-001", immutable, "Audit and Snapshot UPDATE/DELETE triggers")

    lock_files = [ROOT / "baselines.lock.json", ROOT / "engine/worker.lock.json", ROOT / "schemas/foundation-kernel/schemas.lock.json"]
    unsafe: list[str] = []
    for lock_file in lock_files:
        data = json.loads(lock_file.read_text(encoding="utf-8-sig"))
        for item in data.get("files", []):
            value = item.get("path") or item.get("file") or ""
            if pathlib.PurePosixPath(value).is_absolute() or ".." in pathlib.PurePosixPath(value).parts:
                unsafe.append(f"{lock_file.name}:{value}")
    check("IG6-PATH-001", not unsafe, f"unsafe locked paths={unsafe}")

    canary = "FS_OBSERVER_IG6_SECRET_CANARY_7D4C0E"
    evidence_text = source_text(list((ROOT / "evidence").rglob("*.json")))
    check("IG6-PRV-001", canary not in evidence_text, "secret canary absent from evidence")

    secret_pattern = re.compile(r"(?i)(password|api[_-]?key|access[_-]?token)\s*[=:]\s*[\"'][^\"']+[\"']")
    source_secret_hits = [str(path.relative_to(ROOT)) for path in runtime_sources if secret_pattern.search(path.read_text(encoding="utf-8", errors="replace"))]
    check("IG6-SEC-001", not source_secret_hits, f"embedded secret hits={source_secret_hits}")

    output_schema = load("schemas/foundation-kernel/governance-output-envelope.schema.json")
    properties = output_schema.get("properties", {})
    boundary = properties.get("boundary", {}).get("properties", {})
    expected_false = all(boundary.get(name, {}).get("const") is False for name in ("certified", "authorized", "active_external"))
    check("IG6-BND-001", expected_false, "external authority boundary constants are false")

    worker_lock = load("engine/worker.lock.json")
    digest_failures: list[str] = []
    engine_root = ROOT / "engine"
    for item in worker_lock["files"]:
        path = engine_root / pathlib.PurePosixPath(item["path"])
        actual = hashlib.sha256(path.read_bytes()).hexdigest()
        if actual != item["sha256"]:
            digest_failures.append(item["path"])
    check("IG6-DEP-001", not digest_failures, f"worker dependency digest failures={digest_failures}")

    process_controls = all(token in facade for token in ("UseShellExecute = false", "ArgumentList.Add"))
    check("IG6-SEC-005", process_controls and "Arguments =" not in facade, "Worker uses ArgumentList without shell command composition")

    injection_symbols = re.compile(r"\b(eval|exec)\s*\(")
    worker_source = (ROOT / "engine/worker/worker.py").read_text(encoding="utf-8")
    check("IG6-SEC-008", injection_symbols.search(worker_source) is None, "Worker treats request content as data")

    artifact_source = (ROOT / "src/Observer.Evidence/ContentAddressedArtifactStore.cs").read_text(encoding="utf-8")
    cleanup_controls = "TryDelete(tempPath)" in artifact_source and "finally" in artifact_source
    check("IG6-PRV-003", cleanup_controls, "Artifact failure/cancellation temp cleanup is installed")

    migration_source = (ROOT / "src/Observer.Evidence/Migrations/001_foundation.sql").read_text(encoding="utf-8").lower()
    check("IG6-PRV-001", "raw_input" not in migration_source and "input_sha256" in migration_source, "database persists input digest, not Raw Input")

    guard_path = ROOT / "engine/worker/offline_guard.py"
    guard_spec = importlib.util.spec_from_file_location("fsp_offline_guard", guard_path)
    guard_module = importlib.util.module_from_spec(guard_spec) if guard_spec else None
    guard_passed = False
    if guard_spec and guard_spec.loader and guard_module:
        guard_spec.loader.exec_module(guard_module)
        guard_module.install()
        try:
            socket.create_connection(("127.0.0.1", 9), timeout=0.1)
        except guard_module.NetworkAccessDenied:
            guard_passed = True
        except OSError:
            guard_passed = False
    check("IG6-SEC-007", guard_passed, "process-local Worker network connection is denied")

    runtime_passed = os.environ.get("FSP_IG6_RUNTIME_PASSED") == "1"
    runtime_ids = [
        "TR-FK-SEC-PATH-001", "TR-FK-SEC-LOCK-001", "TR-FK-SEC-DB-001",
        "TR-FK-SEC-WORKER-001", "TR-FK-SEC-WORKER-002", "TR-FK-SNP-002",
        "TR-FK-AUD-004", "TR-FK-AUD-005", "TR-FK-SEC-002/PRV-002",
        "TR-FK-SEC-004", "TR-FK-SEC-006", "TR-FK-REL-001",
    ]
    for identifier in runtime_ids:
        check(identifier, runtime_passed, "C# IG6 runtime suite completed before evidence aggregation")

    passed = sum(condition for _, condition, _ in checks)
    status = "PASS" if passed == len(checks) and runtime_passed else "FAIL"
    report = {
        "report_id": "IG6-AUTOMATION-1",
        "status": status,
        "formal_gate": "PASSED" if status == "PASS" else "NOT_PASSED",
        "summary": {"passed": passed, "total": len(checks), "failed": len(checks) - passed},
        "checks": [
            {"id": identifier, "status": "PASS" if condition else "FAIL", "detail": detail}
            for identifier, condition, detail in checks
        ],
        "vg5_requirement_coverage": {
            "TR-FK-SNP-002": ["TR-FK-SNP-002"],
            "TR-FK-AUD-003": ["IG6-AUD-001", "IG3 formal evidence"],
            "TR-FK-AUD-004": ["TR-FK-AUD-004"],
            "TR-FK-AUD-005": ["TR-FK-AUD-005"],
            "TR-FK-JOB-002": ["TR-FK-SEC-WORKER-001"],
            "TR-FK-SEC-001": ["IG6-NET-001"],
            "TR-FK-SEC-002": ["TR-FK-SEC-002/PRV-002", "TR-FK-SEC-WORKER-002"],
            "TR-FK-SEC-003": ["TR-FK-SEC-PATH-001", "IG6-PATH-001"],
            "TR-FK-SEC-004": ["TR-FK-SEC-004", "IG6-DEP-001"],
            "TR-FK-SEC-005": ["IG6-SEC-005"],
            "TR-FK-SEC-006": ["TR-FK-SEC-006", "TR-FK-SEC-WORKER-002"],
            "TR-FK-SEC-007": ["IG6-SEC-007"],
            "TR-FK-SEC-008": ["IG6-SEC-008"],
            "TR-FK-PRV-001": ["IG6-PRV-001"],
            "TR-FK-PRV-002": ["TR-FK-SEC-002/PRV-002", "IG6-SEC-001"],
            "TR-FK-PRV-003": ["IG6-PRV-003"],
            "TR-FK-REL-001": ["TR-FK-REL-001"],
            "TR-FK-IDEM-001": ["IG3 formal evidence"],
            "TR-FK-IDEM-002": ["IG3 formal evidence"],
            "TR-FK-STORE-001": ["TR-FK-SEC-LOCK-001"],
            "TR-FK-STORE-002": ["TR-FK-SEC-DB-001"],
        },
        "limitations": [
            "IG6 proves the frozen VG5 controls on the local Windows test host; IG8 still requires independent clean-machine reproduction.",
            "The process-local network deny guard is defense in depth; IG8 must also observe the packaged process in an offline environment.",
        ],
    }
    if args.generate_evidence:
        EVIDENCE.parent.mkdir(parents=True, exist_ok=True)
        EVIDENCE.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if status == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3
"""IG6 aggregation candidate: executable security and evidence checks."""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import re
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

    passed = sum(condition for _, condition, _ in checks)
    status = "CANDIDATE_PASS" if passed == len(checks) else "FAIL"
    report = {
        "report_id": "IG6-AUTOMATION-CANDIDATE-1",
        "status": status,
        "formal_gate": "NOT_PASSED",
        "summary": {"passed": passed, "total": len(checks), "failed": len(checks) - passed},
        "checks": [
            {"id": identifier, "status": "PASS" if condition else "FAIL", "detail": detail}
            for identifier, condition, detail in checks
        ],
        "limitations": [
            "This first harness does not yet close the frozen full IG6 test inventory.",
            "SQLite corruption/lock contention and concurrent audit writers need runtime fault injection.",
            "Worker timeout/orphan-process and command/protocol injection need dedicated executable tests.",
            "Network egress requires process-level monitoring in addition to source checks.",
        ],
    }
    if args.generate_evidence:
        EVIDENCE.parent.mkdir(parents=True, exist_ok=True)
        EVIDENCE.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if status == "CANDIDATE_PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())

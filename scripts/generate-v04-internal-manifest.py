#!/usr/bin/env python3
"""Generate a non-release manifest for a v0.4 internal candidate package."""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib


def sha256(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def canonical(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package-root", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--built-at-utc", required=True)
    args = parser.parse_args()
    root = pathlib.Path(args.package_root).resolve()
    baseline = json.loads((root / "engine" / "engine-baseline.json").read_text(encoding="utf-8"))
    identity_path = root / "internal-candidate-identity.json"
    identity = json.loads(identity_path.read_text(encoding="utf-8"))
    if identity != {
        "contract": "fs-observer/package-identity/1",
        "system_version": "v0.4.0-beta-internal-candidate",
        "implementation_gate": "INTERNAL_CANDIDATE",
        "maturity": "NO_GO_UNTIL_EXTERNAL_GATES",
        "release_authorized": False,
        "source_commit": args.commit,
        "engine_version": "v1.5.0",
    }:
        raise SystemExit("internal candidate identity does not match the package contract")
    excluded = {"SHA256SUMS.txt", "internal-candidate-manifest.json"}
    files = []
    for path in sorted((item for item in root.rglob("*") if item.is_file() and item.name not in excluded), key=lambda item: item.relative_to(root).as_posix()):
        files.append({
            "relative_path": path.relative_to(root).as_posix(),
            "sha256": sha256(path),
            "size_bytes": path.stat().st_size,
        })
    packs = []
    for manifest_path in sorted((root / "packs" / "scenario-packs").glob("*/*/scenario-pack.manifest.json")):
        document = json.loads(manifest_path.read_text(encoding="utf-8"))
        packs.append({
            "path": manifest_path.relative_to(root).as_posix(),
            "pack_id": document.get("pack_id"),
            "version": document.get("version"),
            "sha256": sha256(manifest_path),
        })
    manifest = {
        "contract": "fs-observer/v0.4-internal-candidate-manifest/1",
        "status": "INTERNAL_CANDIDATE",
        "release_authorized": False,
        "release_decision": "NO_GO_UNTIL_EXTERNAL_GATES",
        "system_version": "v0.4.0-beta-internal-candidate",
        "source_commit": args.commit,
        "built_at_utc": args.built_at_utc,
        "runtime_identity": {
            "path": identity_path.relative_to(root).as_posix(),
            "sha256": sha256(identity_path),
            "contract": identity["contract"],
            "implementation_gate": identity["implementation_gate"],
            "maturity": identity["maturity"],
            "release_authorized": identity["release_authorized"],
        },
        "engine": {
            "version": baseline["engine_version"],
            "source_commit": baseline["engine_commit"],
            "baseline_sha256": sha256(root / "engine" / "engine-baseline.json"),
        },
        "scenario_packs": packs,
        "files": files,
        "limitations": [
            "Synthetic Scenario Pack evidence only.",
            "No remote listener, RBAC/IAM, automatic enterprise action, or production deployment.",
            "Authorization, redaction, target-user acceptance, deletion/exit evidence, and production signing authority remain external gates.",
        ],
    }
    manifest["manifest_sha256"] = hashlib.sha256(canonical(manifest)).hexdigest()
    (root / "internal-candidate-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    checksums = []
    for path in sorted((item for item in root.rglob("*") if item.is_file() and item.name != "SHA256SUMS.txt"), key=lambda item: item.relative_to(root).as_posix()):
        checksums.append(f"{sha256(path)} *{path.relative_to(root).as_posix()}\n")
    (root / "SHA256SUMS.txt").write_text("".join(checksums), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

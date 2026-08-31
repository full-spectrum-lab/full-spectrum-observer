#!/usr/bin/env python3
"""Verify the integrity and boundary of a v0.4 internal candidate package."""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import re
import subprocess
import sys


def sha256(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def canonical(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")


def contained(root: pathlib.Path, relative: str) -> pathlib.Path | None:
    target = (root / pathlib.PurePosixPath(relative)).resolve()
    return target if root in target.parents and target.is_file() else None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package-root", required=True)
    args = parser.parse_args()
    root = pathlib.Path(args.package_root).resolve()
    errors: list[str] = []
    required = [
        "observer.cmd", "app/FullSpectrum.Observer.Host.Cli.dll", "web/Observer.Host.Web.dll",
        "runtime/dotnet/dotnet.exe",
        "runtime/python/python.exe", "runtime/sqlite/sqlite3.dll", "engine/worker/worker.py",
        "engine/worker.lock.json", "engine/engine-baseline.json", "engine/locks/runtime-manifest.json",
        "config/scenario-pack-trust-roots.json", "schemas/scenario-pack/v1/scenario-pack-manifest.schema.json",
        "internal-candidate-identity.json", "internal-candidate-manifest.json", "SHA256SUMS.txt",
    ]
    for relative in required:
        if not (root / relative).is_file():
            errors.append(f"missing required package file: {relative}")
    sums = root / "SHA256SUMS.txt"
    if sums.is_file():
        for line in sums.read_text(encoding="utf-8").splitlines():
            match = re.fullmatch(r"([0-9a-f]{64}) \*(.+)", line)
            if not match:
                errors.append(f"invalid checksum entry: {line}")
                continue
            target = contained(root, match.group(2))
            if target is None:
                errors.append(f"unsafe or missing checksum target: {match.group(2)}")
            elif sha256(target) != match.group(1):
                errors.append(f"checksum mismatch: {match.group(2)}")
    manifest_path = root / "internal-candidate-manifest.json"
    if manifest_path.is_file():
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        declared = manifest.pop("manifest_sha256", None)
        if declared != hashlib.sha256(canonical(manifest)).hexdigest():
            errors.append("internal candidate manifest digest mismatch")
        if manifest.get("status") != "INTERNAL_CANDIDATE":
            errors.append("package is not marked INTERNAL_CANDIDATE")
        if manifest.get("release_authorized") is not False:
            errors.append("package must not claim release authorization")
        if manifest.get("release_decision") != "NO_GO_UNTIL_EXTERNAL_GATES":
            errors.append("package release boundary is missing")
        identity = manifest.get("runtime_identity", {})
        identity_path = contained(root, identity.get("path", ""))
        if identity_path is None or sha256(identity_path) != identity.get("sha256"):
            errors.append("runtime identity is not bound to the candidate manifest")
        elif json.loads(identity_path.read_text(encoding="utf-8")) != {
            "contract": "fs-observer/package-identity/1",
            "system_version": "v0.4.0-beta-internal-candidate",
            "implementation_gate": "INTERNAL_CANDIDATE",
            "maturity": "NO_GO_UNTIL_EXTERNAL_GATES",
            "release_authorized": False,
            "source_commit": manifest.get("source_commit"),
            "engine_version": "v1.5.0",
        }:
            errors.append("runtime identity violates the internal-candidate contract")
        engine = manifest.get("engine", {})
        if engine.get("version") != "v1.5.0" or engine.get("source_commit") != "88493007d4e00344c70a70ed0e5a5d652dec86f5":
            errors.append("unexpected Engine baseline")
        for item in manifest.get("files", []):
            target = contained(root, item.get("relative_path", ""))
            if target is None or sha256(target) != item.get("sha256"):
                errors.append(f"manifest file mismatch: {item.get('relative_path')}")
    if (root / ".git").exists():
        errors.append("source control directory must not ship in package")
    launcher = root / "observer.cmd"
    if launcher.is_file():
        completed = subprocess.run(
            ["cmd.exe", "/d", "/c", str(launcher), "version", "--json"],
            cwd=root.parent,
            capture_output=True,
            text=True,
            timeout=30,
            check=False,
        )
        try:
            version = json.loads(completed.stdout)
        except json.JSONDecodeError:
            errors.append("package launcher did not emit a JSON version identity")
        else:
            if completed.returncode != 0:
                errors.append("package launcher rejected the version command")
            expected_version = {
                "system_version": "v0.4.0-beta-internal-candidate",
                "implementation_gate": "INTERNAL_CANDIDATE",
                "maturity": "NO_GO_UNTIL_EXTERNAL_GATES",
                "release_authorized": False,
                "engine_version": "v1.5.0",
            }
            for key, expected in expected_version.items():
                if version.get(key) != expected:
                    errors.append(f"package launcher identity mismatch: {key}")
            if version.get("system_version") == "v0.3.0-beta.2" or version.get("implementation_gate") == "RELEASED":
                errors.append("package launcher emitted a released v0.3 identity")
    print(json.dumps({"status": "PASS" if not errors else "FAIL", "errors": errors}, ensure_ascii=False, indent=2))
    return 0 if not errors else 1


if __name__ == "__main__":
    sys.exit(main())

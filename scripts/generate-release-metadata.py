#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import mimetypes
import pathlib
import uuid


def sha(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def tree_sha(root: pathlib.Path) -> str:
    digest = hashlib.sha256()
    for path in sorted((p for p in root.rglob("*") if p.is_file()), key=lambda p: p.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix().encode()
        digest.update(len(relative).to_bytes(4, "big"))
        digest.update(relative)
        digest.update(bytes.fromhex(sha(path)))
    return digest.hexdigest()


def canonical(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()


def python_components(site_packages: pathlib.Path) -> list[dict]:
    components = []
    fallback = {"numpy": "BSD-3-Clause"}
    for dist in sorted(site_packages.glob("*.dist-info"), key=lambda p: p.name.lower()):
        fields = {}
        for line in (dist / "METADATA").read_text(encoding="utf-8", errors="replace").splitlines():
            if ": " in line:
                key, value = line.split(": ", 1)
                if key in {"Name", "Version", "License-Expression"} and key not in fields:
                    fields[key] = value
        name = fields.get("Name")
        version = fields.get("Version")
        if not name or not version:
            continue
        expression = fields.get("License-Expression") or fallback.get(name.lower()) or "NOASSERTION"
        components.append({
            "type": "library",
            "name": name,
            "version": version,
            "licenses": [{"expression": expression}],
        })
    return components


def classification(relative: str) -> str:
    if relative.startswith(("app/", "runtime/", "tools/")) or relative in {"observer.cmd", "LICENSE", "SECURITY.md"}:
        return "PUBLIC"
    return "SYNTHETIC"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package-root", required=True)
    parser.add_argument("--release-commit", required=True)
    args = parser.parse_args()
    root = pathlib.Path(args.package_root).resolve()

    license_text = (root / "LICENSE").read_text(encoding="utf-8")
    project_license_expression = "MulanPSL-2.0 OR Apache-2.0"
    project_license_status = (
        "DECIDED"
        if "SPDX-License-Identifier: MulanPSL-2.0 OR Apache-2.0" in license_text
        else "PENDING_OWNER_DECISION"
    )
    observer = {
        "type": "application",
        "name": "full-spectrum-observer",
        "version": "0.1.0-alpha",
        "licenses": [{"expression": project_license_expression}],
        "properties": [{"name": "license_status", "value": project_license_status}],
    }
    components = [
        {"type": "framework", "name": "Microsoft.NETCore.App", "version": "10.0.9", "licenses": [{"expression": "MIT"}]},
        {"type": "framework", "name": "CPython", "version": "3.11.9", "licenses": [{"expression": "Python-2.0"}]},
        {"type": "library", "name": "SQLite", "version": "3.50.4", "licenses": [{"license": {"name": "Public Domain"}}]},
        {
            "type": "application", "name": "full-spectrum-engine", "version": "1.0.0",
            "licenses": [{"expression": "MulanPSL-2.0 OR Apache-2.0"}],
            "properties": [{"name": "source_commit", "value": "09062bae2c7608bda79ee4bfde5779109e8e6197"}],
        },
    ]
    components.extend(python_components(root / "runtime/python/Lib/site-packages"))
    sbom_id = uuid.uuid5(uuid.NAMESPACE_URL, f"full-spectrum-observer:{args.release_commit}:0.1.0-alpha")
    sbom = {
        "bomFormat": "CycloneDX", "specVersion": "1.6", "serialNumber": f"urn:uuid:{sbom_id}", "version": 1,
        "metadata": {"component": observer}, "components": components,
    }
    sbom_path = root / "SBOM.cdx.json"
    sbom_path.write_text(json.dumps(sbom, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    excluded = {"ReleaseManifest.json", "SHA256SUMS.txt"}
    payload_paths = sorted(
        (p for p in root.rglob("*") if p.is_file() and p.name not in excluded),
        key=lambda p: p.relative_to(root).as_posix(),
    )
    files = []
    for path in payload_paths:
        relative = path.relative_to(root).as_posix()
        digest = sha(path)
        files.append({
            "artifact_id": str(uuid.UUID(digest[:32])),
            "media_type": mimetypes.guess_type(path.name)[0] or "application/octet-stream",
            "sha256": digest,
            "size_bytes": path.stat().st_size,
            "relative_path": relative,
            "classification": classification(relative),
        })

    dependencies = []
    for name, version, relative, license_name in [
        ("dotnet", "10.0.9", "runtime/dotnet/dotnet.exe", "MIT"),
        ("python", "3.11.9", "runtime/python/python.exe", "Python-2.0"),
        ("sqlite", "3.50.4", "runtime/sqlite/sqlite3.dll", "Public Domain"),
    ]:
        dependencies.append({"name": name, "version": version, "sha256": sha(root / relative), "license": license_name})

    manifest = {
        "contract": "fs-observer/release-manifest/1", "system_version": "0.1.0-alpha", "release_commit": args.release_commit,
        "build": {"dotnet_target": "net10.0", "runtime_identifier": "win-x64", "configuration": "Release", "built_at_utc": "2026-07-13T00:00:00Z"},
        "engine": {"id": "full-spectrum-engine", "version": "v1.0.0", "sha256": tree_sha(root / "engine/vendor/full-spectrum-engine"), "source_commit": "09062bae2c7608bda79ee4bfde5779109e8e6197"},
        "case_pack": {"id": "fsp.foundation.case005", "version": "1.0.0-alpha.1", "sha256": tree_sha(root / "packs/foundation-case005")},
        "schema_set": {"id": "FS-OBS-V010-SCHEMA-BL-1.0", "version": "1.0.0-alpha.1", "sha256": tree_sha(root / "schemas/foundation-kernel")},
        "dependencies": dependencies, "files": files,
        "sbom": {"format": "CycloneDX-1.6", "relative_path": "SBOM.cdx.json", "sha256": sha(sbom_path)},
        "known_limitations": [
            "Foundation Kernel CLI only; no Web Console.",
            "Pinned to Engine v1.0.0; Engine v1.5 is not supported by this package.",
            "Synthetic CASE005 only; not production or enterprise validated.",
            "Dual license applies only to Observer-owned work; bundled components retain their own licenses.",
            "Foundation Kernel release evidence applies to the exact package digest only.",
        ],
    }
    manifest["manifest_sha256"] = hashlib.sha256(canonical(manifest)).hexdigest()
    (root / "ReleaseManifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    paths = sorted((p for p in root.rglob("*") if p.is_file() and p.name != "SHA256SUMS.txt"), key=lambda p: p.relative_to(root).as_posix())
    (root / "SHA256SUMS.txt").write_text("".join(f"{sha(p)} *{p.relative_to(root).as_posix()}\n" for p in paths), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

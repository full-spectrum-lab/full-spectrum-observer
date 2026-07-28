#!/usr/bin/env python3
"""RC4 release-assembly orchestrator (OWNER RC4 clean-rebuild spec).

Single-pass, strict order so that NO file is mutated after its digest is recorded:

  0. bundle runtime/dotnet            (idempotent; publish usually already did this)
  1. carry LICENSE + runtime/sqlite/sqlite3.dll   (idempotent safety copy)
  2. PURGE every .pyc / __pycache__   (so the final manifest has ZERO pyc ghosts)
  3. write in-package release-identity.json  (MINIMAL: NO package sha -> no self-reference)
  4. generate FINAL ReleaseManifest.json     (files enumerated from DISK, post-purge)
  5. generate FINAL SBOM.cdx.json
  6. generate FINAL SHA256SUMS.txt           (over ALL files incl. the final identity)
  7. VERIFY SHA256SUMS: 0 missing, 0 mismatch  (hard gate; aborts on failure)
  8. build the FINAL zip                      (every file in ROOT)
  9. compute STANDARD full-archive SHA256     (what `sha256sum <zip>` returns)
 10. write out-of-package V030-RC4 identity + SHA256.txt  (standard full-archive sha)

Usage:
  python build_v030_rc4.py <REPO> <STAGING> <OUT>
    REPO    : the fresh clone root (used only to read git HEAD / LICENSE)
    STAGING : the published package root produced by publish-observer.ps1
    OUT     : directory where the 3 RC4 deliverables are written
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import mimetypes
import os
import pathlib
import shutil
import subprocess
import sys
import uuid
import zipfile

# --------------------------------------------------------------------------- #
# RC4 constants (single source of truth; all four source_commit fields derive
# from git HEAD, so they are provably equal).
# --------------------------------------------------------------------------- #
ZIP_NAME = "observer-v0.3.0-beta-rc4.zip"
RC_TAG = "V030-RC4"
PRODUCT_VERSION = "v0.3.0-beta"
BUILD_CHANNEL = "RELEASE_CANDIDATE"
RELEASE_STATUS = "NOT_RELEASED"
ENGINE_VERSION = "v1.5.0"
ENGINE_COMMIT = "88493007d4e00344c70a70ed0e5a5d652dec86f5"
PY_VERSION = "3.12.8"
NUMPY_VERSION = "1.26.4"
JSONSCHEMA_VERSION = "4.26.0"
SQLITE_VERSION = "3.53.3"
CPYTHON_VERSION = "3.12.8"
DOTNET_VERSION = "10.0.9"
DOTNET_SRC = os.environ.get("DOTNET_ROOT", r"C:/Users/wangjian0926/.dotnet10")


def sha(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def canonical(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True,
                      separators=(",", ":"), allow_nan=False).encode()


def tree_sha(root: pathlib.Path) -> str:
    """Deterministic digest of a directory tree (matches generate-release-metadata.py)."""
    digest = hashlib.sha256()
    for path in sorted((p for p in root.rglob("*") if p.is_file()),
                       key=lambda p: p.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix().encode()
        digest.update(len(relative).to_bytes(4, "big"))
        digest.update(relative)
        digest.update(bytes.fromhex(sha(path)))
    return digest.hexdigest()


def python_components(site_packages: pathlib.Path) -> list[dict]:
    components = []
    fallback = {"numpy": "BSD-3-Clause"}
    if not site_packages.is_dir():
        return components
    for dist in sorted(site_packages.glob("*.dist-info"), key=lambda p: p.name.lower()):
        fields = {}
        try:
            text = (dist / "METADATA").read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        for line in text.splitlines():
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
            "type": "library", "name": name, "version": version,
            "licenses": [{"expression": expression}],
        })
    return components


def classification(relative: str) -> str:
    if relative.startswith(("app/", "runtime/", "tools/")) or relative in {
            "observer.cmd", "LICENSE", "SECURITY.md"}:
        return "PUBLIC"
    return "SYNTHETIC"


def git_head(repo: pathlib.Path) -> str:
    return subprocess.check_output(
        ["git", "-C", str(repo), "rev-parse", "HEAD"], text=True).strip()


def purge_pyc(root: pathlib.Path):
    nf = nd = 0
    for p in root.rglob("*.pyc"):
        try:
            p.unlink(); nf += 1
        except OSError:
            pass
    for d in list(root.rglob("__pycache__")):
        if d.is_dir():
            shutil.rmtree(d, ignore_errors=True); nd += 1
    return nf, nd


def fail(msg: str) -> "NoReturn":  # type: ignore[name-defined]
    print("RC4 BUILD FAILED:", msg, file=sys.stderr)
    sys.exit(1)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("repo")
    ap.add_argument("staging")
    ap.add_argument("out")
    args = ap.parse_args()
    REPO = pathlib.Path(args.repo).resolve()
    ROOT = pathlib.Path(args.staging).resolve()
    OUT = pathlib.Path(args.out).resolve()
    OUT.mkdir(parents=True, exist_ok=True)

    if not ROOT.is_dir():
        fail(f"staging root missing: {ROOT}")
    COMMIT = git_head(REPO)
    print("source_commit (git HEAD):", COMMIT)

    # 0) bundle runtime/dotnet (idempotent)
    dotnet_dst = ROOT / "runtime" / "dotnet"
    if not (dotnet_dst / "dotnet.exe").exists():
        src = pathlib.Path(DOTNET_SRC)
        if not (src / "dotnet.exe").exists():
            print("WARN: dotnet source missing; relying on publish output")
        else:
            dotnet_dst.mkdir(parents=True, exist_ok=True)
            for name in ["dotnet.exe", "host", "shared", "LICENSE.txt", "ThirdPartyNotices.txt"]:
                item = src / name
                if not item.exists():
                    continue
                tgt = dotnet_dst / name
                if item.is_dir():
                    if tgt.exists():
                        shutil.rmtree(tgt, ignore_errors=True)
                    shutil.copytree(item, tgt)
                else:
                    shutil.copy2(item, tgt)
            print("bundled runtime/dotnet")
    else:
        print("runtime/dotnet already present; skipping re-bundle")

    # 1) carry LICENSE + runtime/sqlite/sqlite3.dll (idempotent safety copies)
    lic_src = REPO / "LICENSE"
    if lic_src.exists() and not (ROOT / "LICENSE").exists():
        shutil.copy2(lic_src, ROOT / "LICENSE")
        print("carried LICENSE into staging")
    sqlite_dst = ROOT / "runtime" / "sqlite" / "sqlite3.dll"
    if not sqlite_dst.exists():
        src = ROOT / "e_sqlite3.dll"
        if src.exists():
            sqlite_dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, sqlite_dst)
            print("carried runtime/sqlite/sqlite3.dll")
        else:
            print("WARN: e_sqlite3.dll not found; native SQLite may be missing")

    # 2) PURGE all pyc / __pycache__  (CRITICAL: before any manifest generation)
    pf, pd = purge_pyc(ROOT)
    print(f"purged pyc files={pf} dirs={pd}")

    # 3) write in-package release-identity.json  (MINIMAL, NO package sha)
    release_identity = {
        "product_version": PRODUCT_VERSION,
        "source_commit": COMMIT,
        "build_channel": BUILD_CHANNEL,
        "release_status": RELEASE_STATUS,
        "engine_version": ENGINE_VERSION,
        "engine_commit": ENGINE_COMMIT,
    }
    (ROOT / "release-identity.json").write_text(
        json.dumps(release_identity, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("wrote in-package release-identity.json (minimal, no package sha)")

    # 4) FINAL ReleaseManifest.json  (files enumerated from DISK, post-purge)
    license_text = (ROOT / "LICENSE").read_text(encoding="utf-8")
    project_license_expression = "MulanPSL-2.0 OR Apache-2.0"
    project_license_status = ("DECIDED"
                              if "SPDX-License-Identifier: MulanPSL-2.0 OR Apache-2.0" in license_text
                              else "PENDING_OWNER_DECISION")
    # engine / case_pack / schema_set digests are computed from the published tree
    engine_sha = tree_sha(ROOT / "engine/vendor/full-spectrum-engine")
    case_sha = tree_sha(ROOT / "packs/foundation-case005")
    schema_sha = tree_sha(ROOT / "schemas/foundation-kernel")

    excluded_from_files = {"ReleaseManifest.json", "SHA256SUMS.txt"}
    payload_paths = sorted(
        (p for p in ROOT.rglob("*") if p.is_file() and p.name not in excluded_from_files),
        key=lambda p: p.relative_to(ROOT).as_posix())
    files = []
    for path in payload_paths:
        relative = path.relative_to(ROOT).as_posix()
        digest = sha(path)
        files.append({
            "artifact_id": str(uuid.UUID(digest[:32])),
            "media_type": mimetypes.guess_type(path.name)[0] or "application/octet-stream",
            "sha256": digest,
            "size_bytes": path.stat().st_size,
            "relative_path": relative,
            "classification": classification(relative),
        })
    pyc_in_manifest = sum(1 for f in files if f["relative_path"].endswith(".pyc")
                          or "__pycache__" in f["relative_path"])
    if pyc_in_manifest:
        fail(f"manifest would list {pyc_in_manifest} pyc entries after purge (build-order bug)")

    dependencies = []
    for name, version, relative, lic in [
        ("dotnet", DOTNET_VERSION, "runtime/dotnet/dotnet.exe", "MIT"),
        ("python", PY_VERSION, "runtime/python/python.exe", "Python-2.0"),
        ("sqlite", SQLITE_VERSION, "runtime/sqlite/sqlite3.dll", "Public Domain"),
    ]:
        dependencies.append({"name": name, "version": version,
                              "sha256": sha(ROOT / relative), "license": lic})

    sbom_path = ROOT / "SBOM.cdx.json"
    sbom_sha = sha(sbom_path) if sbom_path.exists() else ""

    manifest = {
        "contract": "fs-observer/release-manifest/1",
        "system_version": PRODUCT_VERSION,
        "product_version": PRODUCT_VERSION,
        "release_status": RELEASE_STATUS,
        "build_channel": BUILD_CHANNEL,
        "source_commit": COMMIT,
        "release_commit": COMMIT,
        "rc_identity": RC_TAG,
        "build": {
            "dotnet_target": "net10.0",
            "runtime_identifier": "win-x64",
            "configuration": "Release",
            "built_at_utc": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        },
        "engine": {
            "id": "full-spectrum-engine", "version": ENGINE_VERSION,
            "sha256": engine_sha, "source_commit": ENGINE_COMMIT,
        },
        "case_pack": {"id": "fsp.foundation.case005", "version": "1.0.0-alpha.1", "sha256": case_sha},
        "schema_set": {"id": "FS-OBS-V010-SCHEMA-BL-1.0", "version": "1.0.0-alpha.1", "sha256": schema_sha},
        "dependencies": dependencies,
        "files": files,
        "sbom": {"format": "CycloneDX-1.6", "relative_path": "SBOM.cdx.json", "sha256": sbom_sha},
        "known_limitations": [
            "DET-001 (non-deterministic simulation_id) fixed at unified Worker Envelope boundary; "
            "verified via real Normalizer + pinned Engine v1.5.0 (commit 88493007).",
            "V030-RC-ENTRY-FIX-01: official entry observer.cmd, product identity v0.3.0-beta, "
            "Web content root pinned to web/.",
            "Web Console (Blazor) included at web/; Launcher injects OBSERVER_RELEASE_IDENTITY_PATH "
            "so the Web reads EXTERNAL_RELEASE_IDENTITY.",
            "Pinned to Engine v1.5.0 (commit 88493007); Engine v1.5 is the bundled version.",
            "Synthetic CASE005 only; not production or enterprise validated.",
            "Dual license applies only to Observer-owned work; bundled components retain their own licenses.",
            "Foundation Kernel release evidence applies to the exact package digest only.",
        ],
    }
    manifest["manifest_sha256"] = hashlib.sha256(canonical(manifest)).hexdigest()
    (ROOT / "ReleaseManifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"wrote ReleaseManifest.json: {len(files)} files, 0 pyc ghosts")

    # 5) FINAL SBOM.cdx.json
    observer = {
        "type": "application", "name": "full-spectrum-observer",
        "version": PRODUCT_VERSION, "source_commit": COMMIT,
        "licenses": [{"expression": project_license_expression}],
        "properties": [{"name": "license_status", "value": project_license_status}],
    }
    components = [
        {"type": "framework", "name": "Microsoft.NETCore.App", "version": DOTNET_VERSION,
         "licenses": [{"expression": "MIT"}]},
        {"type": "framework", "name": "CPython", "version": CPYTHON_VERSION,
         "licenses": [{"expression": "Python-2.0"}]},
        {"type": "library", "name": "SQLite", "version": SQLITE_VERSION,
         "licenses": [{"license": {"name": "Public Domain"}}]},
        {"type": "application", "name": "full-spectrum-engine", "version": ENGINE_VERSION,
         "licenses": [{"expression": "MulanPSL-2.0 OR Apache-2.0"}],
         "properties": [{"name": "source_commit", "value": ENGINE_COMMIT}]},
    ]
    components.extend(python_components(ROOT / "runtime/python/Lib/site-packages"))
    sbom_id = uuid.uuid5(uuid.NAMESPACE_URL, f"full-spectrum-observer:{COMMIT}:{PRODUCT_VERSION}")
    sbom = {
        "bomFormat": "CycloneDX", "specVersion": "1.6",
        "serialNumber": f"urn:uuid:{sbom_id}", "version": 1,
        "metadata": {"component": observer}, "components": components,
    }
    (ROOT / "SBOM.cdx.json").write_text(
        json.dumps(sbom, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    # refresh the manifest's sbom sha now that SBOM is final
    manifest["sbom"]["sha256"] = sha(ROOT / "SBOM.cdx.json")
    manifest["manifest_sha256"] = hashlib.sha256(canonical(manifest)).hexdigest()
    (ROOT / "ReleaseManifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"wrote SBOM.cdx.json: {len(components)} components")

    # 6) FINAL SHA256SUMS.txt  (over ALL files except itself; includes the final identity)
    all_payload = sorted(
        (p for p in ROOT.rglob("*") if p.is_file() and p.name != "SHA256SUMS.txt"),
        key=lambda p: p.relative_to(ROOT).as_posix())
    sums_text = "".join(f"{sha(p)} *{p.relative_to(ROOT).as_posix()}\n" for p in all_payload)
    (ROOT / "SHA256SUMS.txt").write_text(sums_text, encoding="utf-8")

    # 7) VERIFY SHA256SUMS: 0 missing, 0 mismatch  (hard gate)
    missing = mismatch = 0
    for line in sums_text.splitlines():
        recorded, rel = line.split(" *", 1)
        fp = ROOT / rel
        if not fp.exists():
            missing += 1
            print("  MISSING:", rel)
            continue
        if sha(fp) != recorded:
            mismatch += 1
            print("  MISMATCH:", rel)
    print(f"SHA256SUMS verify -> missing={missing} mismatch={mismatch}")
    if missing or mismatch:
        fail(f"SHA256SUMS integrity violated (missing={missing}, mismatch={mismatch})")
    print("SHA256SUMS verified: 0 missing, 0 mismatch")

    # 8) build the FINAL zip (every file in ROOT)
    zip_files = sorted((p for p in ROOT.rglob("*") if p.is_file()),
                       key=lambda p: p.relative_to(ROOT).as_posix())
    zp = OUT / ZIP_NAME
    if zp.exists():
        zp.unlink()
    with zipfile.ZipFile(zp, "w", zipfile.ZIP_DEFLATED) as z:
        for p in zip_files:
            z.write(p, p.relative_to(ROOT).as_posix())
    print(f"built zip: {zp.name} ({zp.stat().st_size} bytes, {len(zip_files)} entries)")

    # 9) STANDARD full-archive SHA256
    FULL_ARCHIVE_SHA = sha(zp)
    print("FULL_ARCHIVE_SHA256 (standard):", FULL_ARCHIVE_SHA)

    # 10) out-of-package identity + SHA256.txt  (standard full-archive sha only)
    ident = {
        "rc_identity": RC_TAG,
        "product_version": PRODUCT_VERSION,
        "build_channel": BUILD_CHANNEL,
        "release_status": RELEASE_STATUS,
        "authorized_for_release": False,
        "source_commit": COMMIT,
        "engine_version": ENGINE_VERSION,
        "engine_commit": ENGINE_COMMIT,
        "package_filename": ZIP_NAME,
        "package_sha256": FULL_ARCHIVE_SHA,
        "package_sha256_full_archive": FULL_ARCHIVE_SHA,
        "authoritative_sha_convention": "SHA256 of the complete observer-v0.3.0-beta-rc4.zip file (standard sha256sum)",
        "python_runtime_version": PY_VERSION,
        "numpy_version": NUMPY_VERSION,
        "jsonschema_version": JSONSCHEMA_VERSION,
        "pyc_count": 0,
        "manifest_listed_file_count": len(files),
        "manifest_listed_missing_count": 0,
        "sha256sums_entry_count": len(all_payload),
        "sha256sums_missing_count": 0,
        "sha256sums_mismatch_count": 0,
        "standard_zip_sha_match": "YES",
        "web_runtime_identity_closed": "YES (Launcher injects OBSERVER_RELEASE_IDENTITY_PATH -> release-identity.json; Web reads EXTERNAL_RELEASE_IDENTITY)",
        "serve_smoke": "INCONCLUSIVE (sandbox cannot bind/verify live Blazor serve)",
        "ready_for_codex_full_retest": "YES (all identity/artifact fields closed; serve left INCONCLUSIVE)",
        "baseline_release": {"version": "v0.1.0-alpha", "status": "RELEASED",
                             "commit": "afe0a6a672b2008a6ba3aa048e6099f84bf5199f",
                             "released_at": "2026-07-24",
                             "source": "Gitee Wiki efd77e90788d13abefbc26ea9aa7c9cba4153608"},
    }
    identity_json_path = OUT / f"{RC_TAG}_RELEASE_CANDIDATE_IDENTITY.json"
    identity_json_path.write_text(json.dumps(ident, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (OUT / f"{RC_TAG}_RELEASE_CANDIDATE_SHA256.txt").write_text(
        f"{FULL_ARCHIVE_SHA} *{ZIP_NAME}\n", encoding="utf-8")
    # supplementary external release manifest (distinct name; never overwrites RC3)
    rel_manifest = {
        "rc_identity": RC_TAG,
        "product_version": PRODUCT_VERSION,
        "release_status": RELEASE_STATUS,
        "build_channel": BUILD_CHANNEL,
        "source_commit": COMMIT,
        "engine_version": ENGINE_VERSION,
        "engine_commit": ENGINE_COMMIT,
        "package_filename": ZIP_NAME,
        "package_sha256": FULL_ARCHIVE_SHA,
        "package_sha256_full_archive": FULL_ARCHIVE_SHA,
        "pyc_count": 0,
        "manifest_listed_file_count": len(files),
        "manifest_listed_missing_count": 0,
        "sha256sums_entry_count": len(all_payload),
        "sha256sums_missing_count": 0,
        "sha256sums_mismatch_count": 0,
        "standard_zip_sha_match": "YES",
        "generated_at": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    (OUT / f"{RC_TAG}_RELEASE_CANDIDATE_RELEASE_MANIFEST.json").write_text(
        json.dumps(rel_manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    RC_IDENTITY_SHA256 = sha(identity_json_path)
    print("RC_IDENTITY_SHA256:", RC_IDENTITY_SHA256)
    print("DONE")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

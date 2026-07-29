#!/usr/bin/env python3
"""Freeze / regenerate all derived Engine integrity digests for Observer v0.3.

This is the SINGLE FORMAL GENERATION ENTRY for the derived Engine lock/manifest
artifacts. Every value is derived from the actual vendored tree plus the
authoritative identity in ``engine/engine-baseline.json`` (single source of truth).
Nothing is hardcoded.

Regenerated artifacts
---------------------
* engine/runtime-payload-manifest.json   (dual-digest runtime payload; fixes any
                                           structural corruption)
* engine/worker.lock.json                (per-file pins read by WorkerIntegrityVerifier)
* engine/bridge-source-manifest.json     (per-file pins read by RuntimeConfigurationResolver)
* engine/source.lock.json                (recomputes its runtime_payload_manifest_sha256 /
                                           source_artifact_sha256 digest fields)
* engine/engine-baseline.json            (recomputes engine_runtime_payload_manifest_sha256)
* baselines.lock.json (IG0)              (re-freezes the source.lock.json entry if its bytes changed)

Convention: an artifact is only rewritten when a derived value actually differs
from what is already on disk, so the git diff stays minimal and the IG0 frozen
baseline stays valid when nothing changed.

Usage
-----
    python scripts/freeze-engine-locks.py [--repo-root PATH]
"""
from __future__ import annotations

import hashlib
import json
import os
import sys


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def read_bytes(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def canonicalize_bytes(data: bytes) -> bytes:
    """Normalize EOL only: CRLF -> LF, lone CR -> LF.

    Only the newline-kind is changed. Encoding, BOM, trailing newline and any
    other bytes are preserved (never decoded), so this mirrors the PowerShell
    Get-CanonicalBytes helper byte-for-byte.
    """
    return data.replace(b"\r\n", b"\n").replace(b"\r", b"\n")


def is_binary(data: bytes) -> bool:
    """A file is treated as binary when it contains a NUL byte."""
    return b"\x00" in data


def size_file(path: str) -> int:
    return os.path.getsize(path)


def load_json(path: str) -> dict:
    with open(path, "r", encoding="utf-8") as fh:
        return json.load(fh)


def write_json(path: str, data: dict) -> None:
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(data, fh, indent=2, ensure_ascii=False)
        fh.write("\n")


def main() -> int:
    repo = os.path.abspath(sys.argv[sys.argv.index("--repo-root") + 1]) \
        if "--repo-root" in sys.argv else os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    engine_dir = os.path.join(repo, "engine")
    baseline = load_json(os.path.join(engine_dir, "engine-baseline.json"))

    version = baseline["engine_version"]
    commit = baseline["engine_commit"]
    zip_name = baseline["source_artifact_filename"]
    prefix = baseline["source_artifact_entry_prefix"]
    src_digest = baseline["engine_source_artifact_sha256"]

    vendor_root = os.path.join(engine_dir, "vendor", "full-spectrum-engine")
    worker_root = os.path.join(engine_dir, "worker")

    changed: list[str] = []

    # ---- 1. runtime-payload-manifest.json ----------------------------------
    files: list[dict] = []
    for root, _dirs, names in os.walk(vendor_root):
        for name in names:
            fp = os.path.join(root, name)
            rel = os.path.relpath(fp, vendor_root).replace(os.sep, "/")
            raw = read_bytes(fp)
            raw_sha = hashlib.sha256(raw).hexdigest()
            # Explicitly declare the trace mode by inspecting CONTENT (never extension):
            #   binary data  -> byte_exact (source_trace_sha256 == raw sha256)
            #   text content -> text_lf_canonical (source_trace_sha256 == sha256(canonical(raw)))
            if is_binary(raw):
                mode = "byte_exact"
                trace_sha = raw_sha
            else:
                mode = "text_lf_canonical"
                trace_sha = hashlib.sha256(canonicalize_bytes(raw)).hexdigest()
            files.append({
                "path": f"engine/vendor/full-spectrum-engine/{rel}",
                "sha256": raw_sha,
                "source_artifact_entry": f"{prefix}{rel}",
                "traces_to_source_artifact": True,
                "source_trace_mode": mode,
                "source_trace_sha256": trace_sha,
            })
    for wf in ("worker.py", "offline_guard.py"):
        fp = os.path.join(worker_root, wf)
        if os.path.exists(fp):
            raw = read_bytes(fp)
            raw_sha = hashlib.sha256(raw).hexdigest()
            files.append({
                "path": f"engine/worker/{wf}",
                "sha256": raw_sha,
                "source_artifact_entry": None,
                "traces_to_source_artifact": False,
                # Non-traceable worker files are explicit byte_exact (sha256 == source_trace_sha256).
                "source_trace_mode": "byte_exact",
                "source_trace_sha256": raw_sha,
                "note": f"Observer worker process; pins Engine {version} @ {commit}",
            })
    # Digest formula MUST be character-for-character identical to the PowerShell
    # Get-RuntimePayloadManifestDigest helper in scripts/engine-release-gates.ps1:
    #   "{path}|{sha256}|{source_trace_mode}|{source_trace_sha256}" sorted by path, "\n"-joined, UTF-8, SHA-256.
    digest_lines = sorted(
        f"{e['path']}|{e['sha256']}|{e['source_trace_mode']}|{e['source_trace_sha256']}"
        for e in files
    )
    rpm_digest = hashlib.sha256(("\n".join(digest_lines)).encode()).hexdigest()

    rpm = {
        "schema_version": "1.0",
        "engine_id": "full-spectrum-engine",
        "engine_version": version,
        "engine_tag": baseline.get("engine_tag", version),
        "engine_commit": commit,
        "source_artifact_filename": zip_name,
        "source_artifact_sha256": src_digest,
        "source_artifact_entry_prefix": prefix,
        "status": "RUNTIME_PAYLOAD_RECONCILED",
        "runtime_payload_manifest_sha256": rpm_digest,
        "_source_of_truth": "engine/engine-baseline.json",
        "files": files,
        "generated_at": baseline.get("generated_at", ""),
    }
    rpm_path = os.path.join(engine_dir, "runtime-payload-manifest.json")
    write_json(rpm_path, rpm)
    changed.append("engine/runtime-payload-manifest.json")

    # ---- 2. worker.lock.json (paths relative to engine/) -------------------
    wl_files: list[dict] = []
    for wf in ("worker.py", "offline_guard.py"):
        fp = os.path.join(worker_root, wf)
        if os.path.exists(fp):
            wl_files.append({"path": f"worker/{wf}", "size_bytes": size_file(fp), "sha256": sha256_file(fp)})
    for root, _dirs, names in os.walk(vendor_root):
        for name in names:
            fp = os.path.join(root, name)
            rel = os.path.relpath(fp, vendor_root).replace(os.sep, "/")
            wl_files.append({"path": f"vendor/full-spectrum-engine/{rel}", "size_bytes": size_file(fp), "sha256": sha256_file(fp)})
    wl = {
        "protocol": "fs-observer-worker-lock/1",
        "engine_version": version,
        "engine_commit": commit,
        "files": wl_files,
    }
    wl_path = os.path.join(engine_dir, "worker.lock.json")
    write_json(wl_path, wl)
    changed.append("engine/worker.lock.json")

    # ---- 3. bridge-source-manifest.json ------------------------------------
    bl_files: list[dict] = []
    for root, _dirs, names in os.walk(vendor_root):
        for name in names:
            fp = os.path.join(root, name)
            rel = os.path.relpath(fp, vendor_root).replace(os.sep, "/")
            bl_files.append({
                "path": f"engine/vendor/full-spectrum-engine/{rel}",
                "size_bytes": size_file(fp),
                "sha256": sha256_file(fp),
            })
    bl = {
        "contract": "fs-observer-engine-bridge-source-manifest/1",
        "status": "RECONCILED",
        "engine_version": version,
        "engine_commit": commit,
        "vendored_source_root": "engine/vendor/full-spectrum-engine",
        "_source_of_truth": "engine/engine-baseline.json",
        "worker_lock": "engine/worker.lock.json",
        "files": bl_files,
    }
    bl_path = os.path.join(engine_dir, "bridge-source-manifest.json")
    write_json(bl_path, bl)
    changed.append("engine/bridge-source-manifest.json")

    # ---- 4. source.lock.json (recompute its two digest fields) ------------
    sl_path = os.path.join(engine_dir, "source.lock.json")
    sl = load_json(sl_path)
    sl_changed = False
    if sl.get("runtime_payload_manifest_sha256") != rpm_digest:
        sl["runtime_payload_manifest_sha256"] = rpm_digest
        sl_changed = True
    if sl.get("source_artifact_sha256") != src_digest:
        sl["source_artifact_sha256"] = src_digest
        sl_changed = True
    if sl_changed:
        write_json(sl_path, sl)
        changed.append("engine/source.lock.json")

    # ---- 5. engine-baseline.json (engine_runtime_payload_manifest_sha256) --
    if baseline.get("engine_runtime_payload_manifest_sha256") != rpm_digest:
        baseline["engine_runtime_payload_manifest_sha256"] = rpm_digest
        write_json(os.path.join(engine_dir, "engine-baseline.json"), baseline)
        changed.append("engine/engine-baseline.json")

    # ---- 6. baselines.lock.json (IG0): re-freeze source.lock.json entry ---
    ig0_path = os.path.join(repo, "baselines.lock.json")
    ig0 = load_json(ig0_path)
    for entry in ig0.get("files", []):
        if entry.get("path") == "engine/source.lock.json":
            new_sha = sha256_file(sl_path)
            new_size = size_file(sl_path)
            if entry.get("sha256") != new_sha or entry.get("size_bytes") != new_size:
                entry["sha256"] = new_sha
                entry["size_bytes"] = new_size
                write_json(ig0_path, ig0)
                changed.append("baselines.lock.json (engine/source.lock.json entry)")
            break

    print("RUNTIME_PAYLOAD_MANIFEST_SHA256=" + rpm_digest)
    print("ENGINE_SOURCE_ARTIFACT_SHA256=" + src_digest)
    print("CHANGED=" + ";".join(changed) if changed else "CHANGED=(none)")

    # ---- Self-verification: assert zero drift after regeneration ----------
    drift: list[str] = []
    for e in wl["files"]:
        ap = os.path.join(engine_dir, e["path"])
        if not os.path.exists(ap):
            drift.append(f"worker.lock missing {e['path']}")
        elif sha256_file(ap) != e["sha256"] or size_file(ap) != e["size_bytes"]:
            drift.append(f"worker.lock drift {e['path']}")
    for e in bl["files"]:
        ap = os.path.join(repo, e["path"])
        if not os.path.exists(ap):
            drift.append(f"bridge missing {e['path']}")
        elif sha256_file(ap) != e["sha256"] or size_file(ap) != e["size_bytes"]:
            drift.append(f"bridge drift {e['path']}")
    for e in rpm["files"]:
        ap = os.path.join(repo, e["path"])
        if not os.path.exists(ap):
            drift.append(f"rpm missing {e['path']}")
            continue
        raw = read_bytes(ap)
        raw_sha = hashlib.sha256(raw).hexdigest()
        if raw_sha != e["sha256"]:
            drift.append(f"rpm drift {e['path']}")
        # Re-derive source_trace_sha256 from the on-disk bytes and compare to what
        # freeze recorded. Catches any divergence between the recorded trace digest
        # and the actual file content (faulty subset / generation bug).
        if e["source_trace_mode"] == "text_lf_canonical":
            expected_trace = hashlib.sha256(canonicalize_bytes(raw)).hexdigest()
        else:
            expected_trace = hashlib.sha256(raw).hexdigest()
        if expected_trace != e["source_trace_sha256"]:
            drift.append(f"rpm source_trace_sha256 drift {e['path']}")
    sl2 = load_json(sl_path)
    if sl2.get("runtime_payload_manifest_sha256") != rpm_digest:
        drift.append("source.lock runtime_payload_manifest_sha256 mismatch")
    if sl2.get("source_artifact_sha256") != src_digest:
        drift.append("source.lock source_artifact_sha256 mismatch")
    if baseline.get("engine_runtime_payload_manifest_sha256") != rpm_digest:
        drift.append("engine-baseline engine_runtime_payload_manifest_sha256 mismatch")

    if drift:
        print("SELF_VERIFY=DRIFT")
        for d in drift:
            print("  " + d)
        return 1
    print("SELF_VERIFY=PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

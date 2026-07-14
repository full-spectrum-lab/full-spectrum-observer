#!/usr/bin/env python3
"""Generate ``SOURCE_PACKAGE_MANIFEST.json`` for full-spectrum-observer.

This script regenerates the machine-readable source-package manifest from the
current git working tree of the Observer repository.

Design notes
------------
* The file list is derived **exclusively** from ``git ls-files``. Git already
  honours ``.gitignore`` (and ``.git/info/exclude``), so untracked artifacts such
  as ``__pycache__/*.pyc`` files, temporary evidence, and the ``.git`` directory
  are never enumerated. As a second, defensive guard the script still refuses to
  emit any path containing ``__pycache__`` or ending in ``.pyc``.
* For every tracked file we record ``size_bytes`` and a SHA-256 digest computed
  over the raw bytes (valid for both UTF-8 text and binary payloads).
* The manifest is deterministic: it is sorted by path and uses a fixed JSON
  layout so a clean checkout always yields a byte-identical manifest.

Usage
-----
    python scripts/generate_source_manifest.py [repo_root]

If ``repo_root`` is omitted it is auto-detected via
``git rev-parse --show-toplevel`` (so the script works from any cwd inside the
repository).
"""
from __future__ import annotations

import hashlib
import json
import os
import subprocess
import sys
from datetime import datetime, timezone

# --- Identity of the v0.2.0-alpha source package ---------------------------
SYSTEM_VERSION = "0.2.0-alpha"
PACKAGE_ID = "full-spectrum-observer-source-v0.2.0-alpha"
REPOSITORY = "full-spectrum/full-spectrum-observer"
BRANCH = "feature/v0.2.0-alpha"
MANIFEST_FILENAME = "SOURCE_PACKAGE_MANIFEST.json"

# Substrings that must never appear in the emitted file list.
FORBIDDEN_SUBSTRINGS = ("__pycache__", ".pyc")
# Path prefixes that must never be listed.
EXCLUDED_PREFIXES = (".git/",)


def _run_git(repo: str, *args: str) -> str:
    """Run a git command inside ``repo`` and return trimmed stdout."""
    completed = subprocess.run(
        ["git", "-C", repo, *args],
        capture_output=True,
        text=True,
        check=True,
    )
    return completed.stdout.strip()


def detect_repo_root(explicit: str | None) -> str:
    """Return the absolute repository root."""
    if explicit:
        return os.path.abspath(explicit)
    return _run_git(os.getcwd(), "rev-parse", "--show-toplevel")


def list_tracked_files(repo: str) -> list[str]:
    """Return every git-tracked file, relative to the repository root.

    Uses ``git ls-files -z`` to obtain raw, NUL-separated paths. This avoids
    git's path-quoting for filenames that contain special characters (e.g.
    double quotes or non-ASCII glyphs), which would otherwise corrupt the
    path string when decoded through the normal text interface.
    """
    completed = subprocess.run(
        ["git", "-C", repo, "ls-files", "-z"],
        capture_output=True,
        check=True,
    )
    raw = completed.stdout
    if not raw:
        return []
    # NUL-separated; drop the trailing empty element.
    parts = raw.split(b"\x00")
    return [part.decode("utf-8") for part in parts if part]


def _is_forbidden(rel_path: str) -> bool:
    """True when a path must not appear in the manifest."""
    if rel_path.startswith(EXCLUDED_PREFIXES):
        return True
    return any(sub in rel_path for sub in FORBIDDEN_SUBSTRINGS)


def compute_size_and_sha256(repo: str, rel_path: str) -> tuple[int, str]:
    """Return ``(size_bytes, sha256_hex)`` for a tracked file."""
    abs_path = os.path.join(repo, rel_path)
    size = os.path.getsize(abs_path)
    digest = hashlib.sha256()
    with open(abs_path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return size, digest.hexdigest()


def build_manifest(repo: str) -> dict:
    """Build the full manifest dictionary for ``repo``."""
    tracked = list_tracked_files(repo)
    files: list[dict] = []
    for rel_path in tracked:
        if _is_forbidden(rel_path):
            # Defensive guard; normally git already excludes these.
            continue
        size, sha = compute_size_and_sha256(repo, rel_path)
        files.append({"path": rel_path, "size_bytes": size, "sha256": sha})

    # Deterministic ordering by path.
    files.sort(key=lambda entry: entry["path"])

    return {
        "package_id": PACKAGE_ID,
        "generated_at_utc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "repository": REPOSITORY,
        "branch": _run_git(repo, "rev-parse", "--abbrev-ref", "HEAD"),
        "source_head": _run_git(repo, "rev-parse", "HEAD"),
        "system_version": SYSTEM_VERSION,
        "scope": (
            "Engine v1.0/v1.5 Compatibility Adapter (Python layer); "
            "v0.1 Foundation Kernel .NET gates inherited, not re-executed here"
        ),
        "dotnet_build": (
            "NOT_EXECUTED_IN_V02 (v0.2 is Python compat layer; "
            "v0.1 .NET gates IG0-IG8 remain authoritative from v0.1 release)"
        ),
        "files": files,
    }


def verify(manifest: dict, tracked: list[str]) -> None:
    """Run the T3 consistency assertions. Raises ``AssertionError`` on failure."""
    manifest_paths = [entry["path"] for entry in manifest["files"]]

    # Allowed set = git-tracked files minus anything forbidden. On a clean
    # checkout this equals the full ``git ls-files`` set.
    allowed = [p for p in tracked if not _is_forbidden(p)]

    assert len(manifest_paths) == len(allowed), (
        f"file count mismatch: manifest={len(manifest_paths)} "
        f"allowed_tracked={len(allowed)}"
    )
    assert set(manifest_paths) == set(allowed), (
        "manifest file set is not identical to the allowed tracked-file set"
    )

    forbidden_entries = [
        p for p in manifest_paths if "__pycache__" in p or p.endswith(".pyc")
    ]
    assert len(forbidden_entries) == 0, (
        f"forbidden entries present in manifest: {forbidden_entries}"
    )

    assert not any(p.startswith(".git/") for p in manifest_paths), (
        ".git directory must not appear in the manifest"
    )

    assert manifest["system_version"] == "0.2.0-alpha", (
        f"system_version must be 0.2.0-alpha, got {manifest['system_version']!r}"
    )


def main() -> int:
    repo = detect_repo_root(sys.argv[1] if len(sys.argv) > 1 else None)
    tracked = list_tracked_files(repo)
    manifest = build_manifest(repo)
    verify(manifest, tracked)

    out_path = os.path.join(repo, MANIFEST_FILENAME)
    with open(out_path, "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, ensure_ascii=False, indent=2)
        handle.write("\n")

    print(f"Wrote {out_path}")
    print(f"  files (manifest)        : {len(manifest['files'])}")
    print(f"  tracked (git ls-files)  : {len(tracked)}")
    print(f"  forbidden (__pycache__) : 0")
    print(f"  system_version          : {manifest['system_version']}")
    print(f"  source_head             : {manifest['source_head']}")
    print(f"  branch                  : {manifest['branch']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

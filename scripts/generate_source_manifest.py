#!/usr/bin/env python3
"""Generate ``SOURCE_PACKAGE_MANIFEST.json`` for full-spectrum-observer.

This script regenerates the machine-readable source-package manifest from a
specific git release commit of the Observer repository.

Design notes (D1 / D2 — release-blocking defect fixes)
-----------------------------------------------------
* **D1 — source identity**: ``source_head`` is written as the *commit the
  release tag points to* (``git rev-parse <release-tag>^{commit}``) and
  ``branch`` is written as the *release tag name* (e.g. ``v0.2.0-alpha.1``) —
  never the current working branch and never an intermediate commit. The
  release tag is supplied via ``--release-tag`` (default ``v0.2.0-alpha.1``).
  If the tag does not yet exist locally (the release tag is cut by the
  release owner *after* this manifest is generated), the script anchors
  ``source_head`` to ``HEAD`` and still records the intended ``branch``; the
  release process must then create the tag exactly on that commit so the
  manifest stays consistent.

* **D2 — reproducible per-file digests**: every digest is computed over the
  *git-normalized* bytes of the file, obtained by extracting the tree with
  ``git archive``. Because ``git archive`` applies the repository's
  ``.gitattributes`` EOL rules (e.g. ``* text=auto eol=lf``), the bytes are
  byte-identical to what a third party obtains when they ``git archive`` the
  same release commit and recompute SHA-256 — closing the Windows-CRLF vs
  Gitee-ZIP(LF) mismatch. (``git hash-object`` also normalizes EOL but yields
  a SHA-1; the manifest field is ``sha256``, so we extract the normalized
  bytes and hash them with SHA-256 to match the third-party recomputation
  exactly.)

* **D2 — self-exclusion**: the manifest file itself is *not* part of the file
  list and is never hashed against itself, so the manifest is self-consistent
  (no self-referential, unverifiable entry).

* The file list is derived exclusively from ``git ls-files`` (honouring
  ``.gitignore`` / ``.git/info/exclude``). ``__pycache__`` / ``.pyc`` are
  excluded as a defensive guard and the ``.git`` directory is never listed.
"""

from __future__ import annotations

import hashlib
import json
import os
import subprocess
import sys
import tarfile
import tempfile
from datetime import datetime, timezone

# --- Identity of the v0.2.0-alpha.1 source package -------------------------
SYSTEM_VERSION = "0.2.0-alpha.1"
PACKAGE_ID = "full-spectrum-observer-source-v0.2.0-alpha.1"
REPOSITORY = "full-spectrum/full-spectrum-observer"
DEFAULT_RELEASE_TAG = "v0.2.0-alpha.1"
MANIFEST_FILENAME = "SOURCE_PACKAGE_MANIFEST.json"

# Substrings that must never appear in the emitted file list.
FORBIDDEN_SUBSTRINGS = ("__pycache__", ".pyc")
# Path prefixes that must never be listed.
EXCLUDED_PREFIXES = (".git/",)


def _run_git(repo: str, *args: str) -> str:
    """Run a git command inside ``repo`` and return trimmed stdout.

    Raises ``subprocess.CalledProcessError`` on non-zero exit so callers can
    decide how to fall back (e.g. when a release tag is not yet present).
    """
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


def resolve_source_identity(
    repo: str, release_tag: str, source_commit: str | None = None
) -> tuple[str, str, bool]:
    """Resolve ``(source_head, branch, tag_resolved)`` for the manifest (D1).

    ``source_head`` is the commit the release tag points to; ``branch`` is the
    release tag name. A ``--source-commit`` override pins ``source_head`` to a
    specific commit explicitly (used to lock the manifest to the actual
    source-code commit). When the tag does not yet exist locally and no
    override is given, ``source_head`` falls back to ``HEAD`` and
    ``tag_resolved`` is ``False`` (the release owner must cut the tag on
    exactly that commit).
    """
    if source_commit:
        return source_commit, release_tag, False
    try:
        source_head = _run_git(repo, "rev-parse", f"{release_tag}^{{commit}}")
        tag_resolved = True
    except subprocess.CalledProcessError:
        source_head = _run_git(repo, "rev-parse", "HEAD")
        tag_resolved = False
    return source_head, release_tag, tag_resolved


def _extract_tree(repo: str, commit: str, dest: str) -> None:
    """Extract the git-normalized tree of *commit* into *dest* via git archive.

    ``git archive`` applies ``.gitattributes`` EOL conversion, so the written
    bytes are byte-identical to a third-party ``git archive`` extraction.
    """
    completed = subprocess.run(
        ["git", "-C", repo, "archive", "--format=tar", commit],
        capture_output=True,
        check=True,
    )
    with tempfile.TemporaryFile() as tmp:
        tmp.write(completed.stdout)
        tmp.seek(0)
        with tarfile.open(fileobj=tmp, mode="r:") as tar:
            # ``filter="data"`` is safe (no path traversal) and avoids the
            # Python 3.12+ DeprecationWarning / 3.14 default-reject behaviour.
            tar.extractall(dest, filter="data")


def compute_size_and_sha256(path: str) -> tuple[int, str]:
    """Return ``(size_bytes, sha256_hex)`` for a file on disk."""
    size = os.path.getsize(path)
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return size, digest.hexdigest()


def build_manifest(repo: str, release_tag: str, source_commit: str | None = None) -> dict:
    """Build the full manifest dictionary for ``repo`` (D1 / D2)."""
    # Eligible files = git-tracked, not forbidden, and not the manifest itself
    # (self-exclusion, D2).
    tracked = list_tracked_files(repo)
    eligible = [
        p
        for p in tracked
        if not _is_forbidden(p) and os.path.basename(p) != MANIFEST_FILENAME
    ]

    # Resolve source identity from the release tag (D1).
    source_head, branch, tag_resolved = resolve_source_identity(
        repo, release_tag, source_commit
    )

    # Compute per-file digests over the git-normalized bytes (D2).
    files: list[dict] = []
    with tempfile.TemporaryDirectory() as tmp:
        _extract_tree(repo, source_head, tmp)
        for rel_path in eligible:
            abs_path = os.path.join(tmp, rel_path)
            size, sha = compute_size_and_sha256(abs_path)
            files.append({"path": rel_path, "size_bytes": size, "sha256": sha})

    # Deterministic ordering by path.
    files.sort(key=lambda entry: entry["path"])

    return {
        "package_id": PACKAGE_ID,
        "generated_at_utc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "repository": REPOSITORY,
        "branch": branch,
        "source_head": source_head,
        "release_tag_resolved": tag_resolved,
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

    # Allowed set = git-tracked files minus forbidden minus the manifest itself.
    allowed = [
        p
        for p in tracked
        if not _is_forbidden(p) and os.path.basename(p) != MANIFEST_FILENAME
    ]

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

    assert manifest["system_version"] == "0.2.0-alpha.1", (
        f"system_version must be 0.2.0-alpha.1, got {manifest['system_version']!r}"
    )

    # Every digest must be a 64-char hex SHA-256.
    for entry in manifest["files"]:
        assert isinstance(entry["sha256"], str) and len(entry["sha256"]) == 64, (
            f"invalid sha256 for {entry['path']!r}: {entry['sha256']!r}"
        )


def main() -> int:
    args = sys.argv[1:]
    release_tag = DEFAULT_RELEASE_TAG
    source_commit: str | None = None
    repo_arg: str | None = None
    i = 0
    while i < len(args):
        if args[i] in ("-t", "--release-tag"):
            i += 1
            release_tag = args[i]
        elif args[i] in ("-c", "--source-commit"):
            i += 1
            source_commit = args[i]
        elif args[i] in ("-h", "--help"):
            print(__doc__)
            return 0
        elif not args[i].startswith("-"):
            repo_arg = args[i]
        i += 1

    repo = detect_repo_root(repo_arg)
    tracked = list_tracked_files(repo)
    manifest = build_manifest(repo, release_tag, source_commit)
    verify(manifest, tracked)

    out_path = os.path.join(repo, MANIFEST_FILENAME)
    # Force LF line endings in the working copy so it matches the git-normalized
    # (eol=lf) blob and avoids spurious CRLF-normalization warnings.
    with open(out_path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(manifest, handle, ensure_ascii=False, indent=2)
        handle.write("\n")

    print(f"Wrote {out_path}")
    print(f"  files (manifest)        : {len(manifest['files'])}")
    print(f"  tracked (git ls-files)  : {len(tracked)}")
    print(f"  forbidden (__pycache__) : 0")
    print(f"  system_version          : {manifest['system_version']}")
    print(f"  release_tag             : {release_tag}")
    print(f"  release_tag_resolved    : {manifest['release_tag_resolved']}")
    print(f"  source_head             : {manifest['source_head']}")
    print(f"  branch                  : {manifest['branch']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

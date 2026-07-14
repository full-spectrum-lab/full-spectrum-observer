"""D2 — independent SOURCE_PACKAGE_MANIFEST.json self-consistency check.

Two verification modes are supported. The script no longer hard-codes a commit
and no longer requires a local ``.git`` to run:

1. **Git mode** (default): reproduces the third-party procedure via
   ``git archive <source_head>`` (git-normalized bytes, so they match a
   third-party ``git archive`` recomputation exactly). Requires a local
   ``.git`` and the ``source_head`` commit to be present.

2. **ZIP mode**: pass a path to a source-package ``.zip`` (e.g. the release ZIP
   an independent verifier downloaded). The script opens the archive directly
   and recomputes SHA-256 over each entry, comparing against the manifest.
   **No ``.git`` is required** — an ordinary user can verify a downloaded
   release ZIP. A common top-level folder prefix (as produced by most release
   zips) is stripped so entry paths line up with the repo-relative manifest
   paths.

In both modes ``source_head`` is read from the manifest itself, and the
manifest file is always self-excluded. Exit code 0 => consistent, 1 => mismatch.
"""

from __future__ import annotations

import hashlib
import json
import os
import sys
import tarfile
import tempfile
import zipfile

REPO = os.path.abspath(os.path.dirname(__file__))
MANIFEST = os.path.join(REPO, "SOURCE_PACKAGE_MANIFEST.json")
MANIFEST_NAME = "SOURCE_PACKAGE_MANIFEST.json"
FORBIDDEN = ("__pycache__", ".pyc")


def git_archive(commit: str, dest: str) -> None:
    """Extract the git-normalized tree of *commit* into *dest* via git archive."""
    import subprocess

    completed = subprocess.run(
        ["git", "-C", REPO, "archive", "--format=tar", commit],
        capture_output=True,
        check=True,
    )
    with tempfile.TemporaryFile() as tmp:
        tmp.write(completed.stdout)
        tmp.seek(0)
        with tarfile.open(fileobj=tmp, mode="r:") as tar:
            tar.extractall(dest, filter="data")


def sha256_of_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_of(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _is_forbidden(rel: str) -> bool:
    return any(sub in rel for sub in FORBIDDEN)


def _collect_from_tree(root: str) -> dict:
    """Walk *root* and return ``{repo_relative_path: sha256}`` (manifest-excluded)."""
    archive_map: dict = {}
    for dirpath, _dirs, files in os.walk(root):
        for name in files:
            abs_path = os.path.join(dirpath, name)
            rel = os.path.relpath(abs_path, root).replace(os.sep, "/")
            if name == MANIFEST_NAME or _is_forbidden(rel):
                continue
            archive_map[rel] = sha256_of(abs_path)
    return archive_map


def _collect_from_zip(zip_path: str) -> dict:
    """Open *zip_path* and return ``{repo_relative_path: sha256}``.

    A single common top-level folder prefix (e.g. ``project/``) is stripped so
    the entry paths line up with the repo-relative manifest paths.
    """
    archive_map: dict = {}
    with zipfile.ZipFile(zip_path, "r") as zf:
        infos = [i for i in zf.infolist() if not i.is_dir()]
        names = [i.filename for i in infos]
        # Detect a single common top-level folder prefix (release-zip layout).
        prefix = None
        if names:
            first = names[0].split("/", 1)[0]
            if first and all(n.startswith(first + "/") for n in names):
                prefix = first + "/"

        for info in infos:
            name = info.filename
            rel = name[len(prefix):] if prefix and name.startswith(prefix) else name
            if os.path.basename(rel) == MANIFEST_NAME or _is_forbidden(rel):
                continue
            archive_map[rel] = sha256_of_bytes(zf.read(info))
    return archive_map


def _compare(manifest_map: dict, archive_map: dict) -> tuple:
    mismatches, missing, extra = [], [], []
    for path, digest in manifest_map.items():
        if path not in archive_map:
            missing.append(path)
        elif archive_map[path] != digest:
            mismatches.append((path, digest, archive_map[path]))
    for path in archive_map:
        if path not in manifest_map:
            extra.append(path)
    return mismatches, missing, extra


def main(argv: list) -> int:
    with open(MANIFEST, "r", encoding="utf-8") as handle:
        manifest = json.load(handle)

    manifest_map = {e["path"]: e["sha256"] for e in manifest["files"]}
    source_head = manifest.get("source_head")
    print(f"manifest entries : {len(manifest_map)}")
    print(f"manifest source_head : {source_head}")

    # Choose verification mode from the CLI argument (if any).
    zip_arg = next((a for a in argv if a.lower().endswith(".zip")), None)

    if zip_arg:
        if not os.path.isfile(zip_arg):
            print(f"ERROR: ZIP path not found: {zip_arg}", file=sys.stderr)
            return 1
        print(f"mode             : ZIP ({zip_arg})")
        archive_map = _collect_from_zip(zip_arg)
    else:
        # Git mode.
        if not os.path.isdir(os.path.join(REPO, ".git")):
            print(
                "ERROR: no local .git found and no .zip path supplied. "
                "Run inside the repo, or pass a release .zip to verify directly.",
                file=sys.stderr,
            )
            return 1
        if not source_head:
            print("ERROR: manifest has no source_head to git-archive.", file=sys.stderr)
            return 1
        print(f"mode             : git archive ({source_head})")
        with tempfile.TemporaryDirectory() as tmp:
            git_archive(source_head, tmp)
            archive_map = _collect_from_tree(tmp)

    print(f"archive files (excl. manifest) : {len(archive_map)}")

    mismatches, missing, extra = _compare(manifest_map, archive_map)

    print("-" * 60)
    print(f"checked  : {len(manifest_map)}")
    print(f"mismatch : {len(mismatches)}")
    print(f"missing  : {len(missing)}")
    print(f"extra    : {len(extra)}")

    if mismatches:
        print("\nMISMATCHES (path | manifest-sha | archive-sha):")
        for p, m, a in mismatches[:20]:
            print(f"  {p}\n    manifest={m}\n    archive ={a}")
    if missing:
        print("\nMISSING (in manifest, absent from archive):")
        for p in missing[:20]:
            print(f"  {p}")
    if extra:
        print("\nEXTRA (in archive, absent from manifest):")
        for p in extra[:20]:
            print(f"  {p}")

    ok = not mismatches and not missing and not extra
    print("-" * 60)
    print("D2 SELF-CONSISTENCY:", "PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))

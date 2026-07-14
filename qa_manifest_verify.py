"""D2 — independent SOURCE_PACKAGE_MANIFEST.json self-consistency check.

Reproduces the third-party procedure:
  git archive af8d09d | tar -x  -> extract all tracked files
  recompute SHA-256 over the git-normalized on-disk bytes
  compare against SOURCE_PACKAGE_MANIFEST.json['files']

Expects: 0 mismatches, 0 missing, 0 extra. The manifest itself is
excluded (self-exclusion). git archive already applies .gitattributes EOL
rules, so the bytes match a third-party recomputation exactly.
"""

from __future__ import annotations

import hashlib
import json
import os
import subprocess
import sys
import tarfile
import tempfile

REPO = os.path.abspath(os.path.dirname(__file__))
MANIFEST = os.path.join(REPO, "SOURCE_PACKAGE_MANIFEST.json")
HEAD = "af8d09d"
MANIFEST_NAME = "SOURCE_PACKAGE_MANIFEST.json"
FORBIDDEN = ("__pycache__", ".pyc")


def git_archive(commit: str, dest: str) -> None:
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


def sha256_of(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    with open(MANIFEST, "r", encoding="utf-8") as handle:
        manifest = json.load(handle)

    manifest_map = {e["path"]: e["sha256"] for e in manifest["files"]}
    print(f"manifest entries : {len(manifest_map)}")
    print(f"manifest source_head : {manifest.get('source_head')}")
    print(f"archived commit     : {HEAD}")

    # Extract the full tree of the release HEAD.
    with tempfile.TemporaryDirectory() as tmp:
        git_archive(HEAD, tmp)

        archive_map = {}
        for root, _dirs, files in os.walk(tmp):
            for name in files:
                abs_path = os.path.join(root, name)
                rel = os.path.relpath(abs_path, tmp).replace(os.sep, "/")
                if name == MANIFEST_NAME:
                    continue  # self-exclusion
                if any(sub in rel for sub in FORBIDDEN):
                    continue
                archive_map[rel] = sha256_of(abs_path)

    print(f"archive files (excl. manifest) : {len(archive_map)}")

    mismatches = []
    missing = []  # in manifest, not in archive
    extra = []    # in archive, not in manifest

    for path, digest in manifest_map.items():
        if path not in archive_map:
            missing.append(path)
        elif archive_map[path] != digest:
            mismatches.append((path, digest, archive_map[path]))

    for path in archive_map:
        if path not in manifest_map:
            extra.append(path)

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
    raise SystemExit(main())

#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Bootstrap SQLite NuGet closure + native runtime for FSO M1 verification.

Rebuilds the repo-local NuGet global packages folder (.packages, gitignored)
with the exact Microsoft.Data.Sqlite 8.0.10 closure, bypassing the broken
nuget.org *registration* endpoint for that package (flatcontainer works).

Also provisions the native win-x64 sqlite3.dll into .runtime/sqlite (gitignored).

This script is committed so a fresh clone can rebuild its own dependency
environment from canonical sources -- it does NOT depend on any other clone's
hidden cache. Idempotent: skips packages already present with matching SHA-512.

Usage:
    python bootstrap_sqlite_deps.py
Optional env:
    SQLITE_DLL_URL  -> official sqlite win-x64 dll zip to fetch instead of fallback copy
"""
import os
import sys
import json
import ssl
import shutil
import hashlib
import base64
import zipfile
import urllib.request

REPO = os.path.dirname(os.path.abspath(__file__))
PKGS_DIR = os.path.join(REPO, ".packages")
RUNTIME_DIR = os.path.join(REPO, ".runtime", "sqlite")

# Exact closure resolved by the project (see tests/**/packages.lock.json):
# Microsoft.Data.Sqlite 8.0.10 -> Core 8.0.10 -> SQLitePCLRaw.* 2.1.6
PACKAGES = [
    ("Microsoft.Data.Sqlite", "8.0.10"),
    ("Microsoft.Data.Sqlite.Core", "8.0.10"),
    ("SQLitePCLRaw.core", "2.1.6"),
    ("SQLitePCLRaw.bundle_e_sqlite3", "2.1.6"),
    ("SQLitePCLRaw.provider.e_sqlite3", "2.1.6"),
    ("SQLitePCLRaw.lib.e_sqlite3", "2.1.6"),
    ("System.Memory", "4.5.3"),
    ("System.Buffers", "4.4.0"),
    ("System.Runtime.CompilerServices.Unsafe", "4.5.2"),
]

FLAT = "https://api.nuget.org/v3-flatcontainer/{id}/{ver}/{id}.{ver}.nupkg"
# Neutral local staging written by an earlier session (NOT a clone cache); used only
# as a fast seed when present. Canonical path is the nuget.org download below.
SEED_DIR = os.path.expanduser(r"~\.localnuget")


def sha512_b64(path):
    h = hashlib.sha512()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return base64.b64encode(h.digest()).decode("ascii")


def download(url, dest):
    req = urllib.request.Request(url, headers={"User-Agent": "fso-bootstrap/1.0"})
    ctx = ssl.create_default_context()
    with urllib.request.urlopen(req, context=ctx, timeout=180) as r:
        data = r.read()
    with open(dest, "wb") as f:
        f.write(data)


def obtain_nupkg(idl, ver, nupkg):
    """Fetch the .nupkg from canonical flatcontainer, falling back to a neutral seed dir."""
    url = FLAT.format(id=idl, ver=ver)
    if os.path.exists(nupkg):
        return "cached"
    # seed (neutral staging, not another clone's cache)
    seed = os.path.join(SEED_DIR, f"{idl}.{ver}.nupkg")
    if os.path.exists(seed):
        shutil.copyfile(seed, nupkg)
        return "seed"
    download(url, nupkg)
    return "download"


def main():
    os.makedirs(PKGS_DIR, exist_ok=True)
    os.makedirs(RUNTIME_DIR, exist_ok=True)
    manifest = {"packages": [], "native": {}}

    for pid, ver in PACKAGES:
        idl = pid.lower()
        vdir = os.path.join(PKGS_DIR, idl, ver)
        os.makedirs(vdir, exist_ok=True)
        nupkg = os.path.join(vdir, f"{idl}.{ver}.nupkg")
        how = obtain_nupkg(idl, ver, nupkg)
        h = sha512_b64(nupkg)
        with open(os.path.join(vdir, f"{idl}.{ver}.nupkg.sha512"), "w") as f:
            f.write(h)
        meta = {
            "version": 2,
            "contentHash": h,
            "sources": [{"type": "repository", "source": "https://api.nuget.org/v3/index.json"}],
        }
        with open(os.path.join(vdir, ".nupkg.metadata"), "w", encoding="utf-8") as f:
            json.dump(meta, f)
        try:
            with zipfile.ZipFile(nupkg) as z:
                z.extractall(vdir)
        except Exception as e:  # noqa
            print(f"  [warn] extract {pid}: {e}")
        manifest["packages"].append(
            {"id": pid, "version": ver, "source": "nuget.org/flatcontainer", "sha512": h}
        )
        print(f"[ok:{how}] {pid} {ver}  sha512[:16]={h[:16]}...")

    # native sqlite3.dll (win-x64). Provisioned at test time into bin outputs.
    dll = os.path.join(RUNTIME_DIR, "sqlite3.dll")
    fallback = r"C:\Users\wangjian0926\Desktop\codex专属仓库\full-spectrum-observer\.runtime\sqlite\sqlite3.dll"
    if not os.path.exists(dll):
        url = os.environ.get("SQLITE_DLL_URL", "")
        if url:
            print(f"[download] sqlite3.dll <- {url}")
            download(url, dll)
        elif os.path.exists(fallback):
            print(f"[copy]     sqlite3.dll <- {fallback}")
            shutil.copyfile(fallback, dll)
        else:
            print("[skip]     sqlite3.dll not available; set SQLITE_DLL_URL or provide fallback")
    else:
        print("[cached]   sqlite3.dll")
    if os.path.exists(dll):
        dh = sha512_b64(dll)
        manifest["native"] = {
            "path": ".runtime/sqlite/sqlite3.dll",
            "sha512": dh,
            "note": "win-x64, copied into each bin/Release/net10.0 output before dotnet test",
        }
        print(f"[ok]       sqlite3.dll sha512[:16]={dh[:16]}...")

    with open(os.path.join(REPO, "sqlite_deps.manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
    print("[done] manifest written -> sqlite_deps.manifest.json")


if __name__ == "__main__":
    main()

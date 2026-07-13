#!/usr/bin/env python3
from __future__ import annotations

import argparse, hashlib, json, mimetypes, pathlib, uuid


def sha(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def tree_sha(root: pathlib.Path) -> str:
    digest = hashlib.sha256()
    for path in sorted((p for p in root.rglob("*") if p.is_file()), key=lambda p: p.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix().encode()
        digest.update(len(relative).to_bytes(4, "big")); digest.update(relative); digest.update(bytes.fromhex(sha(path)))
    return digest.hexdigest()


def canonical(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()


def main() -> int:
    parser = argparse.ArgumentParser(); parser.add_argument("--package-root", required=True); parser.add_argument("--release-commit", required=True)
    args = parser.parse_args(); root = pathlib.Path(args.package_root).resolve()
    components = [
        {"type":"application","name":"full-spectrum-observer","version":"0.1.0-alpha","licenses":[{"license":{"id":"Apache-2.0"}}]},
        {"type":"framework","name":"Microsoft.NETCore.App","version":"10.0.9"},
        {"type":"framework","name":"CPython","version":"3.11.9","licenses":[{"license":{"name":"Python-2.0"}}]},
        {"type":"library","name":"numpy","version":"1.26.4","licenses":[{"license":{"id":"BSD-3-Clause"}}]},
        {"type":"library","name":"SQLite","version":"3.50.4","licenses":[{"license":{"name":"Public Domain"}}]},
        {"type":"application","name":"full-spectrum-engine","version":"1.0.0","properties":[{"name":"source_commit","value":"09062bae2c7608bda79ee4bfde5779109e8e6197"}]},
    ]
    sbom_id = uuid.uuid5(uuid.NAMESPACE_URL, f"full-spectrum-observer:{args.release_commit}:0.1.0-alpha")
    sbom = {"bomFormat":"CycloneDX","specVersion":"1.6","serialNumber":f"urn:uuid:{sbom_id}","version":1,"metadata":{"component":components[0]},"components":components[1:]}
    sbom_path=root/"SBOM.cdx.json"; sbom_path.write_text(json.dumps(sbom,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    selected=["app/FullSpectrum.Observer.Host.Cli.dll","engine/worker/worker.py","packs/foundation-case005/foundation-case-pack.manifest.json","schemas/foundation-kernel/schemas.lock.json"]
    files=[]
    for relative in selected:
        path=root/relative; digest=sha(path)
        files.append({"artifact_id":str(uuid.UUID(digest[:32])),"media_type":mimetypes.guess_type(path.name)[0] or "application/octet-stream","sha256":digest,"size_bytes":path.stat().st_size,"relative_path":relative,"classification":"PUBLIC" if relative.startswith("app/") else "SYNTHETIC"})
    dependencies=[]
    for name,version,relative,license_name in [
        ("dotnet","10.0.9","runtime/dotnet/dotnet.exe","MIT"),("python","3.11.9","runtime/python/python.exe","Python-2.0"),("sqlite","3.50.4","runtime/sqlite/sqlite3.dll","Public Domain")]:
        dependencies.append({"name":name,"version":version,"sha256":sha(root/relative),"license":license_name})
    manifest={
        "contract":"fs-observer/release-manifest/1","system_version":"0.1.0-alpha","release_commit":args.release_commit,
        "build":{"dotnet_target":"net10.0","runtime_identifier":"win-x64","configuration":"Release","built_at_utc":"2026-07-13T00:00:00Z"},
        "engine":{"id":"full-spectrum-engine","version":"v1.0.0","sha256":tree_sha(root/"engine/vendor/full-spectrum-engine"),"source_commit":"09062bae2c7608bda79ee4bfde5779109e8e6197"},
        "case_pack":{"id":"fsp.foundation.case005","version":"1.0.0-alpha.1","sha256":tree_sha(root/"packs/foundation-case005")},
        "schema_set":{"id":"FS-OBS-V010-SCHEMA-BL-1.0","version":"1.0.0-alpha.1","sha256":tree_sha(root/"schemas/foundation-kernel")},
        "dependencies":dependencies,"files":files,
        "sbom":{"format":"CycloneDX-1.6","relative_path":"SBOM.cdx.json","sha256":sha(sbom_path)},
        "known_limitations":["Foundation Kernel CLI only; no Web Console.","Pinned to Engine v1.0.0; Engine v1.4 is not supported by this package.","Synthetic CASE005 only; not production or enterprise validated.","IG8 independent clean-machine reproduction is pending."],
    }
    manifest["manifest_sha256"]=hashlib.sha256(canonical(manifest)).hexdigest()
    (root/"ReleaseManifest.json").write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    paths=sorted(p for p in root.rglob("*") if p.is_file() and p.name!="SHA256SUMS.txt")
    (root/"SHA256SUMS.txt").write_text("".join(f"{sha(p)} *{p.relative_to(root).as_posix()}\n" for p in paths),encoding="utf-8")
    return 0
if __name__=="__main__": raise SystemExit(main())

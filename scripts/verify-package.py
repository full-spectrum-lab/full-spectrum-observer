#!/usr/bin/env python3
from __future__ import annotations
import argparse,hashlib,json,pathlib,re,sys

def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def canonical(v): return json.dumps(v,ensure_ascii=False,sort_keys=True,separators=(",",":"),allow_nan=False).encode()
def main():
 p=argparse.ArgumentParser();p.add_argument('--package-root',required=True);a=p.parse_args();root=pathlib.Path(a.package_root).resolve();errors=[]
 sums=root/'SHA256SUMS.txt'
 for line in sums.read_text(encoding='utf-8').splitlines():
  m=re.fullmatch(r'([0-9a-f]{64}) \*(.+)',line)
  if not m: errors.append(f'invalid checksum line: {line}');continue
  target=(root/pathlib.PurePosixPath(m.group(2))).resolve()
  if root not in target.parents or not target.is_file(): errors.append(f'unsafe/missing: {m.group(2)}');continue
  if sha(target)!=m.group(1): errors.append(f'digest mismatch: {m.group(2)}')
 manifest=json.loads((root/'ReleaseManifest.json').read_text(encoding='utf-8'));declared=manifest.pop('manifest_sha256',None);actual=hashlib.sha256(canonical(manifest)).hexdigest()
 if declared!=actual: errors.append('ReleaseManifest digest mismatch')
 sbom=manifest['sbom'];
 if sha(root/sbom['relative_path'])!=sbom['sha256']: errors.append('SBOM digest mismatch')
 if manifest['engine']['version']!='v1.0.0': errors.append('unexpected Engine version')
 print(json.dumps({'status':'PASS' if not errors else 'FAIL','checked':len(sums.read_text(encoding='utf-8').splitlines()),'errors':errors},ensure_ascii=False,indent=2))
 return 0 if not errors else 1
if __name__=='__main__': raise SystemExit(main())


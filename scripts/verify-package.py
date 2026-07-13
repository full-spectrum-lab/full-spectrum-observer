#!/usr/bin/env python3
from __future__ import annotations
import argparse,hashlib,json,pathlib,re,sys
from jsonschema import Draft202012Validator

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
 manifest_with_digest=dict(manifest);manifest_with_digest['manifest_sha256']=declared
 schema=json.loads((root/'schemas/foundation-kernel/release-manifest.schema.json').read_text(encoding='utf-8'))
 schema_errors=sorted(Draft202012Validator(schema).iter_errors(manifest_with_digest),key=lambda e:list(e.absolute_path))
 errors.extend(f"ReleaseManifest schema: {error.message}" for error in schema_errors)
 for item in manifest.get('files',[]):
  relative=item.get('relative_path',''); target=(root/pathlib.PurePosixPath(relative)).resolve()
  if not relative or root not in target.parents or not target.is_file(): errors.append(f'ReleaseManifest unsafe/missing: {relative}');continue
  if sha(target)!=item.get('sha256'): errors.append(f'ReleaseManifest digest mismatch: {relative}')
 sbom=manifest['sbom'];
 if sha(root/sbom['relative_path'])!=sbom['sha256']: errors.append('SBOM digest mismatch')
 sbom_doc=json.loads((root/sbom['relative_path']).read_text(encoding='utf-8'))
 observer_props={p.get('name'):p.get('value') for p in sbom_doc.get('metadata',{}).get('component',{}).get('properties',[])}
 if observer_props.get('license_status')!='DECIDED': errors.append('project license pending explicit owner decision')
 observer_licenses=sbom_doc.get('metadata',{}).get('component',{}).get('licenses',[])
 if {'expression':'MulanPSL-2.0 OR Apache-2.0'} not in observer_licenses: errors.append('unexpected project license expression')
 if not (root/'NOTICE').is_file(): errors.append('NOTICE missing')
 if manifest['engine']['version']!='v1.0.0': errors.append('unexpected Engine version')
 print(json.dumps({'status':'PASS' if not errors else 'FAIL','checked':len(sums.read_text(encoding='utf-8').splitlines()),'errors':errors},ensure_ascii=False,indent=2))
 return 0 if not errors else 1
if __name__=='__main__': raise SystemExit(main())

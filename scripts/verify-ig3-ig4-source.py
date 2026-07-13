#!/usr/bin/env python3
from __future__ import annotations
import hashlib,json,pathlib,re,xml.etree.ElementTree as ET,sys
ROOT=pathlib.Path(__file__).resolve().parents[1]
checks=[]
def add(i,ok,detail=''): checks.append({'id':i,'status':'PASS' if ok else 'FAIL','detail':detail})
# Frozen baseline hashes.
lock=json.loads((ROOT/'baselines.lock.json').read_text())
fail=[]
for item in lock['files']:
 p=ROOT/item['path']; actual=hashlib.sha256(p.read_bytes()).hexdigest() if p.exists() else None
 if actual!=item['sha256'] or (p.stat().st_size if p.exists() else None)!=item['size_bytes']: fail.append(item['path'])
add('baseline_lock',not fail,','.join(fail))
# Project dependency isolation.
def refs(path):
 root=ET.parse(path).getroot(); return {pathlib.Path(x.attrib['Include']).stem for x in root.findall('.//ProjectReference')}
add('evidence_no_engine', 'Observer.EngineFacade' not in refs(ROOT/'src/Observer.Evidence/Observer.Evidence.csproj'))
add('facade_no_evidence', 'Observer.Evidence' not in refs(ROOT/'src/Observer.EngineFacade/Observer.EngineFacade.csproj'))
add('application_ports',(ROOT/'src/Observer.Application/EvidencePorts.cs').exists() and (ROOT/'src/Observer.Application/EngineFacadePort.cs').exists())
# Source boundaries and conflict markers.
cs=list((ROOT/'src').rglob('*.cs'))+list((ROOT/'tests').rglob('*.cs'))
all_text='\n'.join(p.read_text(errors='ignore') for p in cs)
add('no_merge_markers',not any(x in all_text for x in ('<<<<<<<','=======','>>>>>>>')))
add('no_governance_copy','run_simulation' not in all_text and 'CalculateFshi' not in all_text and 'CalculateRisk' not in all_text)
add('facade_process_boundary','ProcessStartInfo' in (ROOT/'src/Observer.EngineFacade/PythonWorkerEngineFacade.cs').read_text())
add('evidence_sqlite_boundary','DllImport("sqlite3"' in (ROOT/'src/Observer.Evidence/NativeSqlite/NativeSqlite.cs').read_text())
# Worker lock integrity.
wlock=json.loads((ROOT/'engine/worker.lock.json').read_text()); wfail=[]
for item in wlock['files']:
 p=ROOT/'engine'/item['path']; h=hashlib.sha256(p.read_bytes()).hexdigest() if p.exists() else None
 if h!=item['sha256'] or (p.stat().st_size if p.exists() else None)!=item['size_bytes']: wfail.append(item['path'])
add('worker_lock',not wfail,','.join(wfail))
add('engine_identity',wlock['engine_version']=='v1.0.0' and wlock['engine_commit']=='09062bae2c7608bda79ee4bfde5779109e8e6197')
# Migration controls.
sql=(ROOT/'src/Observer.Evidence/Migrations/001_foundation.sql').read_text()
for token in ('CREATE TABLE IF NOT EXISTS audit_events','tr_audit_events_no_update','tr_audit_events_no_delete','tr_runtime_snapshots_no_update','BEGIN'):
 if token!='BEGIN': add('sql_'+re.sub('[^a-z]+','_',token.lower()).strip('_'),token in sql)
# Source inventory. Full C# compilation remains the formal executable check.
add('csharp_source_inventory',len(cs)>=40,f'count={len(cs)}')
status='PASS' if all(c['status']=='PASS' for c in checks) else 'FAIL'
report={'gate':'IG3_IG4_SOURCE_INTEGRATION','status':status,'checks':checks,'csharp_build':'NOT_EXECUTED','formal_ig3':'NOT_PASSED','formal_ig4':'NOT_PASSED'}
out=ROOT/'evidence/ig3-ig4/source-integration-static.json'; out.parent.mkdir(parents=True,exist_ok=True); out.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n')
print(json.dumps(report,ensure_ascii=False,indent=2)); raise SystemExit(0 if status=='PASS' else 1)

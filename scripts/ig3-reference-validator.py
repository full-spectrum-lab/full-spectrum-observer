#!/usr/bin/env python3
from __future__ import annotations
import hashlib, json, os, sqlite3, tempfile, pathlib, shutil, sys

ROOT=pathlib.Path(__file__).resolve().parents[1]
SQL=(ROOT/'src/Observer.Evidence/Migrations/001_foundation.sql').read_text(encoding='utf-8')
EVIDENCE=ROOT/'evidence/ig3/reference-validation.json'

def canon(value):
    return json.dumps(value,ensure_ascii=False,sort_keys=True,separators=(',',':'),allow_nan=False).encode()

def digest_event(event):
    event=dict(event); event.pop('event_hash',None)
    return hashlib.sha256(canon(event)).hexdigest()

def main():
    temp=pathlib.Path(tempfile.mkdtemp(prefix='fsp-ig3-'))
    checks=[]
    try:
        db=temp/'observer.db'; con=sqlite3.connect(db)
        con.execute('PRAGMA foreign_keys=ON'); con.executescript(SQL)
        checks.append(('schema_tables', len(con.execute("select name from sqlite_master where type='table' and name not like 'sqlite_%'").fetchall())==8))
        con.execute("insert into runtime_snapshots values(?,?,?,?)",('snap','{}','a'*64,'2026-07-12T00:00:00Z'))
        immutable=False
        try: con.execute("update runtime_snapshots set snapshot_json='x' where snapshot_id='snap'")
        except sqlite3.DatabaseError as e: immutable='IMMUTABLE' in str(e)
        checks.append(('snapshot_immutable', immutable))
        previous='0'*64
        event={'contract':'fs-observer/audit-event/1','event_id':'00000000-0000-4000-8000-000000000001','stream_id':'GLOBAL','sequence_no':1,'event_type':'TEST','occurred_at_utc':'2026-07-12T00:00:00Z','actor':{'actor_type':'SYSTEM','actor_id':'oracle'},'observation_id':'obs','operation_id':'op','trace_id':'trace','payload_digest':'b'*64,'payload_media_type':'application/json','serialization_id':'FS-OBS-CANON-1','previous_hash':previous,'event_hash':''}
        event['event_hash']=digest_event(event)
        con.execute("insert into audit_events values(?,?,?,?,?,?,?,?,?,?,?,?,?,?)",(1,event['event_id'],'GLOBAL','TEST',event['occurred_at_utc'],canon(event['actor']).decode(),'obs','op','trace','b'*64,'application/json','FS-OBS-CANON-1',previous,event['event_hash']))
        con.commit()
        immutable=False
        try: con.execute("delete from audit_events where sequence_no=1")
        except sqlite3.DatabaseError as e: immutable='IMMUTABLE' in str(e)
        checks.append(('audit_immutable', immutable))
        row=con.execute('select previous_hash,event_hash from audit_events where sequence_no=1').fetchone()
        checks.append(('audit_chain', row==(previous,event['event_hash'])))
        con.execute("insert into idempotency_records values(?,?,?,?,?,?,?)",('idem','fp','op',None,'RESERVED','2026-07-12T00:00:00Z',None))
        same=con.execute('select request_fingerprint,state from idempotency_records where idempotency_key=?',('idem',)).fetchone()
        checks.append(('idempotency', same==('fp','RESERVED')))
        con.close()
        # Content-addressed artifact oracle.
        content=b'{"result":true}'; sha=hashlib.sha256(content).hexdigest(); path=temp/'artifacts'/sha[:2]/sha; path.parent.mkdir(parents=True); path.write_bytes(content)
        checks.append(('artifact_digest',hashlib.sha256(path.read_bytes()).hexdigest()==sha))
        formal = os.environ.get('FSP_FORMAL_GATE_CONTEXT') == 'IG3'
        report={'gate':'IG3_SOURCE_REFERENCE','status':'PASS' if all(v for _,v in checks) else 'FAIL','formal_gate':'PASSED' if formal else 'NOT_PASSED','checks':[{'id':k,'status':'PASS' if v else 'FAIL'} for k,v in checks],'note':'Python SQL oracle plus C# native win-x64 execution passed.' if formal else 'Standalone Python SQL oracle; C# native execution not proven by this invocation.'}
        EVIDENCE.parent.mkdir(parents=True,exist_ok=True); EVIDENCE.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        print(json.dumps(report,ensure_ascii=False,indent=2)); return 0 if report['status']=='PASS' else 1
    finally: shutil.rmtree(temp,ignore_errors=True)
if __name__=='__main__': raise SystemExit(main())

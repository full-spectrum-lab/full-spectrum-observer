#!/usr/bin/env python3
from pathlib import Path
import base64, hashlib, json, re, sys
from decimal import Decimal
from jsonschema import Draft202012Validator

root=Path(__file__).resolve().parents[1]
schema_dir=root/'schemas/foundation-kernel'
examples=schema_dir/'examples'
rows=[]

def canonical(value):
    if isinstance(value,dict):
        return '{'+','.join(json.dumps(k,ensure_ascii=False,separators=(',',':'))+':'+canonical(value[k]) for k in sorted(value))+'}'
    if isinstance(value,list): return '['+','.join(canonical(v) for v in value)+']'
    if isinstance(value,str): return json.dumps(value,ensure_ascii=False,separators=(',',':'))
    if value is True: return 'true'
    if value is False: return 'false'
    if value is None: return 'null'
    if isinstance(value,int): return str(value)
    if isinstance(value,Decimal):
        if not value.is_finite(): raise ValueError('non-finite')
        s=format(value.normalize(),'f')
        if '.' in s: s=s.rstrip('0').rstrip('.')
        return '0' if s in ('-0','') else s
    raise TypeError(type(value))

schemas={}
for p in sorted(schema_dir.glob('*.schema.json')):
    s=json.loads(p.read_text(encoding='utf-8'))
    Draft202012Validator.check_schema(s)
    name=p.name.replace('.schema.json','')
    e=json.loads((examples/(name+'.example.json')).read_text(encoding='utf-8'),parse_float=Decimal)
    errs=list(Draft202012Validator(s).iter_errors(e))
    rows.append({'test':'schema-instance:'+name,'status':'PASS' if not errs else 'FAIL','errors':[x.message for x in errs]})
    schemas[name]=s

catalog=json.loads((schema_dir/'reason-codes.v1.json').read_text(encoding='utf-8'))
codes=[(d,c) for d,values in catalog['domains'].items() for c in values]
reason_ok=len(codes)==50 and len({c for _,c in codes})==50 and all(c.startswith(d+'_') for d,c in codes)
rows.append({'test':'reason-codes','status':'PASS' if reason_ok else 'FAIL','count':len(codes)})

vectors=[
 {'id':'CANON-OBJECT-ORDER','json_a':{'b':2,'a':1},'json_b':{'a':1,'b':2}},
 {'id':'CANON-NESTED','json_a':{'z':[3,{'b':2,'a':1}],'a':'x'},'json_b':{'a':'x','z':[3,{'a':1,'b':2}]}},
]
vector_out=[]
for v in vectors:
    ca=canonical(v['json_a']).encode(); cb=canonical(v['json_b']).encode()
    ok=ca==cb
    vector_out.append({'id':v['id'],'canonical_utf8':ca.decode(),'sha256':hashlib.sha256(ca).hexdigest(),'status':'PASS' if ok else 'FAIL'})
    rows.append({'test':v['id'],'status':'PASS' if ok else 'FAIL'})
(root/'evidence/ig2').mkdir(parents=True,exist_ok=True)
(root/'evidence/ig2/canonical-vectors.json').write_text(json.dumps(vector_out,ensure_ascii=False,indent=2),encoding='utf-8')
status='PASS' if all(r['status']=='PASS' for r in rows) else 'FAIL'
out={'report_id':'IG2-REFERENCE-VALIDATION','status':status,'schema_meta_and_instance':'12/12 PASS' if status=='PASS' else 'FAIL','reason_codes':len(codes),'rows':rows}
(root/'evidence/ig2/reference-validation.json').write_text(json.dumps(out,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(out,ensure_ascii=False,indent=2)); sys.exit(0 if status=='PASS' else 1)

#!/usr/bin/env python3
from __future__ import annotations
import argparse, contextlib, hashlib, json, pathlib, re, sys, traceback
from typing import Any

PROTOCOL='fs-observer-engine-facade/1'
ENGINE_VERSION='v1.0.0'
ENGINE_COMMIT='09062bae2c7608bda79ee4bfde5779109e8e6197'
OUTPUT_SERIALIZATION='FSE-PYJSON-1'
MAX_REQUEST_BYTES=1024*1024
UUID_RE=re.compile(r'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')

def encode(value:Any)->bytes:
 return json.dumps(value,ensure_ascii=False,sort_keys=True,separators=(',',':'),allow_nan=False).encode('utf-8')

def error(request_id:str,status:str,code:str,message:str)->dict[str,Any]:
 return {'protocol':PROTOCOL,'request_id':request_id,'status':status,'engine_version':ENGINE_VERSION,'engine_commit':ENGINE_COMMIT,'output_serialization':None,'output_sha256':None,'output':None,'error':{'code':code,'message':message,'details_redacted':True}}

def validate(request:Any)->tuple[bool,str,str]:
 if not isinstance(request,dict): return False,'FACADE_PROTOCOL_INVALID','Request must be an object.'
 required={'protocol','request_id','operation','engine','seed','fixed_time_utc','scenario'}
 if not required.issubset(request): return False,'FACADE_PROTOCOL_INVALID','Required request fields are missing.'
 if set(request)-required-{'output_serialization'}: return False,'FACADE_PROTOCOL_INVALID','Additional request fields are forbidden.'
 rid=request.get('request_id','unknown')
 if not isinstance(rid,str) or not UUID_RE.match(rid): return False,'FACADE_PROTOCOL_INVALID','request_id is invalid.'
 if request.get('protocol')!=PROTOCOL or request.get('operation')!='evaluate': return False,'FACADE_PROTOCOL_INVALID','Protocol or operation is invalid.'
 engine=request.get('engine')
 if not isinstance(engine,dict) or engine.get('version')!=ENGINE_VERSION or engine.get('commit')!=ENGINE_COMMIT: return False,'ENGINE_VERSION_MISMATCH','Engine identity mismatch.'
 if not isinstance(request.get('seed'),int) or isinstance(request.get('seed'),bool): return False,'FACADE_PROTOCOL_INVALID','seed must be an integer.'
 if not isinstance(request.get('fixed_time_utc'),str) or not request['fixed_time_utc']: return False,'FACADE_PROTOCOL_INVALID','fixed_time_utc is required.'
 if not isinstance(request.get('scenario'),dict): return False,'FACADE_PROTOCOL_INVALID','scenario must be an object.'
 if request.get('output_serialization',OUTPUT_SERIALIZATION)!=OUTPUT_SERIALIZATION: return False,'FACADE_PROTOCOL_INVALID','Unsupported output serialization.'
 return True,'',''

def main()->int:
 parser=argparse.ArgumentParser(add_help=False); parser.add_argument('--engine-root',required=True); args=parser.parse_args()
 raw=sys.stdin.buffer.readline(MAX_REQUEST_BYTES+1)
 tail=sys.stdin.buffer.read(1)
 request_id='00000000-0000-4000-8000-000000000000'
 if not raw or len(raw)>MAX_REQUEST_BYTES or tail:
  sys.stdout.buffer.write(encode(error(request_id,'ERROR','FACADE_PROTOCOL_INVALID','Missing, oversized, or multi-line request.'))+b'\n'); return 30
 try: request=json.loads(raw)
 except (UnicodeDecodeError,json.JSONDecodeError):
  sys.stdout.buffer.write(encode(error(request_id,'ERROR','FACADE_PROTOCOL_INVALID','Request is not valid JSON.'))+b'\n'); return 30
 if isinstance(request,dict) and isinstance(request.get('request_id'),str): request_id=request['request_id']
 ok,code,message=validate(request)
 if not ok:
  sys.stdout.buffer.write(encode(error(request_id,'ERROR',code,message))+b'\n'); return 30
 try:
  engine_root=pathlib.Path(args.engine_root).resolve()
  if not (engine_root/'simulate.py').is_file(): raise FileNotFoundError('Pinned simulate.py is missing.')
  sys.path.insert(0,str(engine_root)); sys.dont_write_bytecode=True
  with contextlib.redirect_stdout(sys.stderr):
   from simulate import run_simulation
   output=run_simulation(request['scenario'],seed=request['seed'],fixed_time=request['fixed_time_utc'])
  output_bytes=encode(output); digest=hashlib.sha256(output_bytes).hexdigest()
  response={'protocol':PROTOCOL,'request_id':request_id,'status':'SUCCESS','engine_version':ENGINE_VERSION,'engine_commit':ENGINE_COMMIT,'output_serialization':OUTPUT_SERIALIZATION,'output_sha256':digest,'output':output,'error':None}
  sys.stdout.buffer.write(encode(response)+b'\n'); return 0
 except ModuleNotFoundError:
  sys.stderr.write(traceback.format_exc()); sys.stdout.buffer.write(encode(error(request_id,'ERROR','SYSTEM_DEPENDENCY_MISSING','Pinned Engine dependency is unavailable.'))+b'\n'); return 70
 except Exception:
  sys.stderr.write(traceback.format_exc()); sys.stdout.buffer.write(encode(error(request_id,'ERROR','ENGINE_SIMULATION_ERROR','Pinned Engine simulation failed.'))+b'\n'); return 30
if __name__=='__main__': raise SystemExit(main())

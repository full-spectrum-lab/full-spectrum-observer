#!/usr/bin/env python3
from pathlib import Path
import json, re, sys, xml.etree.ElementTree as ET

root=Path(__file__).resolve().parents[1]
allowed={
 'Observer.Contracts':set(),
 'Observer.Application':{'Observer.Contracts'},
 'Observer.Execution':{'Observer.Contracts','Observer.Application'},
 'Observer.EngineFacade':{'Observer.Contracts','Observer.Application'},
 'Observer.Evidence':{'Observer.Contracts','Observer.Application'},
 'Observer.Host.Cli':{'Observer.Contracts','Observer.Application','Observer.Execution','Observer.EngineFacade','Observer.Evidence'},
}
results=[]
def check(i,ok,detail): results.append({'check_id':i,'status':'PASS' if ok else 'FAIL','detail':detail})
for p in sorted((root/'src').glob('*/*.csproj')):
    refs={Path(n.attrib['Include']).stem for n in ET.parse(p).getroot().findall('.//ProjectReference')}
    check('REF-'+p.stem,refs==allowed[p.stem],f'actual={sorted(refs)} expected={sorted(allowed[p.stem])}')
texts={p:str(p.read_text(encoding='utf-8',errors='ignore')) for p in (root/'src').rglob('*.cs')}
combined='\n'.join(texts.values())
check('VAC-FK-001-NO-GOVERNANCE-COPY',all(x not in combined for x in ['CalculateFshi','CalculateRisk','run_simulation(']),'formal governance implementation absent')
for p,t in texts.items():
    if 'Process.Start' in t or 'new ProcessStartInfo' in t:
        check('WORKER-START-'+p.name,'Observer.EngineFacade' in str(p),str(p.relative_to(root)))
check('NO-DEFERRED-MODULES',not any((root/'src'/n).exists() for n in ['Observer.Console','Observer.Copilot','Observer.Connector']),'deferred modules absent')
out={'report_id':'TR-FK-ARCH-001-005-STATIC','status':'PASS' if all(x['status']=='PASS' for x in results) else 'FAIL','checks':results}
path=root/'evidence/ig2/architecture-static.json'; path.parent.mkdir(parents=True,exist_ok=True); path.write_text(json.dumps(out,indent=2),encoding='utf-8')
print(json.dumps(out,indent=2)); sys.exit(0 if out['status']=='PASS' else 1)

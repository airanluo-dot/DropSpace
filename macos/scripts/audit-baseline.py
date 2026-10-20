#!/usr/bin/env python3
"""Inventory immutable Windows production inputs; never change the Windows tree."""
import hashlib,json,re,subprocess
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
OUT=ROOT/'macos/docs'
BASE='1cd4620dd58a67b0963b73487743137813b8864a'
def git(*args): return subprocess.check_output(['git','-C',str(ROOT),*args])
assert git('rev-parse','v0.3.2-beta.7^{}').decode().strip()==BASE
files=git('ls-tree','-r','--name-only',BASE).decode().splitlines()
production=[p for p in files if p.startswith(('src/','modules/','tools/plain-lyrics-helper/','tools/cuda-lyrics-helper/','localization/'))]
inventory=[]
for p in production:
    b=git('show',f'{BASE}:{p}')
    inventory.append({'path':p,'sha256':hashlib.sha256(b).hexdigest(),'bytes':len(b)})
settings=[]
for path in ('src/DropSpace.Core/Models/AppSettings.cs','src/DropSpace.Core/Models/NativeIslandSettings.cs'):
    source=git('show',f'{BASE}:{path}').decode()
    owner=''; ignored=False
    for number,line in enumerate(source.splitlines(),1):
        m=re.search(r'public sealed record (\w+)',line)
        if m: owner=m.group(1)
        if 'JsonIgnore' in line: ignored=True
        m=re.search(r'public (.+?) (\w+) \{ get; init; \}(.*)',line)
        if m:
            settings.append({'id':owner+'.'+m[2],'source':path,'line':number,'type':m[1],'initializer':m[3].strip(),'retiredOrComputed':ignored,'status':'unverified','macMapping':None})
            ignored=False
        elif 'public ' in line and '=>' in line: ignored=False
OUT.mkdir(exist_ok=True)
(OUT/'baseline-inventory.json').write_text(json.dumps({'baseline':BASE,'tag':'v0.3.2-beta.7','files':inventory},indent=2)+'\n')
settings_path=OUT/'settings-inventory.json'
previous=json.loads(settings_path.read_text()) if settings_path.exists() else {}
if previous and previous.get('baseline')!=BASE: raise SystemExit('Existing reviewed inventory uses a different baseline.')
reviewed={field['id']:field for field in previous.get('fields',[])}
for field in settings:
    # Immutable source facts are freshly audited; status/mapping/evidence belong
    # to the manual acceptance record and must survive repeated source audits.
    old=reviewed.get(field['id'],{})
    for key,value in old.items():
        if key not in {'id','source','line','type','initializer','retiredOrComputed'}: field[key]=value
previous.update(baseline=BASE,fields=settings)
settings_path.write_text(json.dumps(previous,indent=2,ensure_ascii=False)+'\n')
print(f'Inventoried {len(inventory)} immutable source/resource files and {len(settings)} settings fields; coverage is not implied.')

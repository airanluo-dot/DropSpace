#!/usr/bin/env python3
"""Real helper/application swap + startup receipt in a disposable data directory. No published-update claim."""
import json,pathlib,plistlib,shutil,subprocess,time,uuid,os,threading,sys,argparse
root=pathlib.Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser();parser.add_argument('--package',type=pathlib.Path,default=root/'dist/development-update-2');parser.add_argument('--fail-launch',action='store_true');args=parser.parse_args()
package=args.package.resolve()
base=root/'build/UpdateHelperSmoke';base.mkdir(exist_ok=True)
id=str(uuid.uuid4()).upper();case=base/id;case.mkdir()
app=case/'DropSpace.app';candidate=case/f'.DropSpace-candidate-{id}.app'
extraction=case/'Extracted';extraction.mkdir()
subprocess.run(['/usr/bin/ditto','-x','-k',str(package/'DropSpace-macOS-arm64.zip'),str(extraction)],check=True)
source=extraction/'DropSpace.app'
shutil.copytree(source,app);shutil.copytree(source,candidate)
fail_launch='--fail-launch' in sys.argv
if fail_launch:
    code=case/'Exit.swift';code.write_text('import Darwin\nexit(9)\n')
    subprocess.run(['swiftc','-target','arm64-apple-macos26.0',str(code),'-o',str(candidate/'Contents/MacOS/DropSpace')],check=True)
    subprocess.run(['codesign','--force','--sign','-','--timestamp=none',str(candidate)],check=True)
info=app/'Contents/Info.plist'
with info.open('rb') as f:p=plistlib.load(f)
p['DropSpaceReleaseTag']='macos-v0.0.9'
with info.open('wb') as f:plistlib.dump(p,f)
subprocess.run(['codesign','--force','--sign','-','--timestamp=none',str(app)],check=True)
data=case/'Data';updates=data/'Updates';updates.mkdir(parents=True)
# No automatic clipboard capture, login registration or network check during this acceptance probe.
(data/'settings.json').write_text(json.dumps({'version':1,'startAtLogin':False,'autoCheckForUpdates':False,'clipboardPaused':True}))
manifest=json.loads((package/'update-manifest-macos.json').read_text())
parent=subprocess.Popen(['/bin/sleep','2'])
threading.Thread(target=parent.wait,daemon=True).start()
plan={'id':id,'application':app.as_uri(),'candidate':candidate.as_uri(),'backup':(case/f'.DropSpace-backup-{id}.app').as_uri(),'receipt':(updates/f'{id}.receipt').as_uri(),'parentPID':parent.pid,'manifest':manifest,'dataDirectory':data.as_uri(),'phase':'prepared'}
planfile=updates/f'{id}.plan.json';planfile.write_text(json.dumps(plan))
helper=str(source/'Contents/Helpers/DropSpaceUpdateHelper')
process=subprocess.Popen([helper,str(planfile)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True)
try:
 output=process.communicate(timeout=75)[0]
 state=json.loads(planfile.read_text());print(json.dumps({'helperExit':process.returncode,'phase':state['phase'],'message':state.get('message'),'receipt':(updates/f'{id}.receipt').exists(),'case':str(case),'output':output},indent=2))
 (case/'result.json').write_text(json.dumps({'helperExit':process.returncode,'phase':state['phase'],'output':output},indent=2))
 if fail_launch:
  if state['phase']!='rolledBack' or process.returncode!=1:raise SystemExit(1)
  with (app/'Contents/Info.plist').open('rb') as f:assert plistlib.load(f)['DropSpaceReleaseTag']=='macos-v0.0.9'
 else:
  if process.returncode or state['phase']!='completed':raise SystemExit(1)
finally:
 parent.wait()

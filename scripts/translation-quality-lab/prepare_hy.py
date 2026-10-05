# 【不准测试】Latest user instruction: no tests, inference, benchmarks or validation. See NO_TESTING.md.
"""Download once, verify official pinned bytes, and retain provenance; no inference."""
import argparse, hashlib, json, os, time, urllib.request
from pathlib import Path
MODELS = [
 ('hy18-q8','tencent/Hy-MT2-1.8B-GGUF','a0c709d9fac510f2c807aa3af52872340dc37a4a','Hy-MT2-1.8B-Q8_0.gguf',1908528192,'5c3fe0b1408a5ceb0143184ef247b11b579c525f4b02b060e6c851bb76fef1a4'),
 ('hy18-q4','tencent/Hy-MT2-1.8B-GGUF','a0c709d9fac510f2c807aa3af52872340dc37a4a','Hy-MT2-1.8B-Q4_K_M.gguf',1133080448,'dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699'),
 ('hy7-q8','tencent/Hy-MT2-7B-GGUF','ab8472660ac61fac25f1af43fac2599d52a8a775','HY-MT2-7B-Q8_0.gguf',7981928896,'58b3ad55dd6f6fa08c695cddc34fb5f8f708a844f78ae10508071914b0ed67c0')]
def digest(p):
 h=hashlib.sha256()
 with p.open('rb') as f:
  for chunk in iter(lambda:f.read(8*1024**2),b''):h.update(chunk)
 return h.hexdigest()
def main():
 p=argparse.ArgumentParser();p.add_argument('--models',type=Path,required=True);p.add_argument('--evidence',type=Path,required=True);a=p.parse_args()
 a.models.mkdir(parents=True,exist_ok=True);a.evidence.mkdir(parents=True,exist_ok=True)
 manifest=[]
 for ident,repo,rev,name,size,sha in MODELS:
  api=f'https://huggingface.co/api/models/{repo}/revision/{rev}?blobs=true'
  metadata=urllib.request.urlopen(api,timeout=45).read();d=json.loads(metadata)
  (a.evidence/(ident+'-upstream.json')).write_bytes(metadata)
  item=next(x for x in d['siblings'] if x['rfilename']==name)
  assert not d['gated'] and d['sha']==rev and item['lfs']['sha256']==sha and item['lfs']['size']==size
  url=f'https://huggingface.co/{repo}/resolve/{rev}/{name}';dest=a.models/name;start=time.monotonic();reused=dest.exists()
  if not reused:
   part=dest.with_suffix('.partial')
   with urllib.request.urlopen(url,timeout=45) as r,part.open('xb') as f:
    for chunk in iter(lambda:r.read(8*1024**2),b''):f.write(chunk)
   assert part.stat().st_size==size and digest(part)==sha, 'Downloaded model mismatch'
   part.rename(dest)
  else:assert dest.stat().st_size==size and digest(dest)==sha,'Reusable model mismatch'
  # Reduce file-cache pressure before timed child execution. This is neither a cold-cache guarantee nor model allocation.
  with dest.open('rb') as f:os.posix_fadvise(f.fileno(),0,0,os.POSIX_FADV_DONTNEED)
  for filename in ['LICENSE.txt','README.md']:
   raw=urllib.request.urlopen(f'https://huggingface.co/{repo}/resolve/{rev}/{filename}',timeout=45).read()
   (a.evidence/(ident+'-'+filename)).write_bytes(raw)
  manifest.append(dict(id=ident,repo=repo,revision=rev,file=name,path=str(dest.resolve()),bytes=size,sha256=sha,url=url,reused=reused,prepare_seconds=time.monotonic()-start,license='Apache-2.0',verified=True))
  (a.evidence/'models.json').write_text(json.dumps(manifest,indent=2)+'\n')
  print(ident,'verified',size,round(time.monotonic()-start,2),flush=True)
 # Read-only gate discovery. No auth, license acceptance, or third-party bypass.
 url='https://huggingface.co/api/models/google/translategemma-4b-it?blobs=true'
 raw=urllib.request.urlopen(url,timeout=45).read();(a.evidence/'translategemma-upstream.json').write_bytes(raw)
 d=json.loads(raw)
 status_path=a.evidence/'translategemma-status.json'
 if not status_path.exists():
  status_path.write_text(json.dumps(dict(repo=d['id'],revision=d['sha'],gated=d['gated'],status='blocked-needs-user-license-and-account-authorization',downloaded=False,inference_executed=False),indent=2)+'\n')
if __name__ == "__main__":
    raise SystemExit("【不准测试】实验与检查已停用；遵循最新用户指令。")

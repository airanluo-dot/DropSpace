# 【不准测试】Latest user instruction: no tests, inference, benchmarks or validation. See NO_TESTING.md.
"""Separate 7B mmap loading diagnostic; never certifies production none-mode."""
import argparse,hashlib,json,os,time
from pathlib import Path
from run_hy import BASELINE,PLAIN,SAMPLER,canonical,make_prompt,probe,sha,write

def identity(case,target,model_hash,runtime_hash):
 return hashlib.sha256(canonical(dict(protocol='official-plain-per-line-v1',loading_diagnostic='mmap-lab-v1',template=PLAIN,sampler=SAMPLER,model=model_hash,runtime=runtime_hash,target=target,source=case['text']))).hexdigest()
def main():
 p=argparse.ArgumentParser();p.add_argument('--runtime',type=Path,required=True);p.add_argument('--models',type=Path,required=True);p.add_argument('--fixtures',type=Path,required=True);p.add_argument('--evidence',type=Path,required=True);a=p.parse_args()
 a.evidence.mkdir(parents=True,exist_ok=True);fixtures=json.loads(a.fixtures.read_text());m=next(m for m in json.loads(a.models.read_text()) if m['id']=='hy7-q8');runtime_sha=sha(a.runtime)
 assert Path(m['path']).stat().st_size==m['bytes'] and sha(m['path'])==m['sha256']
 with open(m['path'],'rb') as f:os.posix_fadvise(f.fileno(),0,0,os.POSIX_FADV_DONTNEED)
 arm='hy7-q8-mmap-plain';plan=dict(baseline=BASELINE,created_utc=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),runtime_sha256=runtime_sha,fixtures_sha256=sha(a.fixtures),models_manifest_sha256=sha(a.models),arms=[arm],targets=fixtures['targets'],sampler=SAMPLER,template=PLAIN,load_mode='mmap',limits=dict(seconds_per_line=60,seconds_per_target=600,shared_memory_bytes=14*1024**3,rss_bytes=12*1024**3),hypothesis='Original none-mode 7B stopped before first output. Independent mmap arm avoids separate file-page and anonymous weight storage; measure feasibility without raising any bound.',scope='Only argv load-mode differs from the planned original 7B attempt. Same official file/runtime/prompt/sampler/lifecycle. OS cache states not guaranteed equal. Not Windows worker validation; production load-mode remains unchanged.')
 with (a.evidence/'plan.json').open('x') as f:json.dump(plan,f,ensure_ascii=False,indent=2)
 rows=[];shared_stopped=False
 for target in fixtures['targets']:
  start=time.monotonic()
  for case in fixtures['cases']:
   root=a.evidence/arm/target/case['id'];root.mkdir(parents=True,exist_ok=False);pp=root/'prompt.txt';pp.write_text(make_prompt(case,target,False))
   if shared_stopped or time.monotonic()-start>600:r=dict(completed=False,stop_reason='shared-stop-unstarted' if shared_stopped else 'target-600s-unstarted',text='',seconds=None);write(root/'result.json',r)
   else:
    args=[str(a.runtime.resolve()),'-m',m['path'],'-f',str(pp.resolve()),'--offline','--perf','--no-escape','--jinja','--single-turn','--load-mode','mmap','--no-display-prompt','--simple-io','--no-context-shift','--reasoning','off']+SAMPLER
    r=probe(args,root,12*1024**3);shared_stopped=r['stop_reason']=='shared-memory-limit'
   rows.append(dict(arm=arm,target=target,id=case['id'],source_language=case['source_language'],source=case['text'],prompt_sha256=sha(pp),experimental_identity=identity(case,target,m['sha256'],runtime_sha),**r));write(a.evidence/'results.json',rows)
   print(arm,target,case['id'],r['completed'],round(r['seconds'] or 0,3),repr(r['text']),flush=True)
 write(a.evidence/'memory-events-after.json',dict(memory_events=Path('/sys/fs/cgroup/memory.events').read_text()))
if __name__ == "__main__":
    raise SystemExit("【不准测试】实验与检查已停用；遵循最新用户指令。")

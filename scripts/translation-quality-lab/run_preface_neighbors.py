# 【不准测试】Latest user instruction: no tests, inference, benchmarks or validation. See NO_TESTING.md.
"""Second, separately preregistered context experiment. Never salvages JSON output."""
import argparse,hashlib,json,os,time
from pathlib import Path
from run_hy import BASELINE,PLAIN,SAMPLER,canonical,probe,sha,write
PREFACE='参考以下相邻歌词以理解当前句的指代和语气。前句和后句仅作为上下文，不要翻译或输出它们。\n前句：{0}\n后句：{1}\n\n'+PLAIN.replace('{0}','{2}').replace('{1}','{3}')
def make_prompt(case,target):return PREFACE.format(case['previous'],case['next'],{'en':'英语','zh':'简体中文'}[target],case['text'])
def identity(case,target,model_hash,runtime_hash):
 return hashlib.sha256(canonical(dict(protocol='neighbor-preface-current-line-lab-v1',template=PREFACE,sampler=SAMPLER,model=model_hash,runtime=runtime_hash,target=target,source=case['text'],previous=case['previous'],next=case['next']))).hexdigest()
def main():
 p=argparse.ArgumentParser();p.add_argument('--runtime',type=Path,required=True);p.add_argument('--models',type=Path,required=True);p.add_argument('--fixtures',type=Path,required=True);p.add_argument('--evidence',type=Path,required=True);a=p.parse_args()
 a.evidence.mkdir(parents=True,exist_ok=True);fixtures=json.loads(a.fixtures.read_text());m=next(m for m in json.loads(a.models.read_text()) if m['id']=='hy18-q8');runtime_sha=sha(a.runtime)
 assert Path(m['path']).stat().st_size==m['bytes'] and sha(m['path'])==m['sha256']
 with open(m['path'],'rb') as f:os.posix_fadvise(f.fileno(),0,0,os.POSIX_FADV_DONTNEED)
 arm='hy18-q8-neighbors-preface';plan=dict(baseline=BASELINE,created_utc=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),runtime_sha256=runtime_sha,fixtures_sha256=sha(a.fixtures),models_manifest_sha256=sha(a.models),arms=[arm],targets=fixtures['targets'],sampler=SAMPLER,template=PREFACE,protocol='neighbor-preface-current-line-lab-v1',limits=dict(seconds_per_line=60,seconds_per_target=600,shared_memory_bytes=14*1024**3,rss_bytes=3*1024**3),hypothesis='Plain natural-language context preface followed by exact official current-line template may avoid whole-JSON translation observed in first experiment.',scope='New preregistered arm, not a retest/overwrite of failed JSON arm. Same runtime/model/sampler/probe/fresh-process lifecycle. Not an official contextual template. No production cache.')
 with (a.evidence/'plan.json').open('x') as f:json.dump(plan,f,ensure_ascii=False,indent=2)
 rows=[]
 for target in fixtures['targets']:
  start=time.monotonic()
  for case in fixtures['cases']:
   root=a.evidence/arm/target/case['id'];root.mkdir(parents=True,exist_ok=False);pp=root/'prompt.txt';pp.write_text(make_prompt(case,target))
   if time.monotonic()-start>600:r=dict(completed=False,stop_reason='target-600s-unstarted',text='',seconds=None);write(root/'result.json',r)
   else:
    args=[str(a.runtime.resolve()),'-m',m['path'],'-f',str(pp.resolve()),'--offline','--perf','--no-escape','--jinja','--single-turn','--load-mode','none','--no-display-prompt','--simple-io','--no-context-shift','--reasoning','off']+SAMPLER
    r=probe(args,root,3*1024**3)
   rows.append(dict(arm=arm,target=target,id=case['id'],source_language=case['source_language'],source=case['text'],prompt_sha256=sha(pp),experimental_identity=identity(case,target,m['sha256'],runtime_sha),**r));write(a.evidence/'results.json',rows)
   print(arm,target,case['id'],r['completed'],round(r['seconds'] or 0,3),repr(r['text']),flush=True)
if __name__ == "__main__":
    raise SystemExit("【不准测试】实验与检查已停用；遵循最新用户指令。")

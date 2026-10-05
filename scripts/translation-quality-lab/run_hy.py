# 【不准测试】Latest user instruction: no tests, inference, benchmarks or validation. See NO_TESTING.md.
"""Cloud CPU experiments only. Preserves production sources, cache and sampling."""
import argparse, hashlib, json, os, platform, signal, subprocess, time
from pathlib import Path

BASELINE='64457419ae6fc8af15ab5542a402b7b871cbf9be'
PLAIN='将以下文本翻译为{0}，注意只需要输出翻译后的结果，不要额外解释：\n{1}'
CONTEXT='将下面 JSON 中的“当前句”翻译为{0}。前句和后句仅供理解指代、语气和词义，不能作为待翻译文本。只输出当前句的译文，不要输出邻句、标签或额外解释：\n{1}'
SAMPLER=['-t','4','-tb','4','-ngl','0','-c','4096','-n','2048','--seed','42','--temp','0.1','--top-k','20','--top-p','0.8','--min-p','0.05','--repeat-penalty','1.0','--frequency-penalty','0','--presence-penalty','0']
CGROUP=Path('/sys/fs/cgroup')
def sha(p):
 h=hashlib.sha256()
 with Path(p).open('rb') as f:
  for b in iter(lambda:f.read(8*1024**2),b''):h.update(b)
 return h.hexdigest()
def canonical(d):return json.dumps(d,ensure_ascii=False,sort_keys=True,separators=(',',':')).encode()
def make_prompt(case,target,context):
 name={'en':'英语','zh':'简体中文'}[target]
 if not context:return PLAIN.format(name,case['text'])
 return CONTEXT.format(name,json.dumps({'前句':case['previous'],'当前句':case['text'],'后句':case['next']},ensure_ascii=False))
def identity(case,target,context,model_hash,runtime_hash):
 # No production cache is read/written. Neighbor changes invalidate this experimental identity.
 return hashlib.sha256(canonical(dict(protocol='neighbor-current-line-lab-v1' if context else 'official-plain-per-line-v1',template=CONTEXT if context else PLAIN,sampler=SAMPLER,model=model_hash,runtime=runtime_hash,target=target,source=case['text'],previous=case['previous'] if context else None,next=case['next'] if context else None))).hexdigest()
def write(p,d):p.write_text(json.dumps(d,ensure_ascii=False,indent=2)+'\n')
def group_gone(pid):
 try:os.killpg(pid,0);return False
 except ProcessLookupError:return True

def probe(args,root,budget,timeout=60):
 env={k:v for k,v in os.environ.items() if not k.startswith(('LLAMA_','GGML_'))}
 env.update(OMP_NUM_THREADS='4',OMP_THREAD_LIMIT='4')
 record=dict(argv=args,environment_overrides={'OMP_NUM_THREADS':'4','OMP_THREAD_LIMIT':'4'},seconds_limit=timeout,rss_limit_bytes=budget,shared_limit_bytes=14*1024**3,poll_seconds=0.02)
 write(root/'invocation.json',record)
 start=time.monotonic();first=None;peak=0;cgpeak=0;stop=None
 with (root/'stdout.txt').open('xb') as out,(root/'stderr.txt').open('xb') as err:
  child=subprocess.Popen(args,stdin=subprocess.DEVNULL,stdout=out,stderr=err,env=env,start_new_session=True)
  usage=None
  while True:
   pid,status,usage=os.wait4(child.pid,os.WNOHANG)
   if pid:
    child.returncode=os.waitstatus_to_exitcode(status);break
   elapsed=time.monotonic()-start
   try:
    lines=Path(f'/proc/{child.pid}/status').read_text().splitlines()
    rss=next((int(x.split()[1])*1024 for x in lines if x.startswith('VmRSS:')),0);peak=max(peak,rss)
   except FileNotFoundError:rss=0
   current=int((CGROUP/'memory.current').read_text());cgpeak=max(cgpeak,current)
   if first is None and (root/'stdout.txt').stat().st_size:
    if (root/'stdout.txt').read_bytes().strip():first=elapsed
   if stop is None:
    if elapsed>timeout:stop='timeout'
    elif rss>budget:stop='process-rss-limit'
    elif current>14*1024**3:stop='shared-memory-limit'
    elif (root/'stdout.txt').stat().st_size>65536 or (root/'stderr.txt').stat().st_size>4*1024**2:stop='output-limit'
    if stop:os.killpg(child.pid,signal.SIGKILL)
   time.sleep(.02)
 if usage:peak=max(peak,int(usage.ru_maxrss)*1024)
 duration=time.monotonic()-start
 raw=(root/'stdout.txt').read_text(errors='replace');text=raw.strip()
 if text.endswith('[end of text]'):text=text[:-len('[end of text]')].rstrip()
 complete=child.returncode==0 and stop is None and bool(text) and '\ufffd' not in text
 cleanup=group_gone(child.pid)
 result=dict(exit_code=child.returncode,stop_reason=stop,seconds=duration,first_nonwhitespace_stdout_seconds=first,peak_rss_bytes=peak,peak_shared_cgroup_bytes=cgpeak,cleanup_confirmed=cleanup,completed=complete,text=text,stdout_sha256=sha(root/'stdout.txt'),stderr_sha256=sha(root/'stderr.txt'))
 write(root/'result.json',result)
 if not cleanup:raise RuntimeError('Unconfirmed cleanup; later inference forbidden')
 return result

def main():
 p=argparse.ArgumentParser();p.add_argument('--runtime',type=Path,required=True);p.add_argument('--models',type=Path,required=True);p.add_argument('--fixtures',type=Path,required=True);p.add_argument('--evidence',type=Path,required=True);p.add_argument('--arms',nargs='+',default=['hy18-q8-plain','hy18-q4-plain','hy18-q8-neighbors','hy7-q8-plain']);a=p.parse_args()
 a.evidence.mkdir(parents=True,exist_ok=True);fixtures=json.loads(a.fixtures.read_text());models={m['id']:m for m in json.loads(a.models.read_text())}
 runtime_sha=sha(a.runtime)
 runtime_files=[dict(path=str(x),bytes=x.stat().st_size,sha256=sha(x)) for x in sorted(a.runtime.parent.glob('*.so*')) if x.is_file()]
 plan=dict(baseline=BASELINE,created_utc=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),runtime=str(a.runtime.resolve()),runtime_sha256=runtime_sha,runtime_libraries=runtime_files,source_revision='7fe450e19305b828c199d602c23a8337aaa1f03b',fixtures_sha256=sha(a.fixtures),models_manifest_sha256=sha(a.models),arms=a.arms,targets=fixtures['targets'],sampler=SAMPLER,plain_template=PLAIN,context_template=CONTEXT,preregistered_order='each arm: en then zh, original song order; independent fresh process each line',limits=dict(seconds_per_line=60,seconds_per_target=600,shared_memory_bytes=14*1024**3,rss_small_bytes=3*1024**3,rss_large_bytes=12*1024**3),cache='No result cache. Experimental identity only; no production admission/coordinator/native worker tested.',measurement='Wall includes process creation/model load; first stdout is poll-observed, not a token timestamp. No OS cold-cache guarantee. Linux CPU only.',machine=dict(platform=platform.platform(),cpu_quota=(CGROUP/'cpu.max').read_text().strip(),memory_max=(CGROUP/'memory.max').read_text().strip(),memory_events_before=(CGROUP/'memory.events').read_text()))
 with (a.evidence/'plan.json').open('x') as f:json.dump(plan,f,ensure_ascii=False,indent=2)
 rows=[]
 for arm in a.arms:
  context=arm.endswith('neighbors');model_id=arm.replace('-plain','').replace('-neighbors','');m=models[model_id]
  assert Path(m['path']).stat().st_size==m['bytes'] and sha(m['path'])==m['sha256']
  with open(m['path'],'rb') as f:os.posix_fadvise(f.fileno(),0,0,os.POSIX_FADV_DONTNEED)
  for target in fixtures['targets']:
   target_start=time.monotonic();target_stopped=False
   for case in fixtures['cases']:
    root=a.evidence/arm/target/case['id'];root.mkdir(parents=True,exist_ok=False)
    prompt=make_prompt(case,target,context);pp=root/'prompt.txt';pp.write_text(prompt)
    key=identity(case,target,context,m['sha256'],runtime_sha)
    if target_stopped or time.monotonic()-target_start>600:
     r=dict(completed=False,stop_reason='target-600s-unstarted',text='',seconds=None);write(root/'result.json',r)
    else:
     args=[str(a.runtime.resolve()),'-m',m['path'],'-f',str(pp.resolve()),'--offline','--perf','--no-escape','--jinja','--single-turn','--load-mode','none','--no-display-prompt','--simple-io','--no-context-shift','--reasoning','off']+SAMPLER
     r=probe(args,root,(12 if model_id=='hy7-q8' else 3)*1024**3)
     if r['stop_reason']=='shared-memory-limit':target_stopped=True
    rows.append(dict(arm=arm,target=target,id=case['id'],source_language=case['source_language'],source=case['text'],prompt_sha256=sha(pp),experimental_identity=key,**r))
    write(a.evidence/'results.json',rows)
    print(arm,target,case['id'],r['completed'],round(r['seconds'] or 0,3),repr(r['text']),flush=True)
 write(a.evidence/'memory-events-after.json',dict(memory_events=(CGROUP/'memory.events').read_text()))
if __name__ == "__main__":
    raise SystemExit("【不准测试】实验与检查已停用；遵循最新用户指令。")

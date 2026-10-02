"""Bounded CPU semantic screen, not Windows production acceptance or automatic release approval."""
import argparse, hashlib, json, os, pathlib, shutil, signal, subprocess, time, urllib.request

GIB = 1024 ** 3
MODELS = {
 '1.8b': ('https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/a0c709d9fac510f2c807aa3af52872340dc37a4a/Hy-MT2-1.8B-Q8_0.gguf',1908528192,'5c3fe0b1408a5ceb0143184ef247b11b579c525f4b02b060e6c851bb76fef1a4',4,3),
 '7b': ('https://huggingface.co/tencent/Hy-MT2-7B-GGUF/resolve/ab8472660ac61fac25f1af43fac2599d52a8a775/HY-MT2-7B-Q8_0.gguf',7981928896,'58b3ad55dd6f6fa08c695cddc34fb5f8f708a844f78ae10508071914b0ed67c0',13,12)
}
PROFILES = ['baseline','context','context_style','context_style_official_sampler']

def digest(path):
 h=hashlib.sha256()
 with open(path,'rb') as f:
  for block in iter(lambda:f.read(8*1024*1024),b''): h.update(block)
 return h.hexdigest()

def available():
 return int(next(line.split()[1] for line in pathlib.Path('/proc/meminfo').read_text().splitlines() if line.startswith('MemAvailable:'))) * 1024

def prompt(case,profile):
 target='简体中文' if case['target']=='zh-CN' else '英语'
 base=f"将以下文本翻译为{target}，注意只需要输出翻译后的结果，不要额外解释：\n{case['source']}"
 if profile=='baseline': return base
 text=f"〖背景信息〗\n{case['context']}\n请结合背景信息将以下文本翻译为{target}。只翻译待翻译文本，不翻译背景，不要额外解释。\n"
 if 'style' in profile:
  text+='译文用于歌词：忠实保留原意、否定、人物关系和意象，措辞自然凝练，保留原文情绪与节奏。不要为了押韵添加情节或改变含义。\n'
 return text+'〖待翻译文本〗\n'+case['source']

def download(model,path):
 url,size,sha,_,_=MODELS[model]
 if path.exists():
  if path.stat().st_size==size and digest(path)==sha: return
  raise RuntimeError('Existing file does not match pinned model; no overwrite')
 if shutil.disk_usage(path.parent).free < size+2*GIB: raise RuntimeError('Insufficient disk for pinned model plus 2 GiB headroom')
 part=path.with_suffix('.part')
 if part.exists(): raise RuntimeError('Existing partial download retained; no blind restart')
 h=hashlib.sha256(); total=0; started=time.monotonic()
 with urllib.request.urlopen(url,timeout=60) as source,open(part,'xb') as dest:
  while True:
   block=source.read(4*1024*1024)
   if not block: break
   total+=len(block)
   if total>size or time.monotonic()-started>600: raise RuntimeError('Download bound exceeded')
   h.update(block);dest.write(block)
 if total!=size or h.hexdigest()!=sha: raise RuntimeError('Pinned size/hash mismatch')
 part.rename(path)

def infer(exe,model_path,text,profile,out,stem,rss_limit):
 prompt_path=out/(stem+'.prompt.txt'); prompt_path.write_text(text,encoding='utf-8')
 stdout=out/(stem+'.stdout.txt');stderr=out/(stem+'.stderr.txt')
 args=[str(exe),'-m',str(model_path),'-f',str(prompt_path),'--offline','--perf','--no-escape','--jinja','--single-turn','--load-mode','none','--no-display-prompt','--simple-io','--no-context-shift','--reasoning','off','-t','4','-tb','4','-ngl','0','-c','4096','-n','2048','--seed','42','--temp','0.7' if profile.endswith('official_sampler') else '0.1','--top-k','20','--top-p','0.6' if profile.endswith('official_sampler') else '0.8','--min-p','0.05','--repeat-penalty','1.05' if profile.endswith('official_sampler') else '1.0','--frequency-penalty','0','--presence-penalty','0']
 started=time.monotonic();peak=0;status='completed';first=None
 with open(stdout,'xb') as so,open(stderr,'xb') as se:
  child=subprocess.Popen(args,stdin=subprocess.DEVNULL,stdout=so,stderr=se,start_new_session=True)
  try:
   while child.poll() is None:
    try:
     info=pathlib.Path(f'/proc/{child.pid}/status').read_text()
     rss=int(next((line.split()[1] for line in info.splitlines() if line.startswith('VmRSS:')),'0'))*1024
     peak=max(peak,rss)
    except (FileNotFoundError,ProcessLookupError): pass
    if stdout.stat().st_size and first is None: first=time.monotonic()-started
    if peak>rss_limit: status='memory_stop';break
    if stdout.stat().st_size>65536 or stderr.stat().st_size>2*1024*1024: status='output_bound_stop';break
    if time.monotonic()-started>60: status='timeout';break
    time.sleep(.05)
  finally:
   if child.poll() is None:
    os.killpg(child.pid,signal.SIGTERM)
    try: child.wait(timeout=5)
    except subprocess.TimeoutExpired:
     os.killpg(child.pid,signal.SIGKILL);child.wait(timeout=5)
  code=child.wait(timeout=5)
 raw=stdout.read_text(encoding='utf-8',errors='replace')
 return dict(status=status if code==0 or status!='completed' else 'process_failure',exitCode=code,seconds=time.monotonic()-started,firstOutputSeconds=first,peakRssBytes=peak,promptSha256=hashlib.sha256(text.encode()).hexdigest(),arguments=args,rawOutput=raw,stderrFile=stderr.name)

def main():
 p=argparse.ArgumentParser();p.add_argument('--model',choices=MODELS);p.add_argument('--exe');p.add_argument('--output');p.add_argument('--self-test',action='store_true');a=p.parse_args()
 cases=json.loads(pathlib.Path(__file__).with_name('cases.json').read_text())
 if a.self_test:
  assert len(cases)==8 and len({c['id'] for c in cases})==8
  for c in cases:
   for profile in PROFILES:
    s=prompt(c,profile);assert c['source'] in s and c['review'] not in s and len(s.encode())<=1800
  assert prompt(cases[0],'baseline').startswith('将以下文本翻译为简体中文')
  print('32 prompt contracts passed; no model loaded');return
 if not a.model or not a.exe or not a.output: p.error('model, exe and output are required')
 out=pathlib.Path(a.output).resolve();out.mkdir(parents=True,exist_ok=False)
 model_path=out.parent/(a.model+'.gguf');exe=pathlib.Path(a.exe).resolve()
 records=[];spec=MODELS[a.model];start=time.monotonic()
 manifest=dict(model=a.model,expectedBytes=spec[1],expectedSha256=spec[2],runtimeCommit='7fe450e19305b828c199d602c23a8337aaa1f03b',sourceSha=os.getenv('GITHUB_SHA'),casesSha256=digest(pathlib.Path(__file__).with_name('cases.json')),platform='Linux CPU diagnostic, not Windows production validation',semanticVerdict='PENDING HUMAN REVIEW',releaseApproved=False,plannedCalls=32,profiles=PROFILES)
 try:
  if available()<spec[3]*GIB: raise RuntimeError('Memory admission unavailable before download')
  download(a.model,model_path);manifest['verifiedModelSha256']=digest(model_path);manifest['runtimeExecutableSha256']=digest(exe)
  stop=False
  for case in cases:
   for profile in PROFILES:
    rec=dict(case=case['id'],profile=profile,source=case['source'],target=case['target'],review=case['review'])
    if stop or time.monotonic()-start>2100 or available()<spec[3]*GIB:
     rec['status']='not_started_safety_or_total_budget';stop=True
    else:
     rec.update(infer(exe,model_path,prompt(case,profile),profile,out,case['id']+'-'+profile,spec[4]*GIB))
     if rec['status']!='completed': stop=True
    records.append(rec)
    (out/'results.json').write_text(json.dumps(records,ensure_ascii=False,indent=2))
  manifest['technicalComplete']=len(records)==32 and all(r['status']=='completed' for r in records)
 except Exception as e:
  manifest['error']=str(e);manifest['technicalComplete']=False
 finally:
  manifest['completedCalls']=sum(r.get('status')=='completed' for r in records)
  (out/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2))
 if not manifest['technicalComplete']: raise SystemExit(1)
if __name__=='__main__':main()

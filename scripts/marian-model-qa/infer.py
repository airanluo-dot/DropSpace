"""QA-only offline process; fixture language tags are supplied, never inferred."""
import argparse, hashlib, json, os, socket, threading, time

def deny_network(*args, **kwargs):
    raise RuntimeError("QA inference is offline; network connections are forbidden")
socket.socket.connect = deny_network
socket.create_connection = deny_network
from pathlib import Path
os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', HF_HUB_DISABLE_TELEMETRY='1', USE_TORCH='0', USE_TF='0', OMP_NUM_THREADS='4', MKL_NUM_THREADS='4')

def sha(path):
    h=hashlib.sha256()
    with path.open('rb') as f:
        for b in iter(lambda:f.read(1048576),b''): h.update(b)
    return h.hexdigest()

def main():
    p=argparse.ArgumentParser();p.add_argument('--request',required=True);a=p.parse_args()
    req=json.loads(Path(a.request).read_text(encoding='utf-8-sig'))
    evidence=Path(req['evidence']);evidence.mkdir(parents=True,exist_ok=True)
    def write(name,data):
        (evidence/name).write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
    def expire(label):
        write('deadline.json',{'reason':label,'seconds':time.monotonic()-started});os._exit(124)
    started=time.monotonic();total=threading.Timer(180,expire,args=('target-180s',));total.daemon=True;total.start()
    manifest=json.loads(Path(req['manifest']).read_text(encoding='utf-8-sig'))
    cache={};calls=0
    def hop(direction,lines,phase):
        nonlocal calls
        calls+=1;clock=time.monotonic();timer=threading.Timer(60,expire,args=(direction+'-hop-60s',));timer.daemon=True;timer.start()
        try:
            import ctranslate2
            from transformers import MarianTokenizer
            if direction not in cache:
                entry=manifest['models'][direction]
                for root,items in [(Path(entry['converted']),entry['files']),(Path(entry['source']),entry['tokenizerFiles'])]:
                    for item in items:
                        path=root/item['path']
                        if path.stat().st_size!=item['bytes'] or sha(path)!=item['sha256']: raise ValueError('Artifact hash mismatch')
                tokenizer=MarianTokenizer.from_pretrained(entry['source'],local_files_only=True)
                if direction=='en-zh' and tokenizer.convert_tokens_to_ids('>>cmn_Hans<<')!=5: raise ValueError('Target prefix mismatch')
                translator=ctranslate2.Translator(entry['converted'],device='cpu',compute_type='int8',inter_threads=1,intra_threads=4)
                cache[direction]=(tokenizer,translator)
            tokenizer,translator=cache[direction]
            encoded=[]
            for line in lines:
                ids=tokenizer.encode(('>>cmn_Hans<< ' if direction=='en-zh' else '')+line['text'],truncation=False)
                if not ids or len(ids)>512: raise ValueError('Input token limit; no truncation')
                encoded.append(tokenizer.convert_ids_to_tokens(ids))
            write(phase+'-'+direction+'.inputs.json',{'lines':lines,'tokens':encoded})
            pending=translator.translate_batch(encoded,beam_size=4,max_batch_size=12,max_input_length=0,max_decoding_length=511,return_end_token=True,sampling_topk=1,length_penalty=1,repetition_penalty=1,no_repeat_ngram_size=0,asynchronous=True)
            write('inference-started.json',{'direction':direction,'phase':phase,'state':'CT2 asynchronous decode dispatched'})
            outputs=[item.result() for item in pending]
            write(phase+'-'+direction+'.raw.json',[{'id':line['id'],'hypothesisTokens':out.hypotheses} for line,out in zip(lines,outputs,strict=True)])
            result=[];raw=[]
            for line,out in zip(lines,outputs,strict=True):
                tokens=out.hypotheses[0]
                if not tokens or tokens[-1]!=tokenizer.eos_token: raise ValueError('Decode reached limit without EOS; reject partial output')
                text=tokenizer.decode(tokenizer.convert_tokens_to_ids(tokens),skip_special_tokens=True,use_source_tokenizer=False,clean_up_tokenization_spaces=False)
                if not text.strip(): raise ValueError('Empty output')
                result.append({'id':line['id'],'text':text})
                raw.append({'id':line['id'],'tokens':tokens,'text':text})
            write(phase+'-'+direction+'.outputs.json',{'outputs':raw,'seconds':time.monotonic()-clock})
            return result
        finally: timer.cancel()
    all_results={}
    for phase in ['cold','warm']:
        clock=time.monotonic();target=req['target'];out={};pivot=[]
        for lang in ['en','zh','ja','ko']:
            lines=[{'id':x['id'],'text':x['text']} for x in req['lines'] if x['language']==lang]
            if not lines: continue
            if lang==target: out.update({x['id']:x['text'] for x in lines});continue
            if lang=='en': pivot.extend(lines)
            else:
                translated=hop(lang+'-en',lines,phase)
                if target=='en': out.update({x['id']:x['text'] for x in translated})
                else: pivot.extend(translated)
        if pivot:
            if target!='zh': raise ValueError('Unexpected route')
            translated=hop('en-zh',sorted(pivot,key=lambda x:x['id']),phase)
            out.update({x['id']:x['text'] for x in translated})
        result=[{'id':x['id'],'text':out[x['id']]} for x in req['lines']]
        if len(out)!=len(req['lines']): raise ValueError('ID mismatch')
        all_results[phase]=result
        write(phase+'.json',{'lines':result,'seconds':time.monotonic()-clock,'loadedModels':list(cache),'modelCallsCumulative':calls})
    total.cancel()
    print(json.dumps({'outputs':all_results,'modelCalls':calls,'seconds':time.monotonic()-started},ensure_ascii=False),flush=True)

if __name__=='__main__':main()

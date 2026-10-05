# 【不准测试】Latest user instruction: no tests, inference, benchmarks or validation. See NO_TESTING.md.
"""Check evidence completeness/identity after execution; never performs inference."""
import argparse,hashlib,json
from pathlib import Path
from run_hy import make_prompt,identity

def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def main():
 p=argparse.ArgumentParser();p.add_argument('--evidence',type=Path,required=True);p.add_argument('--fixtures',type=Path,required=True);a=p.parse_args()
 fixture=json.loads(a.fixtures.read_text());fmap={c['id']:c for c in fixture['cases']}
 hy=a.evidence/'hy';plan=json.loads((hy/'plan.json').read_text());rows=json.loads((hy/'results.json').read_text());models={m['id']:m for m in json.loads((a.evidence/'provenance/models.json').read_text())};reviews=json.loads((hy/'review.json').read_text())
 assert sha(a.fixtures)==plan['fixtures_sha256'];assert sha(a.evidence/'provenance/models.json')==plan['models_manifest_sha256']
 expected={(arm,target,c['id']) for arm in plan['arms'] for target in fixture['targets'] for c in fixture['cases']};observed={(r['arm'],r['target'],r['id']) for r in rows};assert len(rows)==len(expected) and observed==expected
 assert {(r['arm'],r['target'],r['id']) for r in reviews['cases']}==expected and len(reviews['cases'])==len(expected)
 judgment={(r['arm'],r['target'],r['id']):r for r in reviews['cases']}
 hashes=0;started=0;completed=0
 for r in rows:
  root=hy/r['arm']/r['target']/r['id'];case=fmap[r['id']];context=r['arm'].endswith('neighbors');model_id=r['arm'].replace('-plain','').replace('-neighbors','');m=models[model_id]
  assert (root/'prompt.txt').read_text()==make_prompt(case,r['target'],context);assert sha(root/'prompt.txt')==r['prompt_sha256'];hashes+=1
  assert r['experimental_identity']==identity(case,r['target'],context,m['sha256'],plan['runtime_sha256'])
  assert json.loads((root/'result.json').read_text())['text']==r['text']
  assert judgment[(r['arm'],r['target'],r['id'])]['output_sha256']==hashlib.sha256(r['text'].encode()).hexdigest()
  if (root/'invocation.json').exists():
   started+=1;assert sha(root/'stdout.txt')==r['stdout_sha256'] and sha(root/'stderr.txt')==r['stderr_sha256'];hashes+=2
   raw=(root/'stdout.txt').read_text().strip();normalized=raw[:-len('[end of text]')].rstrip() if raw.endswith('[end of text]') else raw
   assert normalized==r['text'];assert r['cleanup_confirmed']
   inv=json.loads((root/'invocation.json').read_text());assert inv['argv'][-len(plan['sampler']):]==plan['sampler'];assert inv['argv'][inv['argv'].index('-m')+1]==m['path']
  if r['completed']:completed+=1;assert r['exit_code']==0 and r['stop_reason'] is None and r['text']
 control=json.loads((a.evidence/'provenance/gguf-metadata.json').read_text())['quantization_control'];assert all(control.values())
 ct=json.loads((a.evidence/'ct2-validation.json').read_text());assert all(all(v is True for k,v in target.items() if k not in ['output_count','actual_translated_lines']) for target in ct.values())
 result=dict(expected_rows=len(expected),started_rows=started,completed_rows=completed,prompt_raw_stream_hashes_checked=hashes,all_cleanup_confirmed=True,all_review_output_hashes_bound=True,quantization_control=control,ct2_validation_rechecked=True,production_changes_checked_separately_by_git=True)
 (a.evidence/'validation.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result,indent=2))
if __name__ == "__main__":
    raise SystemExit("【不准测试】实验与检查已停用；遵循最新用户指令。")

# 【不准测试】Latest user instruction: no tests, inference, benchmarks or validation. See NO_TESTING.md.
"""Summarize measured rows; semantic labels are explicit reviewer judgments."""
import argparse,json,statistics
from pathlib import Path

def main():
 p=argparse.ArgumentParser();p.add_argument('--evidence',type=Path,required=True);a=p.parse_args()
 rows=json.loads((a.evidence/'hy/results.json').read_text());review=json.loads((a.evidence/'hy/review.json').read_text())
 judgments={(r['arm'],r['target'],r['id']):r for r in review['cases']}
 assert len(judgments)==len(rows) and set(judgments)=={(r['arm'],r['target'],r['id']) for r in rows}
 stats=[]
 for arm in dict.fromkeys(r['arm'] for r in rows):
  for target in ['en','zh']:
   rs=[r for r in rows if r['arm']==arm and r['target']==target];done=[r for r in rs if r['completed']]
   js=[judgments[(r['arm'],r['target'],r['id'])] for r in rs]
   foreign=[r for r in rs if r['source_language']!=target]
   foreign_js=[judgments[(r['arm'],r['target'],r['id'])] for r in foreign]
   stats.append(dict(arm=arm,target=target,planned=len(rs),completed=len(done),first_foreign_id=foreign[0]['id'],first_foreign_line_seconds=foreign[0]['seconds'],foreign_line_wall_sum_seconds=sum(r['seconds'] or 0 for r in foreign),foreign_semantic_labels={s:sum(j['label']==s for j in foreign_js) for s in ['acceptable','warning','blocker','uncertain','not-executed']},first_line_seconds=rs[0]['seconds'],first_line_first_stdout_seconds=rs[0].get('first_nonwhitespace_stdout_seconds'),sum_line_wall_seconds=sum(r['seconds'] or 0 for r in rs),median_completed_line_seconds=statistics.median(r['seconds'] for r in done) if done else None,peak_process_rss_bytes=max((r.get('peak_rss_bytes',0) for r in rs),default=0),peak_shared_cgroup_bytes=max((r.get('peak_shared_cgroup_bytes',0) for r in rs),default=0),semantic_labels={s:sum(j['label']==s for j in js) for s in ['acceptable','warning','blocker','uncertain','not-executed']},foreign_exact_copies=[r['id'] for r in rs if r['source_language']!=target and r['source'].strip()==r['text'].strip()],same_language_controls=[r['id'] for r in rs if r['source_language']==target]))
 (a.evidence/'summary.json').write_text(json.dumps(stats,ensure_ascii=False,indent=2)+'\n')
 print('| Arm | Target | Complete | First line s | First stdout s | 16-line sum s | Peak RSS MiB | A/W/B/U |')
 print('|---|---|---:|---:|---:|---:|---:|---|')
 for s in stats:
  number=lambda x:f'{x:.3f}' if x is not None else '—'
  counts='/'.join(str(s['semantic_labels'][x]) for x in ['acceptable','warning','blocker','uncertain'])
  print(f"| {s['arm']} | {s['target']} | {s['completed']}/{s['planned']} | {number(s['first_line_seconds'])} | {number(s['first_line_first_stdout_seconds'])} | {s['sum_line_wall_seconds']:.3f} | {s['peak_process_rss_bytes']/1024**2:.1f} | {counts} |")
if __name__ == "__main__":
    raise SystemExit("【不准测试】实验与检查已停用；遵循最新用户指令。")

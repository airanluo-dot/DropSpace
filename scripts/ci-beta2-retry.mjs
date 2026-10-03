// Reuse only independently successful checks whose source inputs are unchanged.
// This never creates a GitHub check status and never reuses a failed job result.
import fs from 'node:fs';
import {execFileSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import path from 'node:path';
export const priorHead = 'dfe2b62a0d1cec44d2577854d03f3df994bd8062';
export const priorRun = 37142799256;
const permitted = new Set(['.github/workflows/ci.yml','scripts/ci-beta2-retry.mjs','scripts/ci-beta2-retry.test.mjs','scripts/plain-hy-production-evidence/ContractTests.cs','scripts/ai-model-qa/release-approval.json','scripts/ai-model-qa/evidence/v0.3.1-beta.2-owner-accepted-review.json']);
export const reusableSteps = {
 brand: 'Verify committed brand assets on clean checkout',
 localization: 'Enforce localization resource policy',
 localizationTests: 'Test frozen model prompt localization exceptions',
 compatibility: 'Enforce Windows 10/11 compatibility baseline',
 hardcoding: 'Enforce hardcoding governance',
 secret: 'Enforce secret hygiene',
 island: 'Test expanded island scope and fullscreen behavior contracts',
 helper: 'Test private translation helper protocol without model dependencies',
 core: 'Test core policies, SemVer, and channel selection',
 infrastructure: 'Test persistence, manifests, streaming downloads, and update coordination'
};
export function validateReuse({run,jobs,changed,version}) {
 if(version !== 'v0.3.1-beta.2' || !changed.length || changed.some(p=>!permitted.has(p))) return false;
 if(run.id!==priorRun || run.head_sha!==priorHead || run.event!=='pull_request' || run.status!=='completed' || run.repository?.full_name!=='airanluo-dot/DropSpace') return false;
 const job=jobs.find(j=>j.name==='Build and validate shared Windows artifacts' && j.run_id===priorRun && j.status==='completed');
 return !!job && Object.values(reusableSteps).every(name=>job.steps?.some(s=>s.name===name && s.conclusion==='success'));
}
if (process.argv[1] && path.resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
 let reuse=false;
 try {
  const token=process.env.GH_TOKEN;
  if(!token || process.env.GITHUB_REPOSITORY!=='airanluo-dot/DropSpace') throw Error('Repository/token unavailable');
  execFileSync('git',['fetch','--no-tags','origin',priorHead],{stdio:'pipe'});
  const changed=execFileSync('git',['diff','--name-only',priorHead,'HEAD'],{encoding:'utf8'}).trim().split('\n').filter(Boolean);
  const get=async suffix=>{const r=await fetch(`https://api.github.com/repos/airanluo-dot/DropSpace/actions/${suffix}`,{headers:{Authorization:`Bearer ${token}`,Accept:'application/vnd.github+json'}});if(!r.ok)throw Error(`GitHub metadata HTTP ${r.status}`);return r.json();};
  const run=await get(`runs/${priorRun}`);const jobs=await get(`runs/${priorRun}/jobs?per_page=100`);
  reuse=validateReuse({run,jobs:jobs.jobs,changed,version:fs.readFileSync('RELEASE_VERSION','utf8').trim()});
  console.log(JSON.stringify({reuse,priorHead,priorRun,changed,reusedChecks:reuse?Object.values(reusableSteps):[]}));
 } catch(e) { console.log(`No prior-check reuse: ${e.message}`); }
 if(process.env.GITHUB_OUTPUT)fs.appendFileSync(process.env.GITHUB_OUTPUT,`reuse=${reuse}\n`);
}

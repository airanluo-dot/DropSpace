// New Beta releases compile the PR, then package the exact merged main.
// PR builds perform no functional cases. Package production counts one installer
// payload scenario; other focused checks are budgeted separately.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {execFileSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {fileIdentity} from './ai-runtime-publication.mjs';

const repository = 'airanluo-dot/DropSpace';
const kind = 'next-beta-windows-pr-build';
const git = (...args) => execFileSync('git',args,{encoding:'utf8'}).trim();
const read = filename => JSON.parse(fs.readFileSync(filename,'utf8').replace(/^\uFEFF/,''));
const identity = filename => {const {bytes,sha256}=fileIdentity(filename);return {bytes,sha256};};

// Preserve immutable historical routes. New release lines also use this route,
// including beta.1 of a later semantic version; no next Beta number is hardcoded.
export function isNextBetaRelease(tag) {
  const match = /^v(\d+)\.(\d+)\.(\d+)-beta\.(\d+)$/.exec(tag);
  if (!match) return false;
  const numbers=match.slice(1).map(Number);
  if (numbers.some(n=>!Number.isSafeInteger(n)) || numbers[3]<1) return false;
  for (const [index,base] of [0,3,1].entries()) {
    if (numbers[index]!==base) return numbers[index]>base;
  }
  return numbers[3]>18;
}

export function verifyValidation(directory,receipt,expected) {
  assert.equal(receipt.schemaVersion,1);
  assert.equal(receipt.kind,kind);
  for(const key of ['repository','runId','runAttempt','sourceTree','releaseVersion'])
    assert.equal(receipt[key],expected[key],`Validation ${key} mismatch`);
  assert.ok(isNextBetaRelease(receipt.releaseVersion),'A new Beta release is required');
  assert.match(receipt.sourceCommit??'',/^[a-f0-9]{40}$/);
  assert.equal(receipt.appXamlBuild,true,'Complete Windows App/XAML build required');
  assert.deepEqual(receipt.tests,{status:'not-run',executed:0,reason:'PR compilation only; focused functional checks use the project budget'});
  assert.deepEqual(Object.keys(receipt.files),['app-build.txt']);
  assert.deepEqual(receipt.files['app-build.txt'],identity(path.join(directory,'app-build.txt')),'Actual build evidence changed');
  return receipt;
}

async function api(route) {
  const response=await fetch(`https://api.github.com/repos/${repository}/${route}`,{
    headers:{Authorization:`Bearer ${process.env.GH_TOKEN}`,Accept:'application/vnd.github+json','X-GitHub-Api-Version':'2022-11-28'}});
  assert.ok(response.ok,`GitHub metadata failed (${response.status})`);
  return response.json();
}
function producer(run) {
  assert.equal(run.repository?.full_name,repository);
  assert.equal(run.head_repository?.full_name,repository);
  assert.equal(run.path,'.github/workflows/ci.yml');
  assert.equal(run.event,'pull_request');
  assert.equal(run.status,'completed');
  assert.equal(run.conclusion,'success');
  assert.ok(Number.isSafeInteger(run.id)&&run.id>0&&Number.isSafeInteger(run.run_attempt)&&run.run_attempt>0);
}
async function main() {
  const [command,directory]=process.argv.slice(2);
  const releaseVersion=fs.readFileSync('RELEASE_VERSION','utf8').trim();
  assert.ok(isNextBetaRelease(releaseVersion),'New Beta source is required');
  assert.equal(process.env.GITHUB_REPOSITORY,repository);
  assert.equal(git('rev-parse','HEAD'),process.env.GITHUB_SHA,'Actual checkout mismatch');
  if(command==='record') {
    assert.equal(process.env.GITHUB_EVENT_NAME,'pull_request');
    const receipt={schemaVersion:1,kind,repository,
      runId:Number(process.env.GITHUB_RUN_ID),runAttempt:Number(process.env.GITHUB_RUN_ATTEMPT),
      sourceCommit:git('rev-parse','HEAD'),sourceTree:git('rev-parse','HEAD^{tree}'),releaseVersion,
      appXamlBuild:true,
      tests:{status:'not-run',executed:0,reason:'PR compilation only; focused functional checks use the project budget'},
      files:{'app-build.txt':identity(path.join(directory,'app-build.txt'))}};
    verifyValidation(directory,receipt,receipt);
    fs.writeFileSync(path.join(directory,'validation-receipt.json'),JSON.stringify(receipt,null,2)+'\n');
  } else if(command==='find') {
    assert.equal(process.env.GITHUB_REF,'refs/heads/main');
    const sourceTree=git('rev-parse','HEAD^{tree}');
    const runs=await api('actions/workflows/ci.yml/runs?status=success&event=pull_request&per_page=30');
    for(const candidate of runs.workflow_runs??[]) {
      const run=await api(`actions/runs/${candidate.id}`);producer(run);
      const associated=run.pull_requests?.length?run.pull_requests:await api(`commits/${run.head_sha}/pulls`);
      let merged=false;
      for(const pr of associated) {
        const current=await api(`pulls/${pr.number}`);
        if(current.merged&&current.merge_commit_sha===process.env.GITHUB_SHA){merged=true;break;}
      }
      if(!merged)continue;
      const artifacts=await api(`actions/runs/${run.id}/artifacts?per_page=100`);
      const artifact=artifacts.artifacts?.find(a=>a.name===`dropspace-next-beta-validation-${run.id}-${run.run_attempt}`&&!a.expired);
      if(!artifact)continue;
      fs.mkdirSync('artifacts',{recursive:true});
      fs.writeFileSync('artifacts/next-beta-validation-expected.json',JSON.stringify({repository,runId:run.id,
        runAttempt:run.run_attempt,sourceTree,releaseVersion,artifactId:artifact.id}));
      fs.appendFileSync(process.env.GITHUB_OUTPUT,`run_id=${run.id}\nartifact_id=${artifact.id}\n`);return;
    }
    throw new Error('No successful merged PR App/XAML build for exact main; no broad-test fallback');
  } else if(command==='verify') {
    const expected=read('artifacts/next-beta-validation-expected.json');
    assert.equal(expected.releaseVersion,releaseVersion);
    assert.equal(expected.sourceTree,git('rev-parse','HEAD^{tree}'));
    const run=await api(`actions/runs/${expected.runId}`);producer(run);
    assert.equal(run.run_attempt,expected.runAttempt);
    const receipt=read(path.join(directory,'validation-receipt.json'));
    const commit=await api(`git/commits/${receipt.sourceCommit}`);
    assert.equal(commit.tree.sha,expected.sourceTree,'Built PR tree differs from final main');
    assert.ok(receipt.sourceCommit===run.head_sha||commit.parents?.some(parent=>parent.sha===run.head_sha),'Build is not bound to producer head');
    verifyValidation(directory,receipt,expected);
    if(process.env.GITHUB_STEP_SUMMARY)fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY,
      `Reused full Windows App/XAML PR build ${run.id}/${run.run_attempt}, checkout ${receipt.sourceCommit}, identical tree ${receipt.sourceTree}. PR functional cases executed: 0. The final installer payload scenario is counted separately.\n`);
  } else throw new Error('Expected record, find or verify');
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))main().catch(error=>{console.error(error.message);process.exitCode=1;});

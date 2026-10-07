// Beta18 compiles each PR without tests, then packages the exact reviewed main.
// Final-main focused tests are the only functional test execution in this route.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {execFileSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {fileIdentity} from './ai-runtime-publication.mjs';

export const releaseVersion = 'v0.3.1-beta.18';
// The final source review sets exact case names and the pre-existing-suite
// denominator before any functional cases execute. No full-suite fallback.
export const focusedFilter = 'FullyQualifiedName~IslandContentPriorityTests|FullyQualifiedName~IslandAppearanceSettingsTests';
export const focusedCaseCount = 8;
export const originalSuiteCaseCount = 2124;
export const focusedProject = 'tests/DropSpace.Core.Tests/DropSpace.Core.Tests.csproj';
const repository = 'airanluo-dot/DropSpace';
const kind = 'beta18-windows-pr-build';
const git = (...args) => execFileSync('git',args,{encoding:'utf8'}).trim();
const read = filename => JSON.parse(fs.readFileSync(filename,'utf8').replace(/^\uFEFF/,''));
const identity = filename => {const {bytes,sha256}=fileIdentity(filename);return {bytes,sha256};};

export function verifyFocusedResults(xml) {
  assert.ok(focusedFilter&&Number.isSafeInteger(focusedCaseCount)&&focusedCaseCount>0,'Exact focused cases must be configured before production');
  assert.ok(Number.isSafeInteger(originalSuiteCaseCount)&&focusedCaseCount<=Math.floor(originalSuiteCaseCount/100),'Focused cases exceed the existing-suite one-percent budget');
  const summaries=xml.match(/<Counters\b[^>]*\/>/g);
  assert.equal(summaries?.length,1,'One real TRX summary required');
  const counts=Object.fromEntries([...summaries[0].matchAll(/(\w+)="(\d+)"/g)].map(([,key,value])=>[key,Number(value)]));
  assert.equal(counts.total,focusedCaseCount,'Actual execution count differs from reviewed focused case count');
  assert.equal(counts.executed,focusedCaseCount);
  assert.equal(counts.passed,focusedCaseCount,'Focused failures block publication');
  assert.equal(counts.failed,0);
  assert.equal(counts.notExecuted,0,'Skipped cases cannot qualify final packages');
  return {total:focusedCaseCount,passed:focusedCaseCount};
}

export function verifyValidation(directory,receipt,expected) {
  assert.equal(receipt.schemaVersion,1);
  assert.equal(receipt.kind,kind);
  for(const key of ['repository','runId','runAttempt','sourceTree','releaseVersion'])
    assert.equal(receipt[key],expected[key],`Validation ${key} mismatch`);
  assert.match(receipt.sourceCommit??'',/^[a-f0-9]{40}$/);
  assert.equal(receipt.appXamlBuild,true,'Complete Windows App/XAML build required');
  assert.deepEqual(receipt.tests,{status:'not-run',reason:'PR compilation only; focused cases run once during final-main package production'});
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
  assert.equal(fs.readFileSync('RELEASE_VERSION','utf8').trim(),releaseVersion);
  assert.equal(process.env.GITHUB_REPOSITORY,repository);
  assert.equal(git('rev-parse','HEAD'),process.env.GITHUB_SHA,'Actual checkout mismatch');
  if(command==='record-focused') {
    assert.equal(process.env.GITHUB_EVENT_NAME,'workflow_dispatch');
    assert.equal(process.env.GITHUB_REF,'refs/heads/main');
    const trx=path.join(directory,'focused.trx');
    const receipt={schemaVersion:1,sourceCommit:git('rev-parse','HEAD'),sourceTree:git('rev-parse','HEAD^{tree}'),releaseVersion,
      filter:focusedFilter,originalSuiteCaseCount,results:verifyFocusedResults(fs.readFileSync(trx,'utf8')),
      verificationScope:'One focused unit-test invocation; no native smoke/stress, model or GPU execution',
      files:{'focused.trx':identity(trx)}};
    fs.writeFileSync(path.join(directory,'focused-validation.json'),JSON.stringify(receipt,null,2)+'\n');
  } else if(command==='record') {
    assert.equal(process.env.GITHUB_EVENT_NAME,'pull_request');
    const receipt={schemaVersion:1,kind,repository,
      runId:Number(process.env.GITHUB_RUN_ID),runAttempt:Number(process.env.GITHUB_RUN_ATTEMPT),
      sourceCommit:git('rev-parse','HEAD'),sourceTree:git('rev-parse','HEAD^{tree}'),releaseVersion,
      appXamlBuild:true,
      tests:{status:'not-run',reason:'PR compilation only; focused cases run once during final-main package production'},
      files:{'app-build.txt':identity(path.join(directory,'app-build.txt'))}};
    verifyValidation(directory,receipt,receipt);
    fs.writeFileSync(path.join(directory,'validation-receipt.json'),JSON.stringify(receipt,null,2)+'\n');
  } else if(command==='find') {
    assert.equal(process.env.GITHUB_REF,'refs/heads/main');
    const tree=git('rev-parse','HEAD^{tree}');
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
      const artifact=artifacts.artifacts?.find(a=>a.name===`dropspace-beta18-validation-${run.id}-${run.run_attempt}`&&!a.expired);
      if(!artifact)continue;
      fs.mkdirSync('artifacts',{recursive:true});
      fs.writeFileSync('artifacts/beta18-validation-expected.json',JSON.stringify({repository,runId:run.id,
        runAttempt:run.run_attempt,sourceTree:tree,releaseVersion,artifactId:artifact.id}));
      fs.appendFileSync(process.env.GITHUB_OUTPUT,`run_id=${run.id}\nartifact_id=${artifact.id}\n`);return;
    }
    throw new Error('No successful merged PR build for exact Beta18 main; no broad-test fallback');
  } else if(command==='verify') {
    const expected=read('artifacts/beta18-validation-expected.json');
    assert.equal(expected.sourceTree,git('rev-parse','HEAD^{tree}'));
    const run=await api(`actions/runs/${expected.runId}`);producer(run);
    assert.equal(run.run_attempt,expected.runAttempt);
    const receipt=read(path.join(directory,'validation-receipt.json'));
    const commit=await api(`git/commits/${receipt.sourceCommit}`);
    assert.equal(commit.tree.sha,expected.sourceTree,'Built PR tree differs from final main');
    assert.ok(receipt.sourceCommit===run.head_sha||commit.parents?.some(parent=>parent.sha===run.head_sha),'Build is not bound to producer head');
    verifyValidation(directory,receipt,expected);
    if(process.env.GITHUB_STEP_SUMMARY)fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY,
      `Reused full Windows App/XAML PR build ${run.id}/${run.run_attempt}, checkout ${receipt.sourceCommit}, identical tree ${receipt.sourceTree}. PR functional tests were not run. Final-main focused cases and package-byte checks follow.\n`);
  } else throw new Error('Expected record, record-focused, find or verify');
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))main().catch(error=>{console.error(error.message);process.exitCode=1;});

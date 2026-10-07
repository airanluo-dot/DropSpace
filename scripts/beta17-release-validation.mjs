// Beta17 reuses only successful, identical-tree PR validation. No check status
// is created and no test result is inferred from compilation or package hashes.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {execFileSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {fileIdentity} from './ai-runtime-publication.mjs';

export const beta17Version = 'v0.3.1-beta.17';
export const focusedFilters = Object.freeze({
  infrastructure: 'FullyQualifiedName~SqliteUiExecutionBoundaryTests|FullyQualifiedName~DatabaseWriteBoundaryTests|FullyQualifiedName~SqliteFileIntakeBoundaryTests|FullyQualifiedName~DownloadManagerLifetimeTests|FullyQualifiedName~CudaLyricsRuntimePackageTests',
  app: 'FullyQualifiedName~ItemProjectionServiceTests|FullyQualifiedName~MusicUiPressureTests|FullyQualifiedName~SystemVisualPreferenceTests|FullyQualifiedName~MediaNotificationTests|FullyQualifiedName~MediaRegressionTests|FullyQualifiedName~ExpandedMusicLyricsTests',
});
const repository = 'airanluo-dot/DropSpace';
const files = ['app-build.txt', 'infrastructure.trx', 'app.trx'];
const buildOnlyKind = 'beta17-owner-waived-windows-build';
const ownerWaiverPath = 'docs/dev/evidence/beta17-release/owner-test-waiver.json';
const ownerWaiverSha256 = 'f383ff84a55b16381aa8c79af87e7f8dd2b3bb61724850b1c8865ad42de99285';
const git = (...args) => execFileSync('git', args, {encoding:'utf8'}).trim();
const read = filename => JSON.parse(fs.readFileSync(filename, 'utf8').replace(/^\uFEFF/, ''));
const identity = filename => {const {bytes,sha256}=fileIdentity(filename);return {bytes,sha256};};

function ownerWaiver() {
  assert.equal(identity(ownerWaiverPath).sha256,ownerWaiverSha256,'Exact Beta17 owner test waiver changed');
  const waiver=read(ownerWaiverPath);
  assert.equal(waiver.schemaVersion,1);
  assert.equal(waiver.releaseTag,beta17Version);
  assert.equal(waiver.owner,'airanluo-dot');
  assert.deepEqual(waiver.ownerMessages,['减少测试数量，减少多余测试，直接尽快发布','我说了跳过测试立刻发布啊']);
  return {path:ownerWaiverPath,sha256:ownerWaiverSha256,ownerMessages:waiver.ownerMessages,decision:waiver.decision};
}

export function passedCounters(xml) {
  const element = xml.match(/<Counters\b[^>]*\/>/g);
  assert.equal(element?.length, 1, 'One real TRX result summary is required');
  const counts = Object.fromEntries([...element[0].matchAll(/(\w+)="(\d+)"/g)].map(([,key,value])=>[key,Number(value)]));
  assert.ok(counts.total>0 && counts.executed===counts.total && counts.passed===counts.total, 'Focused tests must all execute and pass');
  assert.equal(counts.failed,0,'Focused tests failed');
  assert.equal(counts.notExecuted,0,'Skipped tests cannot qualify the candidate');
  return {total:counts.total,passed:counts.passed};
}
export function verifyValidation(directory, receipt, expected) {
  assert.equal(receipt.schemaVersion,1);
  assert.ok(receipt.kind==='beta17-focused-windows-pr-validation'||receipt.kind===buildOnlyKind,'Unsupported actual PR evidence kind');
  for(const key of ['repository','runId','runAttempt','sourceTree','releaseVersion'])
    assert.equal(receipt[key],expected[key],`Validation ${key} mismatch`);
  assert.match(receipt.sourceCommit??'',/^[a-f0-9]{40}$/);
  assert.equal(receipt.appXamlBuild,true,'Complete Windows App/XAML build is required');
  if(receipt.kind===buildOnlyKind) {
    assert.deepEqual(receipt.ownerWaiver,ownerWaiver());
    assert.deepEqual(receipt.tests,{status:'not-run',reason:'Explicit owner waiver; no automated tests or native smoke/stress executed'});
    assert.equal(receipt.results,undefined,'Build-only receipt cannot claim test results');
    assert.equal(receipt.filters,undefined,'Build-only receipt cannot imply test coverage');
    assert.deepEqual(Object.keys(receipt.files),['app-build.txt']);
    assert.deepEqual(receipt.files['app-build.txt'],identity(path.join(directory,'app-build.txt')),'Actual full App/XAML build evidence changed');
    return receipt;
  }
  assert.deepEqual(receipt.filters,focusedFilters,'Validation filters changed');
  assert.deepEqual(Object.keys(receipt.files).sort(),[...files].sort());
  for(const name of files) assert.deepEqual(receipt.files[name],identity(path.join(directory,name)),`Validation evidence changed: ${name}`);
  for(const name of ['infrastructure','app'])
    assert.deepEqual(receipt.results[name],passedCounters(fs.readFileSync(path.join(directory,`${name}.trx`),'utf8')));
  return receipt;
}
async function api(route) {
  const response=await fetch(`https://api.github.com/repos/${repository}/${route}`,{
    headers:{Authorization:`Bearer ${process.env.GH_TOKEN}`,Accept:'application/vnd.github+json','X-GitHub-Api-Version':'2022-11-28'}});
  assert.ok(response.ok,`GitHub validation metadata failed (${response.status})`);
  return response.json();
}
function producer(run) {
  assert.equal(run.repository?.full_name,repository);
  assert.equal(run.head_repository?.full_name,repository);
  assert.equal(run.path,'.github/workflows/ci.yml');
  assert.equal(run.event,'pull_request');
  assert.equal(run.status,'completed');
  assert.equal(run.conclusion,'success');
  assert.ok(Number.isSafeInteger(run.id)&&run.id>0 && Number.isSafeInteger(run.run_attempt)&&run.run_attempt>0);
}
async function main() {
  const [command,directory]=process.argv.slice(2);
  assert.equal(fs.readFileSync('RELEASE_VERSION','utf8').trim(),beta17Version,'Route is limited to the reviewed Beta17 release');
  assert.equal(process.env.GITHUB_REPOSITORY,repository);
  assert.equal(git('rev-parse','HEAD'),process.env.GITHUB_SHA,'Actual checkout commit mismatch');
  if(command==='verify-owner-waiver') {
    ownerWaiver();
  } else if(command==='record-build-only') {
    assert.equal(process.env.GITHUB_EVENT_NAME,'pull_request');
    const receipt={schemaVersion:1,kind:buildOnlyKind,repository,
      runId:Number(process.env.GITHUB_RUN_ID),runAttempt:Number(process.env.GITHUB_RUN_ATTEMPT),
      sourceCommit:git('rev-parse','HEAD'),sourceTree:git('rev-parse','HEAD^{tree}'),releaseVersion:beta17Version,
      appXamlBuild:true,ownerWaiver:ownerWaiver(),
      tests:{status:'not-run',reason:'Explicit owner waiver; no automated tests or native smoke/stress executed'},
      files:{'app-build.txt':identity(path.join(directory,'app-build.txt'))}};
    verifyValidation(directory,receipt,receipt);
    fs.writeFileSync(path.join(directory,'validation-receipt.json'),JSON.stringify(receipt,null,2)+'\n');
  } else if(command==='record') {
    assert.equal(process.env.GITHUB_EVENT_NAME,'pull_request');
    const receipt={schemaVersion:1,kind:'beta17-focused-windows-pr-validation',repository,
      runId:Number(process.env.GITHUB_RUN_ID),runAttempt:Number(process.env.GITHUB_RUN_ATTEMPT),
      sourceCommit:git('rev-parse','HEAD'),sourceTree:git('rev-parse','HEAD^{tree}'),releaseVersion:beta17Version,
      appXamlBuild:true,filters:focusedFilters,
      results:Object.fromEntries(['infrastructure','app'].map(name=>[name,passedCounters(fs.readFileSync(path.join(directory,`${name}.trx`),'utf8'))])),
      files:Object.fromEntries(files.map(name=>[name,identity(path.join(directory,name))]))};
    verifyValidation(directory,receipt,receipt);
    fs.writeFileSync(path.join(directory,'validation-receipt.json'),JSON.stringify(receipt,null,2)+'\n');
  } else if(command==='find') {
    assert.equal(process.env.GITHUB_REF,'refs/heads/main','Packaging requires final main');
    const tree=git('rev-parse','HEAD^{tree}');
    const runs=await api('actions/workflows/ci.yml/runs?status=success&event=pull_request&per_page=30');
    for(const candidate of runs.workflow_runs??[]) {
      const run=await api(`actions/runs/${candidate.id}`);producer(run);
      const associated=run.pull_requests?.length?run.pull_requests:await api(`commits/${run.head_sha}/pulls`);
      let merged=false;
      for(const pr of associated) {
        const current=await api(`pulls/${pr.number}`);
        if(current.merged && current.merge_commit_sha===process.env.GITHUB_SHA) {merged=true;break;}
      }
      if(!merged)continue;
      const artifacts=await api(`actions/runs/${run.id}/artifacts?per_page=100`);
      const artifact=artifacts.artifacts?.find(a=>a.name===`dropspace-beta17-validation-${run.id}-${run.run_attempt}`&&!a.expired);
      if(!artifact)continue;
      fs.mkdirSync('artifacts',{recursive:true});
      fs.writeFileSync('artifacts/beta17-validation-expected.json',JSON.stringify({repository,runId:run.id,
        runAttempt:run.run_attempt,sourceTree:tree,releaseVersion:beta17Version,artifactId:artifact.id}));
      fs.appendFileSync(process.env.GITHUB_OUTPUT,`run_id=${run.id}\nartifact_id=${artifact.id}\n`);return;
    }
    throw new Error('No successful merged PR validation exists for this exact Beta17 main commit; no broad-test or model-download fallback');
  } else if(command==='verify'||command==='verify-build-only') {
    const expected=read('artifacts/beta17-validation-expected.json');
    assert.equal(expected.sourceTree,git('rev-parse','HEAD^{tree}'));
    const run=await api(`actions/runs/${expected.runId}`);producer(run);
    assert.equal(run.run_attempt,expected.runAttempt,'PR producer rerun changed');
    const receipt=read(path.join(directory,'validation-receipt.json'));
    if(command==='verify-build-only')assert.equal(receipt.kind,buildOnlyKind,'Owner-waived route requires an actual build-only receipt');
    const commit=await api(`git/commits/${receipt.sourceCommit}`);
    assert.equal(commit.tree.sha,expected.sourceTree,'Actual built PR tree differs from final main');
    assert.ok(receipt.sourceCommit===run.head_sha || commit.parents?.some(parent=>parent.sha===run.head_sha),'Build checkout is not bound to the actual PR head');
    verifyValidation(directory,receipt,expected);
    if(process.env.GITHUB_STEP_SUMMARY)fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY,receipt.kind===buildOnlyKind
      ? `Reused actual full Windows App/XAML PR build ${run.id}/${run.run_attempt}, checkout ${receipt.sourceCommit}, identical tree ${receipt.sourceTree}. All additional tests and native smoke/stress are not run under the fixed owner waiver ${ownerWaiverPath} (${ownerWaiverSha256}). Final-commit packaging and actual payload byte verification follow. No test pass is claimed.\n`
      : `Reused successful PR validation ${run.id}/${run.run_attempt}, checkout ${receipt.sourceCommit}, identical tree ${receipt.sourceTree}. Complete Windows App/XAML build and ${receipt.results.infrastructure.total+receipt.results.app.total} focused tests were performed by that PR; no unit tests repeated here. Final-commit packaging and native visual verification follow.\n`);
  } else throw new Error('Expected record, record-build-only, find, verify, verify-build-only or verify-owner-waiver');
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))main().catch(error=>{console.error(error.message);process.exitCode=1;});

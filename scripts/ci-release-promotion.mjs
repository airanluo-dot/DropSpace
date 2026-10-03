import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { verifyReleaseBinding } from './ai-runtime-publication.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const git = (...args) => execFileSync('git', args, {encoding:'utf8'}).trim();
const payloads = ['DropSpace.exe','DropSpaceSetup.exe','DropSpace-x64.msix','runtime-publication.json','DropSpace.Identity.msix'];
const read = p => JSON.parse(fs.readFileSync(p,'utf8').replace(/^\uFEFF/,''));
const identity = p => ({bytes:fs.statSync(p).size,sha256:hash(fs.readFileSync(p))});
export function validateProducer(run, repository) {
  assert.equal(run.repository?.full_name, repository, 'CI repository mismatch');
  assert.equal(run.head_repository?.full_name, repository, 'CI head repository mismatch');
  assert.equal(run.path, '.github/workflows/ci.yml', 'Not the Windows CI producer');
  assert.equal(run.status, 'completed', 'CI is unfinished');
  assert.equal(run.conclusion, 'success', 'CI did not succeed');
  assert.ok(['pull_request','workflow_dispatch'].includes(run.event), 'Unsupported producer event');
  assert.ok(Number.isSafeInteger(run.id) && run.id > 0);
  assert.ok(Number.isSafeInteger(run.run_attempt) && run.run_attempt > 0);
}
export function verifyReceipt(directory, receipt, expected) {
  assert.equal(receipt.schemaVersion,1);
  for(const field of ['repository','runId','runAttempt','sourceTree','releaseVersion'])
    assert.equal(receipt[field],expected[field],`Promotion ${field} mismatch`);
  assert.match(receipt.sourceCommit ?? '',/^[a-f0-9]{40}$/);
  assert.deepEqual(Object.keys(receipt.files).sort(),[...payloads].sort());
  for(const name of payloads) assert.deepEqual(receipt.files[name],identity(path.join(directory,name)),`Promoted bytes changed: ${name}`);
  verifyReleaseBinding(directory,{expectedCommit:receipt.sourceCommit});
  return receipt;
}
async function api(route) {
  const response=await fetch(`https://api.github.com/repos/${process.env.GITHUB_REPOSITORY}/${route}`,{
    headers:{Authorization:`Bearer ${process.env.GH_TOKEN}`,Accept:'application/vnd.github+json','X-GitHub-Api-Version':'2022-11-28'}});
  assert.ok(response.ok,`GitHub metadata failed (${response.status})`);
  return response.json();
}
function output(key,value) { fs.appendFileSync(process.env.GITHUB_OUTPUT,`${key}=${value}\n`); }
async function main() {
 const [command,directory]=process.argv.slice(2);
 if(command==='record') {
  const sourceCommit=git('rev-parse','HEAD');
  assert.equal(sourceCommit,process.env.GITHUB_SHA);
  verifyReleaseBinding(directory,{expectedCommit:sourceCommit});
  const receipt={schemaVersion:1,repository:process.env.GITHUB_REPOSITORY,runId:Number(process.env.GITHUB_RUN_ID),runAttempt:Number(process.env.GITHUB_RUN_ATTEMPT),sourceCommit,sourceTree:git('rev-parse','HEAD^{tree}'),releaseVersion:fs.readFileSync('RELEASE_VERSION','utf8').trim(),files:Object.fromEntries(payloads.map(name=>[name,identity(path.join(directory,name))]))};
  fs.writeFileSync(path.join(directory,'ci-release-receipt.json'),JSON.stringify(receipt,null,2)+'\n');
 } else if(command==='find') {
  // Explicit publication is allowed to reuse only a successful complete CI run
  // whose actual checkout tree is byte-identical to this reviewed main tree.
  output('reuse','false');
  const tree=git('rev-parse','HEAD^{tree}');
  const runs=await api('actions/workflows/ci.yml/runs?status=success&per_page=30');
  for(const candidate of runs.workflow_runs ?? []) {
   if(!['pull_request','workflow_dispatch'].includes(candidate.event)) continue;
   const run=await api(`actions/runs/${candidate.id}`);
   validateProducer(run,process.env.GITHUB_REPOSITORY);
   const artifacts=await api(`actions/runs/${run.id}/artifacts?per_page=100`);
   const name=`dropspace-release-promotion-${run.id}-${run.run_attempt}`;
   const artifact=artifacts.artifacts?.find(a=>a.name===name&&!a.expired);
   if(!artifact) continue;
   // A PR head tree need not be its tested merge tree. The downloadable receipt
   // supplies the checkout commit; it is checked against GitHub's commit object
   // after download, before any bytes become publication candidates.
   const expected={schemaVersion:1,repository:process.env.GITHUB_REPOSITORY,runId:run.id,runAttempt:run.run_attempt,sourceTree:tree,releaseVersion:fs.readFileSync('RELEASE_VERSION','utf8').trim(),artifactId:artifact.id,artifactDigest:artifact.digest};
   // Filter unrelated revisions without downloading large bundles. A successful
   // PR run must be for a PR merged into this exact main commit.
   if(run.event==='pull_request') {
    const associated=run.pull_requests?.length ? run.pull_requests : await api(`commits/${run.head_sha}/pulls`);
    let sameMerge=false;
    for(const pr of associated) {
      const current=await api(`pulls/${pr.number}`);
      if(current.merged && current.merge_commit_sha===process.env.GITHUB_SHA) {sameMerge=true;break;}
    }
    if(!sameMerge) continue;
   } else if(run.head_sha!==process.env.GITHUB_SHA) continue;
   fs.mkdirSync('artifacts',{recursive:true});
   fs.writeFileSync('artifacts/promotion-expected.json',JSON.stringify(expected));
   output('run_id',run.id);output('artifact_id',artifact.id);output('reuse','true');return;
  }
 } else if(command==='promote') {
  const expected=read('artifacts/promotion-expected.json');
  assert.equal(git('rev-parse','HEAD'),process.env.GITHUB_SHA,'Publication checkout mismatch');
  assert.equal(expected.sourceTree,git('rev-parse','HEAD^{tree}'),'Publication tree changed');
  const run=await api(`actions/runs/${expected.runId}`);
  validateProducer(run,process.env.GITHUB_REPOSITORY);
  assert.equal(run.run_attempt,expected.runAttempt,'Producer rerun changed');
  const receipt=read(path.join(directory,'ci-release-receipt.json'));
  const commit=await api(`git/commits/${receipt.sourceCommit}`);
  assert.equal(commit.tree.sha,expected.sourceTree,'Actual CI checkout differs from release tree');
  assert.ok(receipt.sourceCommit===run.head_sha || commit.parents?.some(parent=>parent.sha===run.head_sha),'CI checkout is not bound to the producer head');
  verifyReceipt(directory,receipt,expected);
  const binding=verifyReleaseBinding(directory,{expectedCommit:receipt.sourceCommit});
  // Preserve the actual build commit and receipt. Only the publication binding
  // moves to the identical-tree main commit; binaries are never rebuilt/modified.
  binding.buildSourceCommit=receipt.sourceCommit;
  binding.buildSourceTree=receipt.sourceTree;
  binding.sourceCommit=process.env.GITHUB_SHA;
  fs.writeFileSync(path.join(directory,'runtime-publication.json'),JSON.stringify(binding,null,2)+'\n');
  verifyReleaseBinding(directory,{expectedCommit:process.env.GITHUB_SHA});
  fs.mkdirSync('artifacts/identity',{recursive:true});
  fs.copyFileSync(path.join(directory,'DropSpace.Identity.msix'),'artifacts/identity/DropSpace.Identity.msix');
  fs.writeFileSync(path.join(directory,'build-environment.json'),JSON.stringify({schemaVersion:1,promoted:true,producerReceipt:receipt,publicationCommit:process.env.GITHUB_SHA},null,2)+'\n');
  if(process.env.GITHUB_STEP_SUMMARY) fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY,`Reused validated CI run ${run.id}, attempt ${run.run_attempt}. Actual build commit ${receipt.sourceCommit}; identical tree ${receipt.sourceTree}. No compilation or tests repeated.\n`);
 } else throw new Error('Expected record, find, or promote');
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)) main().catch(error=>{console.error(error.message);process.exitCode=1;});

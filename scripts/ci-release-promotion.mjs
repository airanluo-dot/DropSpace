import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { fileIdentity, verifyReleaseBinding } from './ai-runtime-publication.mjs';
import {focusedFilter,originalSuiteCaseCount,verifyFocusedResults} from './beta18-release-validation.mjs';

const git = (...args) => execFileSync('git', args, {encoding:'utf8'}).trim();
const payloads = ['DropSpace.exe','DropSpaceSetup.exe','DropSpace-x64.msix','runtime-publication.json','DropSpace.Identity.msix'];
const independentCudaVersions = new Set(['v0.3.1-beta.17','v0.3.1-beta.18']);
export function promotionPayloads(releaseVersion) {
  return independentCudaVersions.has(releaseVersion)
    ? [...payloads,'cuda-runtime-download.json','cuda-runtime-manifest.json','update-manifest.json','SHA256SUMS.txt']
    : payloads;
}
const read = p => JSON.parse(fs.readFileSync(p,'utf8').replace(/^\uFEFF/,''));
const identity = p => {const {bytes,sha256}=fileIdentity(p);return {bytes,sha256};};
export function verifyBeta17Metadata(directory, sourceCommit) {
  return verifyIndependentCudaMetadata(directory,sourceCommit,'v0.3.1-beta.17');
}
export function verifyIndependentCudaMetadata(directory,sourceCommit,releaseVersion) {
  assert.ok(independentCudaVersions.has(releaseVersion),'Unreviewed independent CUDA App release');
  const descriptor=read(path.join(directory,'cuda-runtime-download.json'));
  const manifest=identity(path.join(directory,'cuda-runtime-manifest.json'));
  assert.equal(descriptor.schemaVersion,2,'Independent component contract required');
  assert.deepEqual(descriptor.appRelease,{tag:releaseVersion,sourceCommit},'CUDA metadata differs from the actual App build commit');
  assert.equal(descriptor.componentRelease?.tag,'cuda-llama-cpp-v0.5.0-cuda13-win-x64-v1');
  assert.equal(descriptor.download?.url,'https://github.com/airanluo-dot/DropSpace/releases/download/cuda-llama-cpp-v0.5.0-cuda13-win-x64-v1/DropSpace-CUDA-llama-cpp-v0.5.0-cuda13-win-x64-v1.zip');
  assert.equal(descriptor.download.bytes,540873572);
  assert.equal(descriptor.download.sha256,'79e8deb4f8c35e7c9c94da0efa0752826bafe510fcb1e54da23062f2d6f40a0b');
  assert.deepEqual(manifest,{bytes:1815,sha256:'da8742d806541edf452061eec408f645be704445952a93895bc8e9d6a200215a'});
  assert.equal(descriptor.manifest?.bytes,manifest.bytes);
  assert.equal(descriptor.manifest?.sha256,manifest.sha256);
  const names=promotionPayloads(releaseVersion).filter(name=>name!=='DropSpace.Identity.msix'&&name!=='SHA256SUMS.txt');
  const checksums=fs.readFileSync(path.join(directory,'SHA256SUMS.txt'),'utf8').trim().split(/\r?\n/);
  assert.equal(checksums.length,names.length,'Every public App asset needs one checksum');
  const seen=new Set();
  for(const line of checksums) {
    const match=line.match(/^([a-f0-9]{64})  ([A-Za-z0-9_.-]+)$/);
    assert.ok(match&&names.includes(match[2])&&!seen.has(match[2]),'Unexpected or duplicate checksum asset');
    seen.add(match[2]);
    assert.equal(match[1],identity(path.join(directory,match[2])).sha256,'Final public asset checksum mismatch');
  }
}
function verifyBeta18FocusedValidation(directory,sourceCommit) {
  const validation=read(path.join(directory,'focused-validation.json'));
  assert.equal(validation.schemaVersion,1);
  assert.equal(validation.sourceCommit,sourceCommit,'Focused execution used different App source');
  assert.equal(validation.releaseVersion,'v0.3.1-beta.18');
  assert.equal(validation.filter,focusedFilter);
  assert.equal(validation.originalSuiteCaseCount,originalSuiteCaseCount);
  const trx=path.join(directory,'focused.trx');
  assert.deepEqual(validation.files,{'focused.trx':identity(trx)},'Actual focused execution evidence changed');
  assert.deepEqual(validation.results,verifyFocusedResults(fs.readFileSync(trx,'utf8')));
  return validation;
}
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
  const names=promotionPayloads(expected.releaseVersion);
  assert.deepEqual(Object.keys(receipt.files).sort(),[...names].sort());
  for(const name of names) assert.deepEqual(receipt.files[name],identity(path.join(directory,name)),`Promoted bytes changed: ${name}`);
  verifyReleaseBinding(directory,{expectedCommit:receipt.sourceCommit});
  if(independentCudaVersions.has(expected.releaseVersion))verifyIndependentCudaMetadata(directory,receipt.sourceCommit,expected.releaseVersion);
  if(expected.releaseVersion==='v0.3.1-beta.18') {
    const validation=verifyBeta18FocusedValidation(directory,receipt.sourceCommit);
    assert.equal(validation.sourceTree,receipt.sourceTree,'Focused execution used different reviewed source tree');
    assert.deepEqual(receipt.focusedValidation,validation);
  }
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
  const releaseVersion=fs.readFileSync('RELEASE_VERSION','utf8').trim();
  if(independentCudaVersions.has(releaseVersion))verifyIndependentCudaMetadata(directory,sourceCommit,releaseVersion);
  const receipt={schemaVersion:1,repository:process.env.GITHUB_REPOSITORY,runId:Number(process.env.GITHUB_RUN_ID),runAttempt:Number(process.env.GITHUB_RUN_ATTEMPT),sourceCommit,sourceTree:git('rev-parse','HEAD^{tree}'),releaseVersion,files:Object.fromEntries(promotionPayloads(releaseVersion).map(name=>[name,identity(path.join(directory,name))]))};
  if(releaseVersion==='v0.3.1-beta.18') {
    receipt.focusedValidation=verifyBeta18FocusedValidation(directory,sourceCommit);
    assert.equal(receipt.focusedValidation.sourceTree,receipt.sourceTree);
  }
  fs.writeFileSync(path.join(directory,'ci-release-receipt.json'),JSON.stringify(receipt,null,2)+'\n');
 } else if(command==='verify-final') {
  const receipt=read(path.join(directory,'ci-release-receipt.json'));
  const releaseVersion=fs.readFileSync('RELEASE_VERSION','utf8').trim();
  assert.equal(releaseVersion,'v0.3.1-beta.18','Final receipt route is exact Beta18 only');
  assert.equal(git('rev-parse','HEAD'),process.env.GITHUB_SHA);
  assert.equal(receipt.sourceCommit,process.env.GITHUB_SHA,'Final App producer differs from publication commit');
  const run=await api(`actions/runs/${receipt.runId}`);
  validateProducer(run,process.env.GITHUB_REPOSITORY);
  assert.equal(run.event,'workflow_dispatch');
  assert.equal(run.head_sha,process.env.GITHUB_SHA);
  assert.equal(run.run_attempt,receipt.runAttempt);
  verifyReceipt(directory,receipt,{...receipt,repository:process.env.GITHUB_REPOSITORY,
    sourceTree:git('rev-parse','HEAD^{tree}'),releaseVersion});
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
  if(independentCudaVersions.has(expected.releaseVersion)) {
    // The CUDA descriptor is embedded in the App. Final-main packaging must
    // already match publication; retain all approved/checksummed bytes unchanged.
    assert.equal(receipt.sourceCommit,process.env.GITHUB_SHA,'Independent-CUDA App packages must come from the exact final main commit');
    verifyIndependentCudaMetadata(directory,process.env.GITHUB_SHA,expected.releaseVersion);
  } else {
    binding.buildSourceCommit=receipt.sourceCommit;
    binding.buildSourceTree=receipt.sourceTree;
    binding.sourceCommit=process.env.GITHUB_SHA;
    fs.writeFileSync(path.join(directory,'runtime-publication.json'),JSON.stringify(binding,null,2)+'\n');
  }
  verifyReleaseBinding(directory,{expectedCommit:process.env.GITHUB_SHA});
  fs.mkdirSync('artifacts/identity',{recursive:true});
  fs.copyFileSync(path.join(directory,'DropSpace.Identity.msix'),'artifacts/identity/DropSpace.Identity.msix');
  fs.writeFileSync(path.join(directory,'build-environment.json'),JSON.stringify({schemaVersion:1,promoted:true,producerReceipt:receipt,publicationCommit:process.env.GITHUB_SHA},null,2)+'\n');
  if(process.env.GITHUB_STEP_SUMMARY) fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY,`Reused validated CI run ${run.id}, attempt ${run.run_attempt}. Actual build commit ${receipt.sourceCommit}; identical tree ${receipt.sourceTree}. No compilation or tests repeated.\n`);
 } else throw new Error('Expected record, find, promote or verify-final');
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)) main().catch(error=>{console.error(error.message);process.exitCode=1;});

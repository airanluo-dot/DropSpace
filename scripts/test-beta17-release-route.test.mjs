import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {passedCounters,focusedFilters,verifyValidation} from './beta17-release-validation.mjs';
import {promotionPayloads,verifyBeta17Metadata} from './ci-release-promotion.mjs';
import {fileIdentity} from './ai-runtime-publication.mjs';

const source = name => fs.readFileSync(new URL(name,import.meta.url),'utf8').replaceAll('\r\n','\n');
const identity = name => {const {bytes,sha256}=fileIdentity(name);return {bytes,sha256};};
const trx = '<Counters total="2" executed="2" passed="2" failed="0" notExecuted="0" />';
test('only executed, entirely passing focused TRX results qualify reuse',()=>{
  assert.deepEqual(passedCounters(trx),{total:2,passed:2});
  for(const xml of ['',trx+trx,trx.replace('passed="2"','passed="1"'),trx.replace('executed="2"','executed="1"'),trx.replace('notExecuted="0"','notExecuted="1"'),trx.replaceAll('="2"','="0"')])
    assert.throws(()=>passedCounters(xml));
});
function validation(t) {
  const directory=fs.mkdtempSync(path.join(os.tmpdir(),'dropspace-beta17-validation-'));
  t.after(()=>fs.rmSync(directory,{recursive:true,force:true}));
  fs.writeFileSync(path.join(directory,'app-build.txt'),'Synthetic contract fixture, not a Windows build result.');
  for(const name of ['infrastructure','app'])fs.writeFileSync(path.join(directory,`${name}.trx`),trx);
  const expected={repository:'airanluo-dot/DropSpace',runId:12,runAttempt:1,sourceTree:'b'.repeat(40),releaseVersion:'v0.3.1-beta.17'};
  const receipt={schemaVersion:1,kind:'beta17-focused-windows-pr-validation',...expected,sourceCommit:'a'.repeat(40),appXamlBuild:true,
    filters:focusedFilters,results:{infrastructure:{total:2,passed:2},app:{total:2,passed:2}},
    files:Object.fromEntries(['app-build.txt','infrastructure.trx','app.trx'].map(name=>[name,identity(path.join(directory,name))]))};
  return {directory,receipt,expected};
}
test('exact PR validation receipt preserves evidence and actual filter identities',t=>{
  const x=validation(t);verifyValidation(x.directory,x.receipt,x.expected);
});
for(const field of ['sourceTree','releaseVersion','runId','runAttempt'])test(`validation rejects changed ${field}`,t=>{
  const x=validation(t);assert.throws(()=>verifyValidation(x.directory,{...x.receipt,[field]:'different'},x.expected));
});
test('reused validation cannot omit the complete App build or replace test bytes',t=>{
  const x=validation(t);assert.throws(()=>verifyValidation(x.directory,{...x.receipt,appXamlBuild:false},x.expected));
  fs.appendFileSync(path.join(x.directory,'app.trx'),'changed');
  assert.throws(()=>verifyValidation(x.directory,x.receipt,x.expected));
});
function metadata(t) {
  const directory=fs.mkdtempSync(path.join(os.tmpdir(),'dropspace-beta17-metadata-'));
  t.after(()=>fs.rmSync(directory,{recursive:true,force:true}));
  for(const name of ['DropSpace.exe','DropSpaceSetup.exe','DropSpace-x64.msix','runtime-publication.json','update-manifest.json'])
    fs.writeFileSync(path.join(directory,name),`Synthetic ${name}; no package validation claimed.`);
  const manifest=source('../docs/dev/evidence/beta11-local-cuda/cuda13-runtime-manifest.json').replaceAll('\n','\r\n');
  fs.writeFileSync(path.join(directory,'cuda-runtime-manifest.json'),manifest);
  const descriptor={schemaVersion:2,appRelease:{tag:'v0.3.1-beta.17',sourceCommit:'a'.repeat(40)},
    componentRelease:{tag:'cuda-llama-cpp-v0.5.0-cuda13-win-x64-v1'},
    download:{url:'https://github.com/airanluo-dot/DropSpace/releases/download/cuda-llama-cpp-v0.5.0-cuda13-win-x64-v1/DropSpace-CUDA-llama-cpp-v0.5.0-cuda13-win-x64-v1.zip',bytes:540873572,sha256:'79e8deb4f8c35e7c9c94da0efa0752826bafe510fcb1e54da23062f2d6f40a0b'},
    manifest:{bytes:1815,sha256:'da8742d806541edf452061eec408f645be704445952a93895bc8e9d6a200215a'}};
  fs.writeFileSync(path.join(directory,'cuda-runtime-download.json'),JSON.stringify(descriptor));
  const sums=promotionPayloads('v0.3.1-beta.17').filter(name=>name!=='DropSpace.Identity.msix'&&name!=='SHA256SUMS.txt')
    .map(name=>`${identity(path.join(directory,name)).sha256}  ${name}`).join('\n')+'\n';
  fs.writeFileSync(path.join(directory,'SHA256SUMS.txt'),sums);
  return {directory,descriptor};
}
test('Beta17 promotion carries all eight public App assets without CUDA archive bytes',t=>{
  const x=metadata(t);verifyBeta17Metadata(x.directory,'a'.repeat(40));
  assert.equal(promotionPayloads('v0.3.1-beta.17').length,9); // one internal identity package
  assert.equal(promotionPayloads('v0.3.1-beta.16').length,5);
  assert.ok(promotionPayloads('v0.3.1-beta.17').every(name=>!name.endsWith('.zip')));
});
test('embedded App source binding cannot be retargeted to a new merge commit',t=>{
  const x=metadata(t);assert.throws(()=>verifyBeta17Metadata(x.directory,'b'.repeat(40)));
});
test('final publication rejects changed descriptor and public checksum bytes',t=>{
  const x=metadata(t);x.descriptor.download.bytes++;
  fs.writeFileSync(path.join(x.directory,'cuda-runtime-download.json'),JSON.stringify(x.descriptor));
  assert.throws(()=>verifyBeta17Metadata(x.directory,'a'.repeat(40)));
});
test('final publication rejects incomplete checksums',t=>{
  const x=metadata(t);fs.writeFileSync(path.join(x.directory,'SHA256SUMS.txt'),'');
  assert.throws(()=>verifyBeta17Metadata(x.directory,'a'.repeat(40)));
});
test('Beta17 route packages only final main, preserves required PR check, and has no broad fallback',()=>{
  const ci=source('../.github/workflows/ci.yml'),release=source('../.github/workflows/release.yml');
  const producer=source('./Build-Beta17Candidate.ps1'),model=source('./Reuse-BundledLyricsLanguageModel.ps1');
  const job=ci.split('\n  beta17-windows:\n')[1];
  assert.match(job,/name:.*release_version.*Build and test \(x64\)/);
  assert.match(job,/github.event_name == 'pull_request'[\s\S]*Build-Beta17Candidate.ps1 -ValidateOnly/);
  assert.match(job,/github.event_name == 'workflow_dispatch'[\s\S]*Build-Beta17Candidate.ps1\n/);
  assert.match(release,/Require final-main Beta17 producer without broad-test fallback/);
  assert.match(release,/Freeze unsigned bytes and generate release metadata\n        if: steps.version.outputs.tag != 'v0.3.1-beta.17'/);
  assert.doesNotMatch(producer.slice(producer.indexOf("if ($env:GITHUB_EVENT_NAME -cne 'workflow_dispatch'")),/dotnet test/);
  assert.match(producer,/Test-MusicVisualSmoke.ps1 -Language en-US/);
  assert.match(model,/488249448/);
  assert.match(model,/0a9af9f7dda1fc77251ec224f84e72023fe64cb3f0e3d5caa20627db3ae61ac1/);
  assert.match(model,/beta17-pr-validation\/lid.176.bin/);
  assert.doesNotMatch(job+producer+model,/Get-AiLyricsSmokeModel|Run-WindowsProductionEvidence|Build-CudaLyricsExperiment|Get-Content.*manifest.source|Invoke-WebRequest.*manifest.source/);
  for(const name of ['runtime-publication.json','cuda-runtime-download.json','cuda-runtime-manifest.json'])
    assert.ok(release.includes(`needs.validate-release.outputs.tag == 'v0.3.1-beta.17' && 'artifacts/release/${name}'`));
});
test('main missing retained language bytes stops before the PR-only old-App fallback',()=>{
  const model=source('./Reuse-BundledLyricsLanguageModel.ps1');
  const cache=model.indexOf("if (Test-Path -LiteralPath $target)");
  const pr=model.indexOf("if (Test-Path -LiteralPath $prAsset)");
  const guard=model.indexOf("if ($env:GITHUB_REF -ceq 'refs/heads/main')");
  const download=model.indexOf("Invoke-WebRequest -Uri 'https://github.com/airanluo-dot/DropSpace/releases/download/v0.3.1-beta.16/DropSpace.exe'");
  assert.ok(cache>=0&&cache<pr&&pr<guard&&guard<download,'Verified reuse must precede a fail-closed main guard, with PR fallback afterward');
  assert.match(model.slice(cache,pr),/Assert-Model \$target;.*return/);
  assert.match(model.slice(pr,guard),/Assert-Model \$prAsset[\s\S]*Copy-Item[\s\S]*return/);
  assert.match(model.slice(guard,download),/^if \(\$env:GITHUB_REF -ceq 'refs\/heads\/main'\) \{\n    throw 'Final packaging requires the verified PR language asset; no repeated old-App download fallback\.'\n\}/);
  assert.match(model.slice(download),/488249448[\s\S]*0a9af9f7dda1fc77251ec224f84e72023fe64cb3f0e3d5caa20627db3ae61ac1[\s\S]*Extract-StaticBundleAssembly/);
});

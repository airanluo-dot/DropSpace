import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {validateProducer,verifyReceipt} from './ci-release-promotion.mjs';
import {writeReleaseBinding,fileIdentity,hashBytes} from './ai-runtime-publication.mjs';
const repository='airanluo-dot/DropSpace';
const validRun=()=>({repository:{full_name:repository},head_repository:{full_name:repository},path:'.github/workflows/ci.yml',status:'completed',conclusion:'success',event:'pull_request',id:42,run_attempt:1});
test('only completed successful Windows CI is a promotion producer',()=>{
 validateProducer(validRun(),repository);
 for(const change of [{conclusion:'failure'},{status:'in_progress'},{event:'push'},{path:'other.yml'},{repository:{full_name:'other/repo'}},{run_attempt:0}])
  assert.throws(()=>validateProducer({...validRun(),...change},repository));
});
function fixture(t){
 const root=fs.mkdtempSync(path.join(os.tmpdir(),'dropspace-promote-'));t.after(()=>fs.rmSync(root,{recursive:true,force:true}));
 for(const name of ['DropSpace.exe','DropSpaceSetup.exe','DropSpace-x64.msix','DropSpace.Identity.msix']) fs.writeFileSync(path.join(root,name),'synthetic '+name);
 const files=[{path:'runtime-manifest.json',bytes:2,sha256:hashBytes('{}')}];
 const pkg=name=>fileIdentity(path.join(root,name),name);
 writeReleaseBinding(root,{sourceCommit:'a'.repeat(40),runtimeFiles:files,
  portable:{schemaVersion:1,package:pkg('DropSpace.exe'),files},
  msix:{schemaVersion:1,package:pkg('DropSpace-x64.msix'),files},
  installer:{schemaVersion:1,kind:'installer-payload',package:pkg('DropSpaceSetup.exe'),installedPortable:pkg('DropSpace.exe')}});
 const expected={repository,runId:42,runAttempt:1,sourceTree:'b'.repeat(40),releaseVersion:'v0.3.1-beta.3'};
 const receipt={schemaVersion:1,...expected,sourceCommit:'a'.repeat(40),files:{}};
 for(const name of ['DropSpace.exe','DropSpaceSetup.exe','DropSpace-x64.msix','DropSpace.Identity.msix','runtime-publication.json']) {
  const {sha256,bytes}=pkg(name);receipt.files[name]={sha256,bytes};
 }
 return {root,receipt,expected};
}
test('identical-tree packages retain their verified byte identities',t=>{
 const x=fixture(t);verifyReceipt(x.root,x.receipt,x.expected);
});
for(const field of ['repository','runId','runAttempt','sourceTree','releaseVersion'])test(`reject changed ${field}`,t=>{
 const x=fixture(t);assert.throws(()=>verifyReceipt(x.root,{...x.receipt,[field]:'wrong'},x.expected));
});
test('changed installer bytes cannot be promoted',t=>{
 const x=fixture(t);fs.appendFileSync(path.join(x.root,'DropSpaceSetup.exe'),'tampered');assert.throws(()=>verifyReceipt(x.root,x.receipt,x.expected));
});
test('missing or added receipt payloads fail closed',t=>{
 const x=fixture(t);x.receipt.files.extra={bytes:1,sha256:'c'.repeat(64)};assert.throws(()=>verifyReceipt(x.root,x.receipt,x.expected));
});

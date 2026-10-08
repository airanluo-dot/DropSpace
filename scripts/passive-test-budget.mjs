// Keep automatic PR execution to the reviewed, frozen focused selection.
// There is no fixed case ceiling for current or future development tasks.
// Heavy regression, browser, model, and GPU suites require explicit manual dispatch.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {execFileSync} from 'node:child_process';

const selected=[
  {file:'tests/DropSpace.Core.Tests/IslandContentPriorityTests.cs',blob:'129b002dfbc96145c99bfead19ee27db22fee5fc',cases:6},
  {file:'tests/DropSpace.Core.Tests/IslandAppearanceSettingsTests.cs',blob:'37058d270e1f8243d11de01e053b4aa5a852b9d6',cases:2},
  {file:'tests/DropSpace.Infrastructure.Tests/LyricsRecoveryRegressionTests.cs',blob:'41eacded89a1214e69f399c0aefd9f45f1698917',cases:2}
];
const planned=selected.reduce((sum,x)=>sum+x.cases,0);
assert.ok(planned>0,'Declared focused PR selection is empty');
const append=(name,value)=>{if(process.env[name])fs.appendFileSync(process.env[name],value+'\n');};
const read=file=>fs.readFileSync(file,'utf8');
const cmd=process.argv[2]??'audit';

if(cmd==='preflight'){
  assert.equal(process.env.GITHUB_EVENT_NAME,'pull_request','Passive cases run on PRs only');
  const changed=selected.filter(x=>execFileSync('git',['hash-object',x.file],{encoding:'utf8'}).trim()!==x.blob);
  if(changed.length){
    append('GITHUB_OUTPUT','run_tests=false');
    append('GITHUB_STEP_SUMMARY','Frozen passive case definitions changed: '+changed.map(x=>x.file).join(', ')+'. Skip automated functional tests rather than risk unbounded parameterized cases. Manual testing remains available.');
    console.log('Frozen case definitions changed; 0 passive test cases executed.');
  }else{
    append('GITHUB_OUTPUT','run_tests=true');
    append('GITHUB_STEP_SUMMARY','Focused PR plan: '+planned+' known cases (8 Core and 2 Infrastructure). Full suites are manually requested only; necessary task testing has no fixed case ceiling.');
    console.log('PASS: '+planned+' frozen focused PR cases selected.');
  }
}else if(cmd==='verify'){
  const directory=process.argv[3];assert.ok(directory,'TRX directory required');
  let total=0;
  for(const item of [{file:'passive-core.trx',count:8},{file:'passive-infrastructure.trx',count:2}]){
    const match=read(path.join(directory,item.file)).match(/<Counters\b[^>]*\/>/g);
    assert.equal(match?.length,1,'One TRX counter required: '+item.file);
    const counts=Object.fromEntries([...match[0].matchAll(/(\w+)="(\d+)"/g)].map(([,k,v])=>[k,Number(v)]));
    assert.equal(counts.total,item.count,'Unapproved case expansion: '+item.file);
    assert.equal(counts.executed,item.count);
    assert.equal(counts.passed,item.count);
    assert.equal(counts.failed,0);
    assert.equal(counts.notExecuted,0);
    total+=counts.executed;
  }
  assert.equal(total,planned);
  append('GITHUB_STEP_SUMMARY','Verified '+total+' selected focused PR case executions without repeats.');
  console.log('PASS: '+total+' selected focused PR cases executed.');
}else if(cmd==='audit'){
  const diag=['ai-model-diagnostics.yml','ct2-runtime-diagnostics.yml','hy-plain-model-diagnostics.yml','m2m-model-diagnostics.yml','marian-model-diagnostics.yml','music-visual-qa.yml'];
  const root='.github/workflows';
  const actual=fs.readdirSync(root).filter(f=>f.endsWith('.yml')).sort();
  const expected=['ci.yml','release.yml','deploy-website.yml','cuda-component-contracts.yml','publish-release-bridge.yml','secret-scan.yml',...diag].sort();
  assert.deepEqual(actual,expected,'Unclassified workflow added; review automatic test selection');
  for(const file of diag){
    const content=read(path.join(root,file));
    assert.match(content,/^on:\n  workflow_dispatch:/m,'Manual trigger missing: '+file);
    assert.doesNotMatch(content,/^  (push|pull_request|release|schedule|create|workflow_run):/m,'Automatic model/visual tests detected: '+file);
  }
  const ci=read(path.join(root,'ci.yml'));
  assert.match(ci,/Enforce permanent passive test waiver/);
  assert.match(ci,/github.event_name == 'pull_request' && env.OWNER_SKIP_TESTS == 'true'/);
  assert.match(ci,/scripts\/passive-test-budget.mjs preflight/);
  assert.match(ci,/scripts\/Run-PassiveFocusedTests.ps1/);
  const website=read(path.join(root,'deploy-website.yml'));
  assert.match(website,/skip_tests=true[\s\S]*?github.event_name[\s\S]*?workflow_dispatch/);
  const cuda=read(path.join(root,'cuda-component-contracts.yml'));
  assert.match(cuda,/\$limited = '\$\{\{ github.event_name \}\}' -cne 'workflow_dispatch'/);
  const release=read(path.join(root,'release.yml'));
  assert.match(release,/allow_unrestricted_fallback:/);
  assert.match(release,/Forbid passive publication fallback to full suites/);
  assert.match(read(path.join(root,'publish-release-bridge.yml')),/allow_unrestricted_fallback=false/);
  console.log('FOCUSED TEST AUDIT: '+actual.length+' classified workflows; planned '+planned+' PR cases; diagnostics manual only; no fixed task case ceiling.');
}else throw Error('Expected audit, preflight or verify');

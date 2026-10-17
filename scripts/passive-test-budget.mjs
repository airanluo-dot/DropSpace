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
  const job=name=>ci.split(`\n  ${name}:\n`)[1]?.split(/\n  [a-z][a-z-]+:\n/)[0]??'';
  const compile=job('compile-windows');
  assert.match(ci,/group: windows-ci-\$\{\{ github.ref \}\}\$\{\{ github.event_name == 'workflow_dispatch' && inputs.compile_only == true && '-compile-only' \|\| '' \}\}/,'Debug compilation must not cancel the ordinary producer');
  const compileInput=ci.split('      compile_only:\n')[1]?.split(/\n      [a-z][a-z_]+:\n/)[0]??'';
  assert.match(compileInput,/default: false\n        type: boolean/,'Compilation-only dispatch must be explicit opt-in');
  assert.match(compile,/if: github.event_name == 'workflow_dispatch' && inputs.compile_only == true/,'Debug compilation must be manually selected');
  assert.match(compile,/contents: read/,'Compilation-only permissions must be read-only');
  assert.doesNotMatch(compile,/\b(?:contents|actions|packages): write\b/,'Compilation must not grant publication permissions');
  assert.match(compile,/dotnet build src\/DropSpace.App\/DropSpace.App.csproj -c Debug --no-restore/);
  assert.match(compile,/-p:GenerateAppxPackageOnBuild=false/);
  assert.match(compile,/kind = 'windows-debug-app-xaml-compile-only'/);
  assert.match(compile,/releasePromotionEligible = \$false/);
  assert.match(compile,/name: dropspace-debug-compile-/);
  assert.doesNotMatch(compile,/\bdotnet\s+(?:test|run|publish)\b|\bnode\s+--test\b|unittest|Test-PortableSmoke|Build-(?:PortableExe|UnsignedPackage|IdentityPackage|Installer)|Get-ReviewedAiRuntime|stage-reviewed-cuda|Stage-LyricsLanguageModel|test-ai-release-approval|ci-release-promotion|next-beta-release-validation/,'Compilation-only dispatch must not execute tests, stage runtime/model assets or produce release evidence');
  for(const name of ['produce-windows','focused-windows'])
    assert.match(job(name),/if: \(github.event_name != 'workflow_dispatch' \|\| inputs.compile_only != true\) &&/,'Existing producer must exclude compilation-only dispatch: '+name);
  assert.match(job('required-validation'),/\[\[ "\$COMPILE_RESULT" == success && "\$FOCUSED_RESULT" == skipped && "\$LEGACY_RESULT" == skipped \]\]/,'Compilation-only required check must reject fallback or concurrent production');
  assert.match(job('required-validation'),/\[\[ "\$COMPILE_RESULT" == skipped \]\]/,'Ordinary validation cannot be satisfied by a Debug build');
  assert.match(job('required-validation'),/name: \$\{\{ github.event_name == 'workflow_dispatch' && inputs.compile_only == true && 'Debug App\/XAML compile check' \|\| 'Build and test \(x64\)' \}\}/,'Debug compilation cannot emit the protected production check name');
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

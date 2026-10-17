import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {runInNewContext} from 'node:vm';
import test from 'node:test';

const ci=fs.readFileSync(new URL('../.github/workflows/ci.yml',import.meta.url),'utf8');
const job=name=>ci.split(`\n  ${name}:\n`)[1]?.split(/\n  [a-z][a-z-]+:\n/)[0]??'';
const required=job('required-validation');
const gate=required.split('        run: |\n')[1].replace(/^          /gm,'').trim();

function evaluate(t,values) {
  const directory=fs.mkdtempSync(path.join(os.tmpdir(),'dropspace-compile-only-'));
  t.after(()=>fs.rmSync(directory,{recursive:true,force:true}));
  const summary=path.join(directory,'summary');
  fs.writeFileSync(summary,'');
  const result=spawnSync('bash',['-e','-c',gate],{encoding:'utf8',env:{...process.env,
    GITHUB_STEP_SUMMARY:summary,SCOPE_RESULT:'success',MAINTENANCE_ONLY:'false',ISOLATED_BETA:'true',
    COMPILE_ONLY:'true',COMPILE_RESULT:'success',FOCUSED_RESULT:'skipped',LEGACY_RESULT:'skipped',...values}});
  assert.ifError(result.error);
  return {...result,summary:fs.readFileSync(summary,'utf8')};
}

test('Debug compilation is manually isolated from release assets and functional execution',()=>{
  const compile=job('compile-windows');
  assert.match(ci,/group: windows-ci-\$\{\{ github.ref \}\}\$\{\{ github.event_name == 'workflow_dispatch' && inputs.compile_only == true && '-compile-only' \|\| '' \}\}/);
  assert.match(compile,/if: github.event_name == 'workflow_dispatch' && inputs.compile_only == true/);
  assert.match(compile,/FULL_TESTS: \$\{\{ inputs.full_tests \}\}/);
  assert.match(compile,/if \(\$env:FULL_TESTS -eq 'true'\) \{ throw/);
  assert.match(compile,/dotnet restore src\/DropSpace.App\/DropSpace.App.csproj -p:Configuration=Debug -p:Platform=x64 -p:RestoreLockedMode=true/);
  assert.match(compile,/dotnet build src\/DropSpace.App\/DropSpace.App.csproj -c Debug --no-restore/);
  assert.match(compile,/-p:AppxPackageSigningEnabled=false -p:GenerateAppxPackageOnBuild=false/);
  assert.doesNotMatch(compile,/\bdotnet\s+(?:test|run|publish)\b|Get-ReviewedAiRuntime|Stage-LyricsLanguageModel|stage-reviewed-cuda|next-beta-validation|release-promotion/);
  assert.match(compile,/kind = 'windows-debug-app-xaml-compile-only'/);
  assert.match(compile,/releasePromotionEligible = \$false/);
  for(const name of ['produce-windows','focused-windows'])
    assert.match(job(name),/if: \(github.event_name != 'workflow_dispatch' \|\| inputs.compile_only != true\) &&/);
});

test('required status accepts the successful compilation-only producer with truthful scope',t=>{
  const result=evaluate(t,{});
  assert.equal(result.status,0,result.stderr);
  assert.match(result.summary,/Debug App\/XAML compilation succeeded; zero functional cases and no Release or publication evidence/);
});

test('required status rejects unavailable Debug builds, failed scope and simultaneous producers',t=>{
  for(const values of [
    {COMPILE_RESULT:'failure'},{COMPILE_RESULT:'cancelled'},{COMPILE_RESULT:'skipped'},
    {SCOPE_RESULT:'failure'},{FOCUSED_RESULT:'success'},{LEGACY_RESULT:'success'},
  ]) assert.notEqual(evaluate(t,values).status,0,JSON.stringify(values));
});

test('ordinary isolated, legacy and maintenance validation retain their required producer',t=>{
  const normal={COMPILE_ONLY:'false',COMPILE_RESULT:'skipped'};
  for(const values of [
    {FOCUSED_RESULT:'success'},
    {ISOLATED_BETA:'false',LEGACY_RESULT:'success'},
    {MAINTENANCE_ONLY:'true'},
  ]) {
    const result=evaluate(t,{...normal,...values});
    assert.equal(result.status,0,JSON.stringify(values)+result.stderr);
    assert.doesNotMatch(result.summary,/Debug App/);
  }
  assert.notEqual(evaluate(t,{...normal,COMPILE_RESULT:'success',FOCUSED_RESULT:'success'}).status,0);
  assert.notEqual(evaluate(t,normal).status,0,'Skipped regular producers cannot be replaced by compilation evidence.');
});


test('compile-only summary cannot emit the protected production check name',()=>{
  const expression=required.match(/^    name: \$\{\{ (.+) \}\}$/m)?.[1];
  assert.ok(expression,'Required check name must be explicitly selected by dispatch scope.');
  assert.equal(expression,"github.event_name == 'workflow_dispatch' && inputs.compile_only == true && 'Debug App/XAML compile check' || 'Build and test (x64)'");
  for(const [event,input,expected] of [
    ['workflow_dispatch',true,'Debug App/XAML compile check'],
    ['workflow_dispatch',false,'Build and test (x64)'],
    ['pull_request',undefined,'Build and test (x64)'],
    ['pull_request',true,'Build and test (x64)'],
  ]) {
    // This actual expression uses the same Boolean/string operators in both
    // JavaScript and Actions; no workflow or protected check is synthesized.
    const name=runInNewContext(expression,{github:{event_name:event},inputs:{compile_only:input}},{timeout:1000});
    assert.equal(name,expected,`${event}/${input}`);
  }
});

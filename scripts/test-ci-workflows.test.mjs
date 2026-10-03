import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';

const read = name => fs.readFileSync(new URL(`../.github/workflows/${name}`, import.meta.url), 'utf8');
const ci = read('ci.yml'), release = read('release.yml'), bridge = read('publish-release-bridge.yml');
const job = (workflow, name) => workflow.split(`\n  ${name}:\n`)[1]?.split(/\n  [a-z][a-z-]+:\n/)[0];
const triggers = workflow => ('\n' + workflow.split('\non:\n')[1]).split('\nconcurrency:')[0].split('\npermissions:')[0];
const producer = job(ci, 'produce-windows'), locales = job(ci, 'smoke-portable');
const required = job(ci, 'build-and-test'), worker = job(ci, 'share-worker');
const runBlock = (source, name) => source.split(`      - name: ${name}\n`)[1].split(/\n      - name:/)[0]
  .split('        run: |\n')[1].replace(/^          /gm, '').trim();

function bash(t, script, values) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'dropspace-ci-gate-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const summary = path.join(root, 'summary');
  fs.writeFileSync(summary, '');
  const execution = spawnSync('bash', ['-e', '-c', script], {
    encoding: 'utf8', env: { ...process.env, ...values, GITHUB_STEP_SUMMARY: summary },
  });
  assert.ifError(execution.error);
  return { ...execution, summary: fs.readFileSync(summary, 'utf8') };
}

test('PR revisions have one Windows pipeline and always emit the required checks', () => {
  const events = triggers(ci);
  assert.match(events, /\n  push:\n    branches: \[main\]/);
  assert.match(events, /\n  pull_request:\n    branches: \[main\]/);
  assert.match(events, /\n  workflow_dispatch:/);
  assert.doesNotMatch(events, /agent\/|paths(?:-ignore)?:/);
  assert.match(required, /name: Build and test \(x64, \$\{\{ matrix.language \}\}\)/);
  assert.match(required, /language: \[en-US, zh-CN\]/);
  assert.match(required, /runs-on: ubuntu-latest/);
  assert.doesNotMatch(required, /actions\/checkout|actions\/download-artifact|dotnet|Test-PortableSmoke/);
  assert.match(job(ci, 'share-worker'), /name: Test encrypted share worker/);
});

test('failed, cancelled, and skipped producers cannot yield successful required checks', () => {
  assert.match(locales, /needs: produce-windows\n    if: \$\{\{ always\(\) \}\}/);
  assert.match(locales, /PRODUCER_RESULT: \$\{\{ needs.produce-windows.result \}\}/);
  assert.match(locales, /if \(\$env:PRODUCER_RESULT -cne 'success'\) \{\s+throw/);
  assert.ok(locales.indexOf('Require successful shared producer') < locales.indexOf('Check out source'));
  assert.match(required, /needs: \[produce-windows, smoke-portable\]\n    if: \$\{\{ always\(\) \}\}/);
  assert.match(required, /SMOKE_RESULT: \$\{\{ needs.smoke-portable.result \}\}/);
  assert.doesNotMatch(producer + locales + required + worker, /continue-on-error:/);
});

test('the actual compatibility gates succeed only when producer and both-language validation succeed', t => {
  const gate = runBlock(required, 'Reflect the consolidated Windows validation result');
  for (const producerResult of ['success', 'failure', 'cancelled', 'skipped', '']) {
    for (const smokeResult of ['success', 'failure', 'cancelled', 'skipped', '']) {
    const execution = bash(t, gate, { PRODUCER_RESULT: producerResult, SMOKE_RESULT: smokeResult });
    if (producerResult === 'success' && smokeResult === 'success') {
      assert.equal(execution.status, 0);
      assert.match(execution.summary, /No additional tests ran/);
    }
    else {
      assert.notEqual(execution.status, 0, `${producerResult}/${smokeResult} must fail`);
      assert.match(execution.stdout, /Consolidated Windows validation did not succeed/);
    }
    }
  }
});

test('common compilation, packaging, tests, and installer lifecycle run once per revision', () => {
  assert.doesNotMatch(producer, /\n    strategy:|matrix.language/);
  for (const command of [
    'Build-AiLyricsRuntime.ps1', 'dotnet test tests/DropSpace.Core.Tests/',
    'dotnet test tests/DropSpace.Infrastructure.Tests/', 'dotnet test tests/DropSpace.App.Tests/',
    'dotnet build src/DropSpace.App/', 'Build-PortableExe.ps1', 'Build-Installer.ps1',
    'Build-UnsignedPackage.ps1', 'Build-IdentityPackage.ps1', 'Test-InstallerLifecycle.ps1',
  ]) {
    assert.equal(ci.split(command).length - 1, 1, `${command} must run once`);
    assert.ok(producer.includes(command), `${command} belongs to the shared producer`);
    assert.ok(!locales.includes(command), `${command} must not run in each language check`);
  }
  for (const contract of ['test-ai-release-approval.test.mjs', 'test-ai-runtime-publication.test.mjs',
    'Test-AiRuntimePayloadInspector.ps1', 'Test-ReviewedAiRuntimeArchive.ps1', 'Test-ProductionEvidenceContract.ps1',
    'test-expanded-island-layout.test.mjs', 'test-fullscreen-island.test.mjs',
    'unittest discover -s tools/ct2-helper', '--contract-self-test']) {
    assert.ok(producer.includes(contract), `Removed Release PR lane's contract still needs PR coverage: ${contract}`);
  }
  assert.doesNotMatch(ci, /Get-AiLyricsSmokeModel|Run-WindowsProductionEvidence/);
});

test('both locales smoke the exact successful producer artifact and fail on byte/provenance mismatch', () => {
  assert.match(producer, /portable_artifact_id: \$\{\{ steps.portable-artifact.outputs.artifact-id \}\}/);
  assert.match(producer, /handoff_sha256: \$\{\{ steps.portable-handoff.outputs.handoff_sha256 \}\}/);
  assert.match(producer, /name: dropspace-portable-win-x64-\$\{\{ github.run_id \}\}-\$\{\{ github.run_attempt \}\}/);
  assert.match(locales, /artifact-ids: \$\{\{ needs.produce-windows.outputs.portable_artifact_id \}\}/);
  assert.match(locales, /EXPECTED_HANDOFF_SHA256: \$\{\{ needs.produce-windows.outputs.handoff_sha256 \}\}/);
  assert.match(locales, /PORTABLE_ARTIFACT_ID: \$\{\{ needs.produce-windows.outputs.portable_artifact_id \}\}/);
  assert.match(locales, /runs-on: windows-2025/);
  assert.doesNotMatch(locales, /strategy:|matrix.language/);
  assert.equal(locales.split('ci-portable-handoff.mjs verify').length - 1, 1);
  assert.match(locales, /Test-PortableSmoke.ps1 -Language en-US/);
  assert.match(locales, /Test-PortableSmoke.ps1 -Language zh-CN/);
  assert.ok(locales.indexOf('-Language en-US') < locales.indexOf('-Language zh-CN'));
  assert.match(locales, /id: smoke-zh\n        if: \$\{\{ !cancelled\(\) && steps.portable-verification.outcome == 'success' \}\}/);
  assert.ok(locales.indexOf('ci-portable-handoff.mjs verify') < locales.indexOf('Test-PortableSmoke.ps1'));
  assert.doesNotMatch(locales, /github-token:|run-id:|pattern:/);
});

test('share-worker remains a required job with complete-diff filtering and fail-safe test execution', () => {
  assert.doesNotMatch(worker.split('\n    steps:')[0], /\n    if:/);
  assert.match(worker, /fetch-depth: 2/);
  assert.match(worker, /run: node scripts\/ci-share-worker-scope.mjs/);
  assert.match(producer, /ci-share-worker-scope.test.mjs/);
  assert.match(worker, /!cancelled\(\).*steps.checkout.outcome == 'success'.*steps.scope.outcome != 'success' \|\| steps.scope.outputs.run_worker != 'false'/);
  assert.match(worker, /node --check src\/index.js\n          npm test/);
  assert.match(worker, /Require truthful share-worker outcome\n        if: \$\{\{ always\(\) \}\}/);
});

test('actual worker outcome gate accepts only executed success or verified not-applicable', t => {
  const gate = runBlock(worker, 'Require truthful share-worker outcome');
  for (const scopeResult of ['success', 'failure', 'skipped', '']) {
    for (const run of ['true', 'false', '']) {
      for (const complete of ['true', 'false', '']) {
        for (const result of ['success', 'failure', 'cancelled', 'skipped', '']) {
          const execution = bash(t, gate, { SCOPE_RESULT: scopeResult, RUN_WORKER: run, COMPLETE_DIFF: complete, TEST_RESULT: result });
          const executed = scopeResult === 'success' && run === 'true' && result === 'success';
          const skipped = scopeResult === 'success' && run === 'false' && complete === 'true' && result === 'skipped';
          assert.equal(execution.status === 0, executed || skipped, `${scopeResult}/${run}/${complete}/${result}`);
          if (skipped) {
            assert.match(execution.summary, /not-applicable.*No test pass is claimed/);
            assert.doesNotMatch(execution.summary, /executed successfully/);
          }
        }
      }
    }
  }
});

test('only proven duplicate asset generation and successful-run crash probes are removed', () => {
  for (const workflow of [ci, release]) {
    assert.match(workflow, /Generate-BrandAssets.ps1 -Verify/);
    assert.match(workflow, /Confirm brand verification did not mutate the checkout/);
    assert.doesNotMatch(workflow, /Generate brand assets from the verified/);
    assert.equal((workflow.match(/Generate-BrandAssets.ps1/g) ?? []).length, 1);
  }
  for (const name of ['Install .NET 10 SDK for isolated crash diagnostics', 'Collect isolated native crash address evidence']) {
    const step = locales.split(`      - name: ${name}\n`)[1].split(/\n      - name:/)[0];
    assert.match(step, /if:.*steps.smoke-en.outcome == 'failure' \|\| steps.smoke-zh.outcome == 'failure'/);
    assert.match(step, /steps.portable-verification.outcome == 'success'/);
  }
  assert.match(triggers(read('deploy-website.yml')), /release:\n    types: \[published\]/);
  assert.doesNotMatch(triggers(read('deploy-website.yml')), /released|prereleased/);
});

test('only explicit release dispatch builds a release and validation cannot block its queue', () => {
  assert.match(triggers(release), /\n  workflow_dispatch:/);
  assert.doesNotMatch(triggers(release), /\n  (?:push|pull_request):/);
  assert.match(release, /group: dropspace-release-.*inputs.publish == true && 'publish' \|\| 'validation'/);
  assert.match(release, /cancel-in-progress: false/);
  const validate = job(release, 'validate-release');
  assert.match(validate, /Retrieve the exact reviewed runtime[\s\S]*Get-ReviewedAiRuntime.ps1/);
  assert.match(validate, /Assert-DropSpacePublicationCommit \$env:EXPECTED_COMMIT \$env:ACTUAL_COMMIT/);
  assert.match(validate, /RELEASE_REF -ne "refs\/heads\/main"/);
  assert.match(validate, /RELEASE_ACTOR -ne \$env:REPOSITORY_OWNER/);
  assert.match(validate, /RELEASE_ACTOR -ne \$env:AUTOMATION_ACTOR/);
  const publish = job(release, 'publish-release');
  assert.match(publish, /inputs.publish == true/);
  assert.match(publish, /needs.validate-release.outputs.ai_publication_authorized == 'true'/);
  assert.match(publish, /Reject an existing tag or release/);
  assert.match(publish, /Recheck AI publication decision/);
  assert.match(publish, /gh workflow run deploy-website.yml --ref main/);
  assert.match(bridge, /github.event.created != true/);
  assert.match(bridge, /publish=true -f expected_commit="\$\{GITHUB_SHA\}"/);
});

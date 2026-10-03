import assert from 'node:assert/strict';
import fs from 'node:fs';
import { spawnSync } from 'node:child_process';
import test from 'node:test';

const read = name => fs.readFileSync(new URL(`../.github/workflows/${name}`, import.meta.url), 'utf8');
const ci = read('ci.yml'), release = read('release.yml'), bridge = read('publish-release-bridge.yml');
const job = (workflow, name) => workflow.split(`\n  ${name}:\n`)[1]?.split(/\n  [a-z][a-z-]+:\n/)[0];
const triggers = workflow => ('\n' + workflow.split('\non:\n')[1]).split('\nconcurrency:')[0].split('\npermissions:')[0];
const producer = job(ci, 'produce-windows'), locales = job(ci, 'build-and-test');

test('PR revisions have one Windows pipeline and always emit the required checks', () => {
  const events = triggers(ci);
  assert.match(events, /\n  push:\n    branches: \[main\]/);
  assert.match(events, /\n  pull_request:\n    branches: \[main\]/);
  assert.match(events, /\n  workflow_dispatch:/);
  assert.doesNotMatch(events, /agent\/|paths(?:-ignore)?:/);
  assert.match(locales, /name: Build and test \(x64, \$\{\{ matrix.language \}\}\)/);
  assert.match(locales, /language: \[en-US, zh-CN\]/);
  assert.match(job(ci, 'share-worker'), /name: Test encrypted share worker/);
});

test('failed, cancelled, and skipped producers cannot yield successful required checks', () => {
  assert.match(locales, /needs: produce-windows\n    if: \$\{\{ always\(\) \}\}/);
  assert.match(locales, /PRODUCER_RESULT: \$\{\{ needs.produce-windows.result \}\}/);
  assert.match(locales, /if \(\$env:PRODUCER_RESULT -cne 'success'\) \{\s+throw/);
  assert.ok(locales.indexOf('Require successful shared producer') < locales.indexOf('Check out source'));
  assert.doesNotMatch(producer + locales, /continue-on-error:/);
});

test('the actual required-check gate succeeds only for a successful producer', () => {
  const gate = locales.match(/        run: \|\n([\s\S]*?)\n\n/)[1].replace(/^          /gm, '');
  for (const result of ['success', 'failure', 'cancelled', 'skipped', '']) {
    const execution = spawnSync('pwsh', ['-NoLogo', '-NoProfile', '-Command', gate], {
      encoding: 'utf8', env: { ...process.env, PRODUCER_RESULT: result },
    });
    assert.ifError(execution.error);
    if (result === 'success') assert.equal(execution.status, 0);
    else {
      assert.notEqual(execution.status, 0, `${result || 'missing'} producer result must fail`);
      assert.match(execution.stderr, /Shared Windows producer did not succeed/);
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
  assert.match(locales, /Test-PortableSmoke.ps1 -Language \$\{\{ matrix.language \}\}/);
  assert.ok(locales.indexOf('ci-portable-handoff.mjs verify') < locales.indexOf('Test-PortableSmoke.ps1'));
  assert.doesNotMatch(locales, /github-token:|run-id:|pattern:/);
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

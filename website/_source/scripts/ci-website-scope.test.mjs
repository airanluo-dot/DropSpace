import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const workflow = fs.readFileSync(new URL('../../../.github/workflows/ci.yml', import.meta.url), 'utf8');
const block = workflow.match(/\n {10}node --input-type=module - <<'JS'\n([\s\S]*?)\n {10}JS(?:\n|$)/);
assert.ok(block, 'The application scope must retain its executable Node classifier');
const classifier = block[1].replace(/^ {10}/gm, '').replace(/^import .+;\n/gm, '');

function classify(names, {version = 'v0.3.1-beta.16', event = 'pull_request', base = 'a'.repeat(40), gitFailure} = {}) {
  const output = new Map();
  const calls = [];
  vm.runInNewContext(classifier, {
    fs: {
      readFileSync(name) {
        assert.equal(name, 'RELEASE_VERSION');
        return version;
      },
      appendFileSync(name, value) {
        output.set(name, (output.get(name) ?? '') + value);
      },
    },
    execFileSync(command, args) {
      assert.equal(command, 'git');
      calls.push(args);
      if (args[0] === gitFailure) throw new Error('Cannot inspect complete diff');
      if (args[0] === 'fetch') {
        assert.deepEqual(Array.from(args), ['fetch', '--no-tags', '--depth=1', 'origin', base]);
        return '';
      }
      assert.deepEqual(Array.from(args), ['diff', '--name-only', '-z', base, 'HEAD']);
      return names.join('\0') + (names.length ? '\0' : '');
    },
    process: {env: {BASE_SHA: base, GITHUB_EVENT_NAME: event, GITHUB_OUTPUT: 'output', GITHUB_STEP_SUMMARY: 'summary'}},
    console: {log() {}},
  });
  const routing = output.get('output');
  assert.match(routing, /^maintenance_only=(true|false)\n$/);
  return {maintenance: routing === 'maintenance_only=true\n', summary: output.get('summary'), calls};
}

test('actual workflow routes website-only changes without claiming application validation', () => {
  for (const names of [
    ['website/_source/src/index.html'],
    ['website/_source/src/index.html', 'website/_source/data/releases.json', '.github/workflows/ci.yml'],
  ]) {
    const result = classify(names);
    assert.equal(result.maintenance, true);
    assert.equal(result.summary, 'Website-only changes: no application binaries built, no application test pass claimed.\n');
  }
});

test('website changes mixed with application, packaging, update, or other workflow inputs require Windows validation', () => {
  for (const extra of [
    'src/DropSpace.App/app.manifest',
    'src/DropSpace.App/Package.appxmanifest',
    'installer/DropSpace.iss',
    'update-manifest.json',
    'scripts/Build-Installer.ps1',
    '.github/workflows/release.yml',
    'README.md',
  ]) {
    assert.equal(classify(['website/_source/src/index.html', extra]).maintenance, false, extra);
  }
});

test('corrective Beta application and CI-only changes retain the exact release route', () => {
  for (const version of ['v0.3.1-beta.15', 'v0.3.1-beta.16']) {
    for (const names of [['src/DropSpace.App/app.manifest'], ['.github/workflows/ci.yml'], []]) {
      assert.equal(classify(names, {version}).maintenance, false, `${version}: ${names}`);
    }
  }
  assert.equal(classify(['ROADMAP.md'], {version: 'v0.3.1-beta.14'}).maintenance, true);
});

test('unknown events, invalid base identities, and incomplete Git inspection fail closed', () => {
  const names = ['website/_source/src/index.html', '.github/workflows/ci.yml'];
  for (const options of [
    {event: 'workflow_dispatch'},
    {event: 'push'},
    {base: ''},
    {base: 'main'},
    {gitFailure: 'fetch'},
    {gitFailure: 'diff'},
  ]) {
    const result = classify(names, options);
    assert.equal(result.maintenance, false, JSON.stringify(options));
    assert.equal(result.summary, 'Application or unknown inputs changed: Windows producer required.\n');
    if (options.event || options.base !== undefined) assert.equal(result.calls.length, 0);
  }
});

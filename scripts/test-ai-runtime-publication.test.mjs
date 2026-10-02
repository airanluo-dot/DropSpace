import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { canonicalRuntimePath, validateInventory, directoryInventory, compareInventory, hashBytes,
  validateArtifactContract, validateArtifactMetadata, validateRuntimeProducer,
  fileIdentity, writeReleaseBinding, verifyReleaseBinding, releaseBindingName } from './ai-runtime-publication.mjs';

const repository = fileURLToPath(new URL('../', import.meta.url));
const now = Date.parse('2026-10-02T00:00:00Z');
const json = value => JSON.stringify(value, null, 2) + '\n';
const bytes = (name, value) => ({ path: name, sha256: hashBytes(value), bytes: Buffer.byteLength(value) });
// Backend-neutral synthetic fixtures deliberately do not claim model evaluation.
const inventory = () => [bytes('native/engine.dll', 'synthetic engine'), bytes('LICENSE', 'synthetic license'), bytes('runtime-manifest.json', '{}')];
function artifactFixture() {
  const contract = {
    schemaVersion: 1, repository: 'airanluo-dot/DropSpace', workflowPath: '.github/workflows/release.yml',
    runId: 123, runAttempt: 2, headCommit: 'a'.repeat(40), checkoutCommit: 'b'.repeat(40),
    artifactId: 456, artifactName: 'ai-candidate-runtime-123-2', archiveSha256: 'c'.repeat(64), files: inventory(),
  };
  const artifact = { id: 456, name: contract.artifactName, expired: false, expires_at: '2026-11-02T00:00:00Z',
    digest: `sha256:${contract.archiveSha256}`, workflow_run: { id: 123, head_sha: contract.headCommit } };
  const run = { id: 123, run_attempt: 2, head_sha: contract.headCommit, path: contract.workflowPath,
    repository: { full_name: contract.repository }, head_repository: { full_name: contract.repository }, status: 'completed', conclusion: 'success' };
  return { contract, artifact, run };
}

test('backend-neutral inventory supports canonical nested files and deterministic order', () => {
  const result = validateInventory(inventory());
  assert.deepEqual(result.map(file => file.path), ['LICENSE', 'native/engine.dll', 'runtime-manifest.json']);
  compareInventory([...result].reverse(), result);
});
for (const name of ['../engine.dll', 'a/../b', '/absolute', 'a//b', 'a\\b', 'C:/engine.dll', './x', 'a/', 'a/NUL.txt', 'a/COM1', 'a/trailing.', 'space name', 'a\u0000b']) {
  test(`rejects unsafe archive/runtime path ${JSON.stringify(name)}`, () => assert.throws(() => canonicalRuntimePath(name), /canonical/));
}
for (const [label, change] of [
  ['empty inventory', x => x.splice(0)], ['case-insensitive duplicate', x => x.push({ ...x[0], path: 'NATIVE/ENGINE.DLL' })],
  ['missing hash', x => delete x[0].sha256], ['invalid hash', x => x[0].sha256 = 'f'],
  ['zero bytes', x => x[0].bytes = 0], ['fractional bytes', x => x[0].bytes = 1.5], ['overlarge payload', x => x[0].bytes = 2 * 1024 ** 3 + 1],
]) test(`rejects ${label}`, () => { const files = inventory(); change(files); assert.throws(() => validateInventory(files)); });

test('directory inventory checks the actual bytes and rejects links and extra files', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'runtime-inventory-test-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  fs.mkdirSync(path.join(root, 'native'));
  fs.writeFileSync(path.join(root, 'native/engine.dll'), 'synthetic engine');
  fs.writeFileSync(path.join(root, 'LICENSE'), 'synthetic license');
  fs.writeFileSync(path.join(root, 'runtime-manifest.json'), '{}');
  compareInventory(directoryInventory(root), inventory());
  fs.writeFileSync(path.join(root, 'LICENSE'), 'changed license');
  assert.throws(() => compareInventory(directoryInventory(root), inventory()), /exact reviewed/);
  fs.writeFileSync(path.join(root, 'LICENSE'), 'synthetic license');
  fs.writeFileSync(path.join(root, 'extra.dll'), 'extra');
  assert.throws(() => compareInventory(directoryInventory(root), inventory()), /exact reviewed/);
  fs.unlinkSync(path.join(root, 'extra.dll'));
  try { fs.symlinkSync(path.join(root, 'LICENSE'), path.join(root, 'link')); }
  catch (error) { if (error.code === 'EPERM') { t.diagnostic('Link fixture unavailable on this host'); return; } throw error; }
  assert.throws(() => directoryInventory(root), /links/);
});

test('exact same-repository successful runtime artifact passes provenance checks', () => {
  const x = artifactFixture(); validateArtifactMetadata(x.contract, x.artifact, x.run, now);
  validateRuntimeProducer({ producer: Object.fromEntries(['repository','workflowPath','runId','runAttempt','headCommit','checkoutCommit'].map(k => [k, x.contract[k]])) }, x.contract);
});
for (const [label, mutate] of [
  ['missing contract', x => x.contract = undefined], ['foreign repository', x => x.contract.repository = 'other/repo'],
  ['untrusted workflow', x => x.contract.workflowPath = '.github/workflows/other.yml'],
  ['missing checkout commit', x => delete x.contract.checkoutCommit], ['missing source head', x => delete x.contract.headCommit],
  ['missing archive digest', x => delete x.contract.archiveSha256], ['wrong attempt name', x => x.contract.artifactName = 'ai-candidate-runtime-123-1'],
  ['invalid artifact ID', x => x.contract.artifactId = 0], ['missing inventory', x => delete x.contract.files],
  ['wrong artifact ID', x => x.artifact.id++], ['wrong artifact name', x => x.artifact.name += '-wrong'],
  ['expired artifact', x => x.artifact.expired = true], ['past expiry', x => x.artifact.expires_at = '2026-10-01T00:00:00Z'],
  ['missing expiry', x => delete x.artifact.expires_at], ['wrong ZIP hash', x => x.artifact.digest = 'sha256:' + '0'.repeat(64)],
  ['wrong artifact run', x => x.artifact.workflow_run.id++], ['wrong artifact source', x => x.artifact.workflow_run.head_sha = 'e'.repeat(40)],
  ['wrong run ID', x => x.run.id++], ['wrong attempt', x => x.run.run_attempt++], ['wrong source', x => x.run.head_sha = 'e'.repeat(40)],
  ['wrong repository', x => x.run.repository.full_name = 'other/repo'], ['forked source', x => x.run.head_repository.full_name = 'other/repo'],
  ['wrong producer workflow', x => x.run.path = '.github/workflows/other.yml'], ['unfinished run', x => x.run.status = 'in_progress'],
  ['failed run', x => x.run.conclusion = 'failure'],
]) test(`artifact retrieval rejects ${label}`, () => { const x = artifactFixture(); mutate(x); assert.throws(() => validateArtifactMetadata(x.contract, x.artifact, x.run, now)); });

function bundleFixture(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'runtime-publication-test-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  for (const name of ['DropSpace.exe', 'DropSpace-x64.msix', 'DropSpaceSetup.exe']) fs.writeFileSync(path.join(directory, name), `synthetic ${name}`);
  const identity = name => fileIdentity(path.join(directory, name));
  const sourceCommit = 'd'.repeat(40), runtimeFiles = inventory();
  const portable = { schemaVersion: 1, package: identity('DropSpace.exe'), files: runtimeFiles };
  const msix = { schemaVersion: 1, package: identity('DropSpace-x64.msix'), files: runtimeFiles };
  const installer = { schemaVersion: 1, kind: 'installer-payload', package: identity('DropSpaceSetup.exe'), installedPortable: portable.package };
  const data = { runtimeFiles, sourceCommit, portable, msix, installer };
  writeReleaseBinding(directory, data);
  return { directory, data, sourceCommit, save: value => fs.writeFileSync(path.join(directory, releaseBindingName), json(value)), read: () => JSON.parse(fs.readFileSync(path.join(directory, releaseBindingName), 'utf8')) };
}
test('final bundle binding verifies inspected portable/MSIX and installed installer payload', t => {
  const x = bundleFixture(t);
  verifyReleaseBinding(x.directory, { expectedInventory: inventory(), expectedCommit: x.sourceCommit });
});
for (const name of ['DropSpace.exe', 'DropSpace-x64.msix', 'DropSpaceSetup.exe']) test(`rejects final ${name} changed after inspection`, t => {
  const x = bundleFixture(t); fs.appendFileSync(path.join(x.directory, name), 'mutated');
  assert.throws(() => verifyReleaseBinding(x.directory), /Final release package changed/);
});
for (const [label, mutate] of [
  ['missing portable inspection', x => delete x.portable], ['missing MSIX inspection', x => delete x.msix],
  ['missing installer inspection', x => delete x.installer], ['missing source commit', x => delete x.sourceCommit],
  ['different embedded runtime', x => x.portable.files = [bytes('foreign.dll', 'foreign')]],
  ['different MSIX runtime', x => x.msix.files = [bytes('foreign.dll', 'foreign')]],
  ['different installer payload', x => x.installer.installedPortable.sha256 = 'e'.repeat(64)],
  ['wrong installer kind', x => x.installer.kind = 'unchecked'], ['mislabeled package', x => x.msix.package.name = 'DropSpace.exe'],
]) test(`rejects ${label} in recorded binding`, t => {
  const x = bundleFixture(t), binding = x.read(); mutate(binding); x.save(binding);
  assert.throws(() => verifyReleaseBinding(x.directory));
});
test('the final gate rejects a structurally consistent but unreviewed inventory', t => {
  const x = bundleFixture(t);
  assert.throws(() => verifyReleaseBinding(x.directory, { expectedInventory: [bytes('another-engine.dll', 'other')] }), /exact reviewed/);
  assert.throws(() => verifyReleaseBinding(x.directory, { expectedCommit: 'a'.repeat(40) }), /source commit mismatch/);
});
test('signing preserves runtime identity but requires fresh final package inspections', t => {
  const x = bundleFixture(t);
  fs.appendFileSync(path.join(x.directory, 'DropSpace.exe'), 'signed');
  assert.throws(() => writeReleaseBinding(x.directory, x.data), /Final release package changed/);
  x.data.portable.package = fileIdentity(path.join(x.directory, 'DropSpace.exe'));
  assert.throws(() => writeReleaseBinding(x.directory, x.data), /different portable payload/);
  x.data.installer.installedPortable = x.data.portable.package;
  writeReleaseBinding(x.directory, x.data);
  verifyReleaseBinding(x.directory, { expectedInventory: inventory() });
});
test('publication utility rejects unknown CLI arguments', () => {
  const result = spawnSync(process.execPath, ['scripts/ai-runtime-publication.mjs', '--skip'], { cwd: repository, encoding: 'utf8' });
  assert.equal(result.status, 1); assert.match(result.stderr, /Usage/);
});
test('Git preserves exact evidence CRLF bytes without changing ordinary JSON attributes', () => {
  const input = Buffer.from('synthetic raw evidence\r\nwith original newlines\r\n');
  const filtered = spawnSync('git', ['hash-object', '--path=scripts/ai-model-qa/evidence/synthetic.json', '--stdin'], { cwd: repository, input, encoding: 'utf8' });
  const raw = spawnSync('git', ['hash-object', '--no-filters', '--stdin'], { cwd: repository, input, encoding: 'utf8' });
  assert.equal(filtered.status, 0); assert.equal(raw.status, 0); assert.equal(filtered.stdout, raw.stdout);
  const attributes = spawnSync('git', ['check-attr', 'text', '--', 'scripts/ai-model-qa/release-approval.json', 'scripts/ai-model-qa/inputs/source48.json'], { cwd: repository, encoding: 'utf8' });
  assert.equal(attributes.status, 0);
  assert.match(attributes.stdout, /release-approval.json: text: set/);
  assert.match(attributes.stdout, /source48.json: text: set/);
});

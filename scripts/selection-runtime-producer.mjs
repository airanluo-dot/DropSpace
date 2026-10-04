// Diagnostic identity only. Never writes an approval or a release decision.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { directoryInventory, hashBytes, releaseRepository, runtimeProducerWorkflow } from './ai-runtime-publication.mjs';
import { readScope } from './test-ai-release-approval.mjs';
import { execFileSync } from 'node:child_process';
const root = process.cwd();
const directory = path.join(root, 'artifacts/ai-runtime/win-x64');
const old = path.join(root, 'artifacts/verified-legacy-runtime');
const read = p => JSON.parse(fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, ''));
const manifestPath = path.join(directory, 'runtime-manifest.json');
const manifest = read(manifestPath), previous = read(path.join(old, 'runtime-manifest.json'));
assert.equal(process.env.GITHUB_REPOSITORY, releaseRepository);
assert.equal(process.env.GITHUB_WORKFLOW_REF?.split('@')[0], `${releaseRepository}/${runtimeProducerWorkflow}`);
const checkoutCommit = execFileSync('git', ['rev-parse', 'HEAD'], {encoding:'utf8'}).trim();
assert.equal(checkoutCommit, process.env.GITHUB_SHA);
const producer = {
  repository: releaseRepository, workflowPath: runtimeProducerWorkflow,
  runId: Number(process.env.GITHUB_RUN_ID), runAttempt: Number(process.env.GITHUB_RUN_ATTEMPT),
  headCommit: process.env.GITHUB_SHA, checkoutCommit,
};
assert.ok(Number.isSafeInteger(producer.runId) && producer.runId > 0 && Number.isSafeInteger(producer.runAttempt) && producer.runAttempt > 0);
const inventory = directoryInventory(directory), legacyInventory = directoryInventory(old);
const reused = ['llama-completion.exe', 'llama-completion-avx2.exe', 'llama-tokenize.exe'];
for (const name of reused) {
  assert.deepEqual(inventory.find(f => f.path === name), legacyInventory.find(f => f.path === name));
}
for (const key of ['schemaVersion', 'runtimeId', 'sourceRepository', 'sourceCommit', 'executable', 'sha256', 'bytes', 'tokenizer', 'avx2', 'build'])
  assert.deepEqual(manifest[key], previous[key], `Preserved runtime identity differs: ${key}`);
assert.equal(manifest.resident.protocol, previous.resident.protocol);
assert.equal(manifest.resident.profile, previous.resident.profile);
assert.equal(inventory.length, 8);
manifest.producer = producer;
fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + '\n');
const scope = readScope(root);
const supportFiles = ['.github/workflows/release.yml', 'scripts/Build-AiLyricsRuntime.ps1', 'scripts/selection-runtime-producer.mjs', 'scripts/selection-model-probe/Program.cs', 'scripts/selection-model-probe/SelectionModelProbe.csproj', 'scripts/selection-model-probe/packages.lock.json'].map(name => ({ path: name, sha256: hashBytes(fs.readFileSync(path.join(root, name))) }));
fs.writeFileSync(path.join(root, 'artifacts/selection-runtime-producer.json'), JSON.stringify({
  schemaVersion: 1, diagnosticOnly: true, publicationAuthorized: false, producer, supportFiles,
  legacyProducer: previous.producer, reusedFiles: legacyInventory.filter(f => reused.includes(f.path)),
  sourceFingerprintSha256: scope.sources.sha256,
  residentSourceSha256: manifest.resident.sourceSha256,
  runtimeManifestSha256: hashBytes(fs.readFileSync(manifestPath)), files: directoryInventory(directory),
}, null, 2) + '\n');

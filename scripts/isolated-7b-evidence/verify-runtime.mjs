import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const [root, directory, artifactPath, runPath, expectedCommit, runId, runAttempt, artifactId, archiveSha256, contractPath] = process.argv.slice(2);
assert.equal(process.argv.length, 12, 'Expected ten explicit arguments');
const read = p => JSON.parse(fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, ''));
const api = await import(pathToFileURL(path.join(root, 'scripts/ai-runtime-publication.mjs')));
const artifact = read(artifactPath);
const run = read(runPath);
assert.match(expectedCommit, /^[a-f0-9]{40}$/);
assert.match(archiveSha256, /^[a-f0-9]{64}$/);
const files = api.directoryInventory(directory);
assert.deepEqual(files.map(f => f.path).sort(), [
  'LICENSE-llama.cpp', 'llama-completion-avx2.exe', 'llama-completion.exe', 'llama-tokenize.exe',
  'plain-lyrics-worker-avx2.exe', 'plain-lyrics-worker-vulkan.exe', 'plain-lyrics-worker.exe', 'runtime-manifest.json',
].sort());
const contract = {
  schemaVersion: 1,
  repository: 'airanluo-dot/DropSpace',
  workflowPath: '.github/workflows/release.yml',
  runId: Number(runId), runAttempt: Number(runAttempt),
  headCommit: expectedCommit, checkoutCommit: expectedCommit,
  artifactId: Number(artifactId),
  artifactName: `ai-candidate-runtime-${runId}-${runAttempt}`,
  archiveSha256, files,
};
api.validateArtifactMetadata(contract, artifact, run);
api.validateRuntimeProducer(read(path.join(directory, 'runtime-manifest.json')), contract);
assert.equal(run.event === 'workflow_dispatch' || run.event === 'push', true,
  'This isolated proposal accepts exact-head producers only, not a PR merge checkout');
assert.equal(run.head_sha, expectedCommit);
fs.writeFileSync(contractPath, JSON.stringify(contract, null, 2) + '\n', { flag: 'wx' });
console.log('Exact runtime producer, successful attempt, artifact digest and manifest inventory verified.');

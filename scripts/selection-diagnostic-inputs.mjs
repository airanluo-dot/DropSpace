// One immutable diagnostic input set. No publication contract or approval exception.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { compareInventory, directoryInventory, hashBytes } from './ai-runtime-publication.mjs';
const [mode, directory] = process.argv.slice(2);
assert.ok(['metadata', 'payload'].includes(mode) && directory);
const read = file => JSON.parse(fs.readFileSync(path.join(directory, file), 'utf8').replace(/^\uFEFF/, ''));
const run = read('run.json');
const producer = { repository:'airanluo-dot/DropSpace', workflowPath:'.github/workflows/release.yml', runId:37235696457,
  runAttempt:1, headCommit:'e81e0d55f248afbc865cf17b3f93041a4e7d12a4', checkoutCommit:'e81e0d55f248afbc865cf17b3f93041a4e7d12a4' };
assert.equal(run.id, producer.runId); assert.equal(run.run_attempt, 1); assert.equal(run.head_sha, producer.headCommit);
assert.equal(run.repository.full_name, producer.repository); assert.equal(run.head_repository.full_name, producer.repository);
assert.equal(run.path, producer.workflowPath); assert.equal(run.status, 'completed'); assert.equal(run.conclusion, 'success');
for (const [name, id, digest, bytes] of [
 ['runtime',11316610580,'344c7aa9016e2f317b6fb157d8bca2e6103c1b1be1ce88fb450816f46589733b',30701589],
 ['evidence',11316152198,'8152aa0e06c0ea7101c06a97fcf2e2e0d57ceba20cc4f4ac5048d2a0c2eaecbd',6720]]) {
  const artifact = read(`${name}-artifact.json`);
  assert.equal(artifact.id, id); assert.equal(artifact.name, `ai-selection-${name}-37235696457-1`);
  assert.equal(artifact.digest, `sha256:${digest}`); assert.equal(artifact.size_in_bytes, bytes);
  assert.equal(artifact.expired, false); assert.ok(Date.parse(artifact.expires_at) > Date.now());
  assert.equal(artifact.workflow_run.id, producer.runId); assert.equal(artifact.workflow_run.head_sha, producer.headCommit);
  if (mode === 'payload') {
    const zip = fs.readFileSync(path.join(directory, `${name}.zip`));
    assert.equal(zip.length, bytes); assert.equal(hashBytes(zip), digest);
  }
}
if (mode === 'payload') {
  const receipt = read('evidence/selection-runtime-producer.json');
  assert.deepEqual(receipt.producer, producer);
  compareInventory(directoryInventory(path.join(directory, 'runtime')), receipt.files, 'Diagnostic runtime');
  assert.equal(hashBytes(fs.readFileSync(path.join(directory, 'runtime/runtime-manifest.json'))), '26162299cd270bf63f25181e960b3139cfe20421eecdc9da44cea99dfb1711ba');
  assert.deepEqual(read('runtime/runtime-manifest.json').producer, producer);
  assert.equal(receipt.runtimeManifestSha256, '26162299cd270bf63f25181e960b3139cfe20421eecdc9da44cea99dfb1711ba');
}
console.log(`Immutable diagnostic ${mode} verified. Original runtime producer retained; no publication authorization.`);

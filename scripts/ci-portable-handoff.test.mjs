import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { handoffName, recordHandoff, verifyHandoff } from './ci-portable-handoff.mjs';

function fixture(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ci-handoff-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  fs.writeFileSync(path.join(directory, 'DropSpace.exe'), 'synthetic portable payload');
  const env = {
    GITHUB_REPOSITORY: 'airanluo-dot/DropSpace', GITHUB_SHA: 'a'.repeat(40),
    GITHUB_RUN_ID: '123', GITHUB_RUN_ATTEMPT: '2', PORTABLE_ARTIFACT_ID: '456',
    GITHUB_WORKFLOW_REF: 'airanluo-dot/DropSpace/.github/workflows/ci.yml@refs/pull/42/merge',
  };
  env.EXPECTED_HANDOFF_SHA256 = recordHandoff(directory, env);
  return { directory, env };
}

test('both language consumers verify the same producer bytes without rewriting provenance', t => {
  const { directory, env } = fixture(t);
  const before = fs.readFileSync(path.join(directory, handoffName));
  const first = verifyHandoff(directory, env), second = verifyHandoff(directory, env);
  assert.deepEqual(first, second);
  assert.deepEqual(first.producer, first.consumer);
  assert.equal(first.artifactId, '456');
  assert.deepEqual(fs.readFileSync(path.join(directory, handoffName)), before);
});

for (const [label, mutate] of [
  ['changed EXE bytes', x => fs.appendFileSync(path.join(x.directory, 'DropSpace.exe'), 'changed')],
  ['missing EXE', x => fs.unlinkSync(path.join(x.directory, 'DropSpace.exe'))],
  ['empty EXE', x => fs.writeFileSync(path.join(x.directory, 'DropSpace.exe'), '')],
  ['missing handoff', x => fs.unlinkSync(path.join(x.directory, handoffName))],
  ['changed handoff', x => fs.appendFileSync(path.join(x.directory, handoffName), ' ')],
  ['missing digest', x => delete x.env.EXPECTED_HANDOFF_SHA256],
  ['wrong digest', x => x.env.EXPECTED_HANDOFF_SHA256 = 'b'.repeat(64)],
  ['missing artifact ID', x => delete x.env.PORTABLE_ARTIFACT_ID],
  ['invalid artifact ID', x => x.env.PORTABLE_ARTIFACT_ID = '0'],
  ['different run', x => x.env.GITHUB_RUN_ID = '124'],
  ['different attempt', x => x.env.GITHUB_RUN_ATTEMPT = '3'],
  ['different commit', x => x.env.GITHUB_SHA = 'b'.repeat(40)],
  ['different repository', x => x.env.GITHUB_REPOSITORY = 'other/repo'],
  ['different workflow', x => x.env.GITHUB_WORKFLOW_REF = 'airanluo-dot/DropSpace/.github/workflows/release.yml@refs/heads/main'],
]) test(`handoff rejects ${label}`, t => {
  const x = fixture(t); mutate(x); assert.throws(() => verifyHandoff(x.directory, x.env));
});

for (const [label, mutate] of [
  ['unsupported schema', x => x.schemaVersion++],
  ['relabelled producer commit', x => x.producer.sourceCommit = 'b'.repeat(40)],
  ['relabelled package', x => x.portable.name = 'DropSpaceSetup.exe'],
  ['different package size', x => x.portable.bytes++],
]) test(`handoff rejects ${label} even with a matching handoff digest`, t => {
  const { directory, env } = fixture(t);
  const filename = path.join(directory, handoffName);
  const handoff = JSON.parse(fs.readFileSync(filename, 'utf8'));
  mutate(handoff);
  const bytes = JSON.stringify(handoff);
  fs.writeFileSync(filename, bytes);
  env.EXPECTED_HANDOFF_SHA256 = createHash('sha256').update(bytes).digest('hex');
  assert.throws(() => verifyHandoff(directory, env));
});

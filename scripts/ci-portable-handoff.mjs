// Same-run CI handoff only. This is not a cache or a cross-commit release binding.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

export const handoffName = 'ci-portable-handoff.json';
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const positiveInteger = value => /^[1-9][0-9]*$/.test(value ?? '');

export function runIdentity(env) {
  assert.match(env.GITHUB_REPOSITORY ?? '', /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/, 'Repository is required');
  assert.match(env.GITHUB_SHA ?? '', /^[a-f0-9]{40}$/, 'Source commit is required');
  assert.ok(positiveInteger(env.GITHUB_RUN_ID), 'Run ID is required');
  assert.ok(positiveInteger(env.GITHUB_RUN_ATTEMPT), 'Run attempt is required');
  assert.ok(env.GITHUB_WORKFLOW_REF?.startsWith(`${env.GITHUB_REPOSITORY}/.github/workflows/ci.yml@`), 'CI workflow identity is required');
  return {
    repository: env.GITHUB_REPOSITORY, workflowPath: '.github/workflows/ci.yml',
    runId: env.GITHUB_RUN_ID, runAttempt: env.GITHUB_RUN_ATTEMPT, sourceCommit: env.GITHUB_SHA,
  };
}

function portableIdentity(directory) {
  const filename = path.join(directory, 'DropSpace.exe');
  const stat = fs.lstatSync(filename);
  assert.ok(stat.isFile() && stat.size > 0, 'A nonempty portable EXE is required');
  return { name: 'DropSpace.exe', bytes: stat.size, sha256: sha256(fs.readFileSync(filename)) };
}

export function recordHandoff(directory, env) {
  const handoff = { schemaVersion: 1, producer: runIdentity(env), portable: portableIdentity(directory) };
  const bytes = JSON.stringify(handoff, null, 2) + '\n';
  fs.writeFileSync(path.join(directory, handoffName), bytes);
  return sha256(bytes);
}

export function verifyHandoff(directory, env) {
  assert.match(env.EXPECTED_HANDOFF_SHA256 ?? '', /^[a-f0-9]{64}$/, 'Producer handoff digest is required');
  assert.ok(positiveInteger(env.PORTABLE_ARTIFACT_ID), 'Exact producer artifact ID is required');
  const filename = path.join(directory, handoffName);
  assert.ok(fs.lstatSync(filename).isFile(), 'A handoff file is required');
  const bytes = fs.readFileSync(filename);
  assert.equal(sha256(bytes), env.EXPECTED_HANDOFF_SHA256, 'Producer handoff digest mismatch');
  const handoff = JSON.parse(bytes);
  assert.equal(handoff.schemaVersion, 1, 'Unsupported handoff schema');
  const consumer = runIdentity(env);
  assert.deepEqual(handoff.producer, consumer, 'Portable handoff must come from this exact run, attempt, and checkout');
  assert.deepEqual(handoff.portable, portableIdentity(directory), 'Portable bytes changed after the producer validated them');
  return { producer: handoff.producer, consumer, artifactId: env.PORTABLE_ARTIFACT_ID, portable: handoff.portable };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const [command, directory, ...extra] = process.argv.slice(2);
    assert.ok(['record', 'verify'].includes(command) && directory && !extra.length, 'Usage: ci-portable-handoff.mjs record|verify DIRECTORY');
    const checkout = spawnSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' });
    assert.equal(checkout.status, 0, 'Checkout identity is unavailable');
    assert.equal(checkout.stdout.trim(), process.env.GITHUB_SHA, 'Checkout must match the current CI commit');
    if (command === 'record') {
      assert.ok(process.env.GITHUB_OUTPUT, 'The producer output channel is required');
      const digest = recordHandoff(directory, process.env);
      fs.appendFileSync(process.env.GITHUB_OUTPUT, `handoff_sha256=${digest}\n`);
    } else {
      const lineage = verifyHandoff(directory, process.env);
      console.log(JSON.stringify(lineage, null, 2));
      if (process.env.GITHUB_STEP_SUMMARY) {
        fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY,
          `Shared portable EXE verified (artifact ${lineage.artifactId}).\n\n` +
          `Original producer: ${JSON.stringify(lineage.producer)}\n\n` +
          `Current consumer: ${JSON.stringify(lineage.consumer)}\n\n` +
          `Portable SHA256: ${lineage.portable.sha256}\n`);
      }
    }
  } catch (error) {
    console.error(`CI portable handoff rejected: ${error.message}`);
    process.exitCode = 1;
  }
}

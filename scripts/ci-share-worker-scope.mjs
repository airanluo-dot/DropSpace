import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const shaPattern = /^[a-f0-9]{40}$/;
export const limits = Object.freeze({ eventBytes: 2 * 1024 * 1024, diffBytes: 4 * 1024 * 1024, paths: 100_000, gitMilliseconds: 30_000 });
const sharedInputs = new Set(['package.json', 'package-lock.json', 'npm-shrinkwrap.json', 'pnpm-lock.yaml', 'yarn.lock', 'bun.lock', 'bun.lockb',
  '.npmrc', '.node-version', '.nvmrc', '.gitattributes', '.gitmodules', '.github/dependabot.yml', '.github/workflows/ci.yml',
  'scripts/ci-share-worker-scope.mjs', 'scripts/ci-share-worker-scope.test.mjs']);

export function affectsShareWorker(name) {
  return name.startsWith('share-worker/') && name !== 'share-worker/README.md' || sharedInputs.has(name) ||
    name.startsWith('.github/actions/') || /^\.github\/workflows\/[^/]*share[^/]*\.ya?ml$/i.test(name) ||
    name.startsWith('src/DropSpace.Infrastructure/Sharing/') ||
    /^src\/DropSpace\.(?:Core|App)\/.*\/I?Shar[^/]*\.cs$/.test(name);
}

export function parseDiff(bytes) {
  assert.ok(Buffer.isBuffer(bytes) && bytes.length <= limits.diffBytes, 'Diff exceeds its bound');
  if (!bytes.length) return [];
  assert.equal(bytes.at(-1), 0, 'Truncated path list');
  const names = new TextDecoder('utf-8', { fatal: true }).decode(bytes).slice(0, -1).split('\0');
  assert.ok(names.length <= limits.paths, 'Too many changed paths');
  assert.ok(names.every(name => name && !name.startsWith('/') && !name.includes('\\') && !name.split('/').some(part => part === '..' || part === '.')), 'Invalid repository path');
  return names;
}

export function endpoints(eventName, event, checkoutSha) {
  assert.match(checkoutSha ?? '', shaPattern);
  let base;
  if (eventName === 'pull_request') {
    assert.match(event.pull_request?.head?.sha ?? '', shaPattern);
    base = event.pull_request?.base?.sha;
  } else if (eventName === 'push') {
    assert.equal(event.deleted, false, 'Deleted ref');
    assert.equal(event.after, checkoutSha, 'Push checkout differs');
    base = event.before;
  } else throw new Error('No complete event diff');
  assert.match(base ?? '', shaPattern);
  assert.notEqual(base, '0'.repeat(40), 'New ref has no comparison base');
  // For a PR, compare the base tree with the actual tested merge checkout.
  // This includes merge resolutions, not a truncated API/event file list.
  return { base, head: checkoutSha };
}

function git(args) {
  const result = spawnSync('git', args, { timeout: limits.gitMilliseconds, maxBuffer: limits.diffBytes });
  assert.ifError(result.error);
  assert.equal(result.status, 0, 'Git comparison unavailable');
  return result.stdout;
}

export function readGitDiff({ base, head }, execute = git) {
  for (const commit of [base, head]) {
    try { execute(['cat-file', '-e', `${commit}^{commit}`]); }
    catch {
      // Fetch exactly one missing commit/tree, with no tags or unbounded history.
      execute(['fetch', '--no-tags', '--depth=1', 'origin', commit]);
      execute(['cat-file', '-e', `${commit}^{commit}`]);
    }
  }
  return execute(['diff', '--name-only', '--no-renames', '-z', base, head, '--']);
}

export function determineScope(eventName, event, checkoutSha, read = readGitDiff) {
  try {
    const range = endpoints(eventName, event, checkoutSha);
    const names = parseDiff(read(range));
    const related = names.filter(affectsShareWorker);
    return { run: related.length > 0, complete: true, ...range, changedPaths: names.length, relatedPaths: related.length,
      reason: related.length ? 'related inputs changed' : 'complete diff contains no related inputs' };
  } catch {
    // Unknown, oversized, malformed, timed-out or unavailable diffs require tests.
    return { run: true, complete: false, reason: 'complete change scope unavailable; execute worker tests' };
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  let scope;
  try {
    assert.ok(process.env.GITHUB_OUTPUT && process.env.GITHUB_STEP_SUMMARY, 'CI output channels are required');
    assert.match(process.env.GITHUB_SHA ?? '', shaPattern);
    assert.equal(git(['rev-parse', 'HEAD']).toString('utf8').trim(), process.env.GITHUB_SHA, 'Checkout identity differs');
    try {
      const filename = process.env.GITHUB_EVENT_PATH;
      assert.ok(fs.statSync(filename).size <= limits.eventBytes, 'Event exceeds its bound');
      scope = determineScope(process.env.GITHUB_EVENT_NAME, JSON.parse(fs.readFileSync(filename, 'utf8')), process.env.GITHUB_SHA);
    } catch { scope = { run: true, complete: false, reason: 'event unavailable; execute worker tests' }; }
  } catch {
    scope = { run: true, complete: false, reason: 'checkout/output identity unavailable; execute tests and fail the scope check' };
    process.exitCode = 1;
  }
  if (process.env.GITHUB_OUTPUT) fs.appendFileSync(process.env.GITHUB_OUTPUT, `run_worker=${scope.run}\ncomplete_diff=${scope.complete}\n`);
  if (process.env.GITHUB_STEP_SUMMARY) fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY,
    `### Encrypted share worker scope\n\n${scope.run ? 'Tests required' : 'Not applicable: no worker tests will run'}: ${scope.reason}.\n\n` +
    (scope.complete ? `Complete tree diff: ${scope.base} → ${scope.head}; ${scope.changedPaths} changed paths, ${scope.relatedPaths} related.\n` : 'No incomplete path list is used to skip tests.\n'));
  console.log(JSON.stringify(scope));
}

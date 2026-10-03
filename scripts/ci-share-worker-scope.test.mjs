import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { affectsShareWorker, determineScope, endpoints, limits, parseDiff, readGitDiff } from './ci-share-worker-scope.mjs';

const base = 'a'.repeat(40), head = 'b'.repeat(40);
const push = (before = base, after = head) => ({ before, after, deleted: false });
const names = paths => Buffer.from(paths.length ? paths.join('\0') + '\0' : '');
const script = fileURLToPath(new URL('./ci-share-worker-scope.mjs', import.meta.url));

function repository(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'dropspace-worker-scope-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const git = args => {
    const result = spawnSync('git', args, { cwd: root, timeout: limits.gitMilliseconds, maxBuffer: limits.diffBytes });
    assert.ifError(result.error);
    assert.equal(result.status, 0, result.stderr.toString());
    return result.stdout;
  };
  git(['init', '-q']);
  git(['config', 'user.email', 'scope-test@example.invalid']);
  git(['config', 'user.name', 'Scope Test']);
  const write = (name, value = 'test\n') => {
    fs.mkdirSync(path.dirname(path.join(root, name)), { recursive: true });
    fs.writeFileSync(path.join(root, name), value);
  };
  const commit = () => {
    git(['add', '-A']);
    git(['commit', '-qm', 'scope fixture']);
    return git(['rev-parse', 'HEAD']).toString().trim();
  };
  return { root, git, write, commit };
}

test('worker implementation, dependencies, workflows and shared protocol changes require tests', () => {
  for (const name of ['share-worker/src/index.js', 'share-worker/package-lock.json', 'share-worker/wrangler.toml',
    'share-worker/test/coordinator.test.js', 'package-lock.json', '.npmrc', '.gitattributes',
    '.github/workflows/ci.yml', '.github/workflows/test-share.yml', '.github/actions/node/action.yml',
    'scripts/ci-share-worker-scope.mjs', 'scripts/ci-share-worker-scope.test.mjs',
    'src/DropSpace.Infrastructure/Sharing/ShareCoordinator.cs', 'src/DropSpace.Core/Models/ShareSession.cs',
    'src/DropSpace.Core/Abstractions/IShareCoordinator.cs', 'src/DropSpace.App/Services/SharingUseCase.cs']) {
    assert.equal(affectsShareWorker(name), true, name);
  }
  for (const name of ['share-worker/README.md', 'docs/release.md', 'website/index.html',
    'src/DropSpace.App/Services/WindowsMediaSessionService.cs']) assert.equal(affectsShareWorker(name), false, name);
});

test('PR scope compares base with the actual tested merge tree, while push uses its complete range', () => {
  assert.deepEqual(endpoints('pull_request', { pull_request: { base: { sha: base }, head: { sha: 'c'.repeat(40) } } }, head), { base, head });
  assert.deepEqual(endpoints('push', push(), head), { base, head });
  const scope = determineScope('push', push(), head, () => names(['docs/readme.md']));
  assert.equal(scope.run, false);
  assert.equal(scope.complete, true);
});

test('real multi-commit Git diff covers over 300 files, deleted worker inputs and renames', t => {
  const repo = repository(t);
  repo.write('share-worker/src/index.js');
  const before = repo.commit();
  for (let index = 0; index < 350; index++) repo.write(`docs/file-${index}.md`);
  repo.commit();
  repo.write('share-worker/wrangler.toml');
  fs.renameSync(path.join(repo.root, 'share-worker/src/index.js'), path.join(repo.root, 'docs/renamed.md'));
  const after = repo.commit();
  const read = range => readGitDiff(range, repo.git);
  const scope = determineScope('push', push(before, after), after, read);
  assert.equal(scope.complete, true);
  assert.equal(scope.run, true);
  assert.equal(scope.changedPaths, 353);
  assert.equal(scope.relatedPaths, 2);
  const paths = parseDiff(read({ base: before, head: after }));
  assert.ok(paths.includes('share-worker/src/index.js'));
  assert.ok(paths.includes('docs/renamed.md'));
  const prScope = determineScope('pull_request', { pull_request: { base: { sha: before }, head: { sha: before } } }, after, read);
  assert.equal(prScope.run, true, 'tested tree changes must not be replaced by PR head paths');
});

test('empty complete diffs can skip, but invalid, truncated and oversized path lists require tests', () => {
  assert.deepEqual(parseDiff(Buffer.alloc(0)), []);
  assert.equal(determineScope('push', push(), head, () => Buffer.alloc(0)).run, false);
  for (const bytes of [Buffer.from('docs/file.md'), Buffer.from([0xff, 0]), names(['../file']),
    names(['/absolute']), names(['a\\b']), names(['a/./b']), names(['']),
    Buffer.alloc(limits.diffBytes + 1), names(Array(limits.paths + 1).fill('a'))]) {
    const scope = determineScope('push', push(), head, () => bytes);
    assert.equal(scope.run, true);
    assert.equal(scope.complete, false);
  }
});

test('unknown events, missing commits and comparison errors cannot suppress worker tests', () => {
  for (const [eventName, event, checkout] of [
    ['workflow_dispatch', {}, head], ['push', push('0'.repeat(40)), head],
    ['push', { ...push(), deleted: true }, head], ['push', push(), base],
    ['pull_request', { pull_request: { base: { sha: base } } }, head], ['push', push(), 'invalid'],
  ]) assert.deepEqual(determineScope(eventName, event, checkout, () => names(['docs/file.md'])),
    { run: true, complete: false, reason: 'complete change scope unavailable; execute worker tests' });
  for (const message of ['missing commit', 'timeout', 'maxBuffer exceeded', 'fetch unavailable']) {
    const scope = determineScope('push', push(), head, () => { throw new Error(message); });
    assert.equal(scope.run, true);
    assert.equal(scope.complete, false);
  }
});

test('missing revisions are fetched by exact SHA with bounded depth, never unbounded history', () => {
  const calls = [], available = new Set([head]);
  readGitDiff({ base, head }, args => {
    calls.push(args);
    if (args[0] === 'cat-file' && !available.has(args[2].slice(0, 40))) throw new Error('missing');
    if (args[0] === 'fetch') available.add(args.at(-1));
    return Buffer.alloc(0);
  });
  assert.deepEqual(calls.filter(args => args[0] === 'fetch'), [['fetch', '--no-tags', '--depth=1', 'origin', base]]);
  assert.deepEqual(calls.at(-1), ['diff', '--name-only', '--no-renames', '-z', base, head, '--']);
});

test('CLI emits truthful not-applicable output only for an identified complete checkout', t => {
  const repo = repository(t);
  repo.write('docs/first.md');
  const before = repo.commit();
  repo.write('docs/second.md');
  const after = repo.commit();
  const invoke = (event, checkout = after) => {
    const output = path.join(repo.root, 'output'), summary = path.join(repo.root, 'summary'), eventFile = path.join(repo.root, 'event');
    fs.writeFileSync(output, ''); fs.writeFileSync(summary, ''); fs.writeFileSync(eventFile, event);
    const result = spawnSync(process.execPath, [script], { cwd: repo.root, encoding: 'utf8',
      env: { ...process.env, GITHUB_SHA: checkout, GITHUB_EVENT_NAME: 'push', GITHUB_EVENT_PATH: eventFile,
        GITHUB_OUTPUT: output, GITHUB_STEP_SUMMARY: summary } });
    assert.ifError(result.error);
    return { ...result, output: fs.readFileSync(output, 'utf8'), summary: fs.readFileSync(summary, 'utf8') };
  };
  const complete = invoke(JSON.stringify(push(before, after)));
  assert.equal(complete.status, 0);
  assert.equal(complete.output, 'run_worker=false\ncomplete_diff=true\n');
  assert.match(complete.summary, /Not applicable: no worker tests will run/);
  assert.doesNotMatch(complete.summary, /tests passed/i);
  for (const event of ['{broken', ' '.repeat(limits.eventBytes + 1)]) {
    const unknown = invoke(event);
    assert.equal(unknown.status, 0);
    assert.equal(unknown.output, 'run_worker=true\ncomplete_diff=false\n');
  }
  const mismatch = invoke(JSON.stringify(push(before, after)), before);
  assert.notEqual(mismatch.status, 0);
  assert.equal(mismatch.output, 'run_worker=true\ncomplete_diff=false\n');
});

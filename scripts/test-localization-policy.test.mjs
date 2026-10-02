import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const repository = fileURLToPath(new URL('../', import.meta.url));
const protocolPath = 'src/DropSpace.Core/Lyrics/PlainHyLyricsProtocol.cs';
const protocol = fs.readFileSync(path.join(repository, protocolPath), 'utf8');
const languagePolicyPath = 'src/DropSpace.Core/Lyrics/LyricsLanguagePolicy.cs';
const languagePolicy = fs.readFileSync(path.join(repository, languagePolicyPath), 'utf8');
const policy = fs.readFileSync(path.join(repository, 'scripts/Test-Localization.ps1'), 'utf8');
const start = policy.indexOf('# These three frozen model-facing constants');
const end = policy.indexOf('$imperativeResourceKeys =', start);
assert.ok(start >= 0 && end > start, 'The production localization policy block must be present');

function runPolicy(t, protocolText = protocol, otherText = 'class Ui { string Text = "Hello"; }', languageText = languagePolicy) {
  const temporaryRoot = path.resolve(os.tmpdir());
  const root = fs.mkdtempSync(path.join(temporaryRoot, 'dropspace-localization-test-'));
  assert.ok(root.startsWith(temporaryRoot + path.sep), 'Fixture must stay inside the temporary directory');
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  for (const [name, content] of [[protocolPath, protocolText], [languagePolicyPath, languageText], ['src/DropSpace.App/Ui.cs', otherText]]) {
    const filename = path.join(root, name);
    fs.mkdirSync(path.dirname(filename), { recursive: true });
    fs.writeFileSync(filename, content);
  }
  // Execute the real guard against disposable source files; no production prompt is changed.
  const command = '$ErrorActionPreference = "Stop"; Set-StrictMode -Version Latest; ' +
    '$repositoryRoot = $env:DROPSPACE_LOCALIZATION_TEST_ROOT; ' +
    '$sourceFiles = @(Get-ChildItem (Join-Path $repositoryRoot "src") -Recurse -File);\n' +
    policy.slice(start, end);
  const result = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', command], {
    encoding: 'utf8', timeout: 15000,
    env: { ...process.env, DROPSPACE_LOCALIZATION_TEST_ROOT: root },
  });
  assert.ifError(result.error);
  return result;
}

test('evaluated model constants are accepted with LF and CRLF', t => {
  for (const endings of ['\n', '\r\n']) {
    const result = runPolicy(t, protocol.replace(/\r\n/g, '\n').replace(/\n/g, endings));
    assert.equal(result.status, 0, result.stderr);
  }
});

test('changing a model target fails the frozen protocol guard', t => {
  const result = runPolicy(t, protocol.replace('"英语"', '"英文"'));
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /frozen Hy model-facing protocol changed/);
});

test('changing the evaluated prompt fails the frozen protocol guard', t => {
  const result = runPolicy(t, protocol.replace('不要额外解释', '请额外解释'));
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /frozen Hy model-facing protocol changed/);
});

test('duplicate protocol definitions fail instead of broadening the exception', t => {
  const result = runPolicy(t, protocol + '\npublic const string EnglishTarget = "英语";\n');
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /frozen Hy model-facing protocol changed/);
});

test('new Chinese text in the protocol file is still rejected', t => {
  const result = runPolicy(t, protocol + '\n// 新增界面提示\n');
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /Chinese UI text must be placed in/);
});

test('an allowed model line copied into UI code is still rejected', t => {
  const result = runPolicy(t, protocol, 'public const string EnglishTarget = "英语";');
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /Chinese UI text must be placed in/);
});


test('host vocabulary accepts LF and CRLF without exempting the entire Core file', t => {
  for (const endings of ['\n', '\r\n']) {
    const result = runPolicy(t, protocol, undefined, languagePolicy.replace(/\r\n/g, '\n').replace(/\n/g, endings));
    assert.equal(result.status, 0, result.stderr);
  }
});

test('changing host classification vocabulary requires its own exact policy review', t => {
  const changed = languagePolicy.replace('"我会"', '"我将"');
  assert.notEqual(changed, languagePolicy);
  const result = runPolicy(t, protocol, undefined, changed);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /host lyric language vocabulary changed/);
});

test('new Chinese UI text inside the language policy file remains rejected', t => {
  const result = runPolicy(t, protocol, undefined, languagePolicy + '\nconst string NewUi = "新增界面提示";\n');
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /Chinese UI text must be placed in/);
});

test('an exact host vocabulary declaration copied into UI code remains rejected', t => {
  const declaration = languagePolicy.split(/\r?\n/).find(line => line.includes('string[] Simplified'));
  assert.ok(declaration);
  const result = runPolicy(t, protocol, declaration);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /Chinese UI text must be placed in/);
});

test('duplicate host vocabulary definitions cannot broaden the localized exception', t => {
  const declaration = languagePolicy.split(/\r?\n/).find(line => line.includes('string[] Simplified'));
  const result = runPolicy(t, protocol, undefined, languagePolicy + '\n' + declaration + '\n');
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /host lyric language vocabulary changed/);
});

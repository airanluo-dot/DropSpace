import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { approvalPath, fixturePath, readScope, sha256, sourcePaths, productionPromptProfile, productionOutputSchema, validateApproval } from './test-ai-release-approval.mjs';

const repository = fileURLToPath(new URL('../', import.meta.url));
const now = Date.parse('2026-10-02T00:00:00Z');
const json = value => JSON.stringify(value, null, 2) + '\n';

// Synthetic approvals exist only in disposable temporary test repositories.
// They are not model evaluations and cannot approve this repository's manifest.
function example(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'dropspace-gate-test-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const write = (name, contents) => {
    fs.mkdirSync(path.dirname(path.join(root, name)), { recursive: true });
    fs.writeFileSync(path.join(root, name), contents);
  };
  for (const name of [...sourcePaths, fixturePath, 'RELEASE_VERSION']) write(name, fs.readFileSync(path.join(repository, name)));
  const scope = readScope(root);
  const evidenceDirectory = 'scripts/ai-model-qa/evidence';
  const stdoutPath = `${evidenceDirectory}/SYNTHETIC-OUTPUT-ONLY.txt`;
  const stdout = 'Synthetic validator regression bytes, not an actual model output or semantic approval.\n';
  write(stdoutPath, stdout);
  const runtimePath = `${evidenceDirectory}/SYNTHETIC-RUNTIME-ONLY.json`;
  const runtime = {
    schemaVersion: 1, runtimeId: scope.runtime.id, sourceCommit: scope.runtime.sourceCommit,
    executable: 'llama-completion.exe', sha256: sha256('synthetic baseline'), bytes: 18,
    avx2: { executable: 'llama-completion-avx2.exe', sha256: sha256('synthetic avx2'), bytes: 14 },
    tokenizer: { executable: 'llama-tokenize.exe', sha256: sha256('synthetic tokenizer'), bytes: 19 },
  };
  write(runtimePath, json(runtime));
  const runtimeReference = { path: runtimePath, sha256: sha256(json(runtime)) };
  const envelopes = scope.shippingModels.flatMap(model => ['baseline', 'avx2'].map(variant => ({
    schemaVersion: 1, kind: 'native-output', model: { ...model },
    fixtureSha256: scope.fixture.sha256, promptVersion: scope.promptVersion,
    promptProfile: productionPromptProfile, outputSchema: productionOutputSchema,
    sourceFingerprintSha256: scope.sources.sha256, platform: 'windows-x64', executedAt: '2026-10-01T00:00:00Z',
    runtime: {
      manifestSha256: runtimeReference.sha256, variant,
      completion: variant === 'baseline' ? { sha256: runtime.sha256, bytes: runtime.bytes } : { sha256: runtime.avx2.sha256, bytes: runtime.avx2.bytes },
      tokenizer: { sha256: runtime.tokenizer.sha256, bytes: runtime.tokenizer.bytes },
    },
    outputs: ['en', 'zh-Hans'].map(targetLanguage => ({ kind: 'raw-output', targetLanguage, path: stdoutPath, sha256: sha256(stdout) })),
  })));
  const configurations = envelopes.map(envelope => ({
    promptProfile: productionPromptProfile, outputSchema: productionOutputSchema, loadOnly: false,
    modelId: envelope.model.id, modelSha256: envelope.model.sha256, modelBytes: envelope.model.bytes,
    executableSha256: envelope.runtime.completion.sha256, tokenizerSha256: envelope.runtime.tokenizer.sha256,
  }));
  const configurationPaths = configurations.map((_, i) => `${evidenceDirectory}/SYNTHETIC-CONFIGURATION-ONLY-${i}.json`);
  configurations.forEach((configuration, i) => {
    write(configurationPaths[i], json(configuration));
    envelopes[i].configuration = { path: configurationPaths[i], sha256: sha256(json(configuration)) };
  });
  const rawPaths = envelopes.map((_, i) => `${evidenceDirectory}/SYNTHETIC-ENVELOPE-ONLY-${i}.json`);
  const rawPath = rawPaths[0];
  const report = {
    schemaVersion: 1, kind: 'semantic-review', verdict: 'approved',
    reviewedBy: 'SYNTHETIC TEST ONLY', reviewedAt: '2026-10-01T00:00:00Z', expiresAt: '2026-10-03T00:00:00Z',
    summary: 'Synthetic unit test; never release evidence.', scope, runtimeManifest: runtimeReference,
    models: scope.shippingModels.map((model, i) => ({ ...model, verdict: 'approved', summary: 'Synthetic unit test.', evidence: [0, 1].map(variant => ({
      kind: 'native-output', path: rawPaths[i * 2 + variant], sha256: '', fixtureSha256: scope.fixture.sha256,
    })) })),
  };
  const reportPath = `${evidenceDirectory}/SYNTHETIC-TEST-ONLY.json`;
  const approval = { schemaVersion: 1, status: 'approved', scope, review: { path: reportPath, sha256: '' } };
  const save = () => {
    write(reportPath, json(report));
    approval.review.sha256 = sha256(json(report));
    write(approvalPath, json(approval));
  };
  const saveEnvelopes = () => {
    envelopes.forEach((envelope, i) => {
      write(rawPaths[i], json(envelope));
      report.models[Math.floor(i / 2)].evidence[i % 2].sha256 = sha256(json(envelope));
    });
    save();
  };
  saveEnvelopes();
  save();
  return { root, scope, report, approval, rawPath, reportPath, stdoutPath, runtimePath, runtime, envelopes, configurations, configurationPaths, write, save, saveEnvelopes, validate: options => validateApproval(root, { now, ...options }) };
}

test('synthetic current approval with complete bound evidence passes', t => example(t).validate());

test('repository manifest is never approved by these synthetic tests', () => {
  const record = JSON.parse(fs.readFileSync(path.join(repository, approvalPath), 'utf8'));
  assert.equal(record.schemaVersion, 1);
  assert.ok(['pending', 'approved'].includes(record.status));
  if (record.status === 'pending') assert.throws(() => validateApproval(repository), /pending or absent/);
  // An approved record can become stale or expire during development. Only
  // publication checks its live validity; ordinary PR regression tests still run.
});

for (const [name, mutate, expected] of [
  ['missing manifest', x => fs.unlinkSync(path.join(x.root, approvalPath)), /ENOENT/],
  ['empty manifest', x => x.write(approvalPath, ''), /JSON/],
  ['malformed manifest', x => x.write(approvalPath, '{'), /JSON/],
  ['unknown schema', x => { x.approval.schemaVersion = 2; x.save(); }, /schema/],
  ['pending manifest', x => { x.approval.status = 'pending'; x.save(); }, /pending or absent/],
  ['rejected manifest', x => { x.approval.status = 'rejected'; x.save(); }, /pending or absent/],
  ['empty manifest scope', x => { x.approval.scope = {}; x.save(); }, /stale/],
  ['missing model approval', x => { x.report.models.pop(); x.save(); }, /Every shipping model/],
  ['duplicate model approval', x => { x.report.models.push(x.report.models[0]); x.save(); }, /Every shipping model/],
  ['model semantic failure', x => { x.report.models[0].verdict = 'rejected'; x.save(); }, /not semantically approved/],
  ['missing review', x => { x.write(approvalPath, json({ ...x.approval, review: null })); }, /reference is required/],
  ['missing report', x => fs.unlinkSync(path.join(x.root, x.reportPath)), /ENOENT/],
  ['empty report', x => x.write(x.reportPath, ''), /must not be empty/],
  ['wrong report hash', x => x.write(x.reportPath, json({ ...x.report, summary: 'changed' })), /hash mismatch/],
  ['structure-only report', x => { x.report.kind = 'candidate-diagnostics'; x.save(); }, /not a semantic review/],
  ['pending semantic report', x => { x.report.verdict = 'pending'; x.save(); }, /has not approved/],
  ['missing reviewer', x => { x.report.reviewedBy = ''; x.save(); }, /identity/],
  ['missing review rationale', x => { x.report.summary = ' '; x.save(); }, /rationale/],
  ['expired review', x => { x.report.expiresAt = '2026-10-02T00:00:00Z'; x.save(); }, /expired/],
  ['future review', x => { x.report.reviewedAt = '2026-10-02T01:00:00Z'; x.save(); }, /future-dated/],
  ['unbounded review', x => { x.report.expiresAt = '2027-01-01T00:00:00Z'; x.save(); }, /30 days/],
  ['missing expiry', x => { delete x.report.expiresAt; x.save(); }, /UTC timestamp/],
  ['invalid date', x => { x.report.reviewedAt = '2026-02-31T00:00:00Z'; x.save(); }, /invalid/],
  ['timezone-free date', x => { x.report.reviewedAt = '2026-10-01T00:00:00'; x.save(); }, /UTC timestamp/],
  ['missing raw evidence', x => { x.report.models[0].evidence = []; x.save(); }, /needs native evidence/],
  ['structural log alone', x => { x.report.models[0].evidence[0].kind = 'structure-only'; x.save(); }, /not runtime evidence/],
  ['wrong raw fixture', x => { x.report.models[0].evidence[0].fixtureSha256 = '0'.repeat(64); x.save(); }, /pinned original QA fixture/],
  ['missing raw file', x => fs.unlinkSync(path.join(x.root, x.rawPath)), /ENOENT/],
  ['empty raw file', x => x.write(x.rawPath, ''), /must not be empty/],
  ['changed raw file', x => x.write(x.rawPath, 'changed'), /hash mismatch/],
  ['invalid raw hash', x => { x.report.models[0].evidence[0].sha256 = ''; x.save(); }, /SHA256 is required/],
  ['external evidence URL', x => { x.report.models[0].evidence[0].path = 'https://example.invalid/report'; x.save(); }, /repository evidence/],
  ['evidence traversal', x => { x.report.models[0].evidence[0].path = 'scripts/ai-model-qa/evidence/../other.txt'; x.save(); }, /path must be canonical/],
]) {
  test(`rejects ${name}`, t => {
    const x = example(t); mutate(x); assert.throws(() => x.validate(), expected);
  });
}

test('all production lyric and media-ingestion files have a code-owned fingerprint', () => {
  for (const directory of ['src/DropSpace.Core/Lyrics', 'src/DropSpace.Infrastructure/Lyrics', 'src/DropSpace.App/Services/Media']) {
    for (const entry of fs.readdirSync(path.join(repository, directory), { recursive: true })) {
      if (entry.endsWith('.cs')) assert.ok(sourcePaths.includes(`${directory}/${entry.replaceAll('\\', '/')}`), `New production source needs fingerprint coverage: ${directory}/${entry}`);
    }
  }
  for (const name of [
    'src/DropSpace.Core/Policies/AppLanguagePolicy.cs', 'src/DropSpace.Core/Media/MediaModels.cs',
    'src/DropSpace.App/App.xaml.cs', 'src/DropSpace.App/ViewModels/MediaViewModel.cs', 'src/DropSpace.App/Services/AppLanguageService.cs',
    'src/DropSpace.App/Services/ResourceStringLocalizer.cs', 'src/DropSpace.App/Views/Music/MusicPage.cs',
    'src/DropSpace.App/Views/Island/MediaCompactView.xaml.cs', 'src/DropSpace.App/Views/Island/MediaExpandedView.xaml.cs',
    'src/DropSpace.App/OverlayWindow.xaml', 'src/DropSpace.App/Views/Island/ExpandedIslandMusicView.xaml',
    'src/DropSpace.App/Views/Island/ExpandedIslandMusicView.xaml.cs',
  ]) assert.ok(sourcePaths.includes(name), `Target/display chain needs fingerprint coverage: ${name}`);
});

for (const [name, mutate, expected] of [
  ['wrong model ID', e => { e.model.id = 'wrong'; }, /model identity/],
  ['wrong model SHA', e => { e.model.sha256 = '0'.repeat(64); }, /model identity/],
  ['wrong model bytes', e => { e.model.bytes = 1; }, /model identity/],
  ['wrong fixture', e => { e.fixtureSha256 = '0'.repeat(64); }, /fixture mismatch/],
  ['wrong prompt', e => { e.promptVersion = 'wrong'; }, /prompt version/],
  ['minimal-target-only profile', e => { e.promptProfile = 'minimal-target-only'; }, /production prompt profile/],
  ['missing prompt profile', e => { delete e.promptProfile; }, /production prompt profile/],
  ['wrong output schema', e => { e.outputSchema = 'diagnostic-free-text'; }, /production output schema/],
  ['missing output schema', e => { delete e.outputSchema; }, /production output schema/],
  ['wrong source fingerprint', e => { e.sourceFingerprintSha256 = '0'.repeat(64); }, /source fingerprint/],
  ['wrong platform', e => { e.platform = 'linux-x64'; }, /Windows x64/],
  ['future execution', e => { e.executedAt = '2026-10-02T00:00:00Z'; }, /postdates/],
  ['missing execution time', e => { delete e.executedAt; }, /UTC timestamp/],
  ['wrong runtime manifest', e => { e.runtime.manifestSha256 = '0'.repeat(64); }, /runtime manifest mismatch/],
  ['missing variant', e => { delete e.runtime.variant; }, /variant is required/],
  ['mislabeled variant', e => { e.runtime.variant = 'avx2'; }, /completion hash/],
  ['wrong completion bytes', e => { e.runtime.completion.bytes = 1; }, /completion hash/],
  ['wrong completion SHA', e => { e.runtime.completion.sha256 = '0'.repeat(64); }, /completion hash/],
  ['wrong tokenizer SHA', e => { e.runtime.tokenizer.sha256 = '0'.repeat(64); }, /tokenizer hash/],
  ['wrong tokenizer bytes', e => { e.runtime.tokenizer.bytes = 1; }, /tokenizer hash/],
  ['missing raw output references', e => { e.outputs = []; }, /output references/],
  ['wrong raw output hash', e => { e.outputs[0].sha256 = '0'.repeat(64); }, /hash mismatch/],
  ['summary used as raw output', e => { e.outputs[0].kind = 'summary'; }, /preserved raw output/],
  ['unsupported output target', e => { e.outputs[0].targetLanguage = 'fr'; }, /target is unsupported/],
  ['missing target language', e => { e.outputs.pop(); }, /both shipping target languages/],
]) {
  test(`native envelope rejects ${name} even with recomputed outer hashes`, t => {
    const x = example(t); mutate(x.envelopes[0]); x.saveEnvelopes(); assert.throws(() => x.validate(), expected);
  });
}

for (const [name, mutate, expected] of [
  ['minimal-target-only run mislabeled by its envelope', c => { c.promptProfile = 'minimal-target-only'; }, /not the production prompt profile/],
  ['implicit default profile', c => { delete c.promptProfile; }, /not the production prompt profile/],
  ['wrong raw schema identity', c => { c.outputSchema = 'minimal-target-only'; }, /production output schema identity/],
  ['missing raw schema identity', c => { delete c.outputSchema; }, /production output schema identity/],
  ['loader-only run', c => { c.loadOnly = true; }, /Loader-only/],
  ['wrong model', c => { c.modelId = 'qwen3-17-q4'; }, /model identity mismatch/],
  ['wrong model hash', c => { c.modelSha256 = '0'.repeat(64); }, /model identity mismatch/],
  ['wrong model bytes', c => { c.modelBytes = 1; }, /model identity mismatch/],
  ['wrong completion hash', c => { c.executableSha256 = '0'.repeat(64); }, /completion hash mismatch/],
  ['wrong tokenizer hash', c => { c.tokenizerSha256 = '0'.repeat(64); }, /tokenizer hash mismatch/],
]) {
  test(`native raw configuration rejects ${name} even when all outer hashes match`, t => {
    const x = example(t); mutate(x.configurations[0]);
    x.write(x.configurationPaths[0], json(x.configurations[0]));
    x.envelopes[0].configuration.sha256 = sha256(json(x.configurations[0]));
    x.saveEnvelopes();
    assert.throws(() => x.validate(), expected);
  });
}

test('missing, modified or hashless native raw configuration fails closed', t => {
  const x = example(t);
  delete x.envelopes[0].configuration.sha256; x.saveEnvelopes();
  assert.throws(() => x.validate(), /SHA256 is required/);
  x.envelopes[0].configuration.sha256 = sha256(json(x.configurations[0])); x.saveEnvelopes();
  x.write(x.configurationPaths[0], '{}');
  assert.throws(() => x.validate(), /hash mismatch/);
  fs.unlinkSync(path.join(x.root, x.configurationPaths[0]));
  assert.throws(() => x.validate(), /ENOENT/);
});

test('opaque nonempty native files are no longer sufficient evidence', t => {
  const x = example(t);
  x.write(x.rawPath, 'not a provenance envelope');
  x.report.models[0].evidence[0].sha256 = sha256('not a provenance envelope');
  x.save();
  assert.throws(() => x.validate(), /JSON|Unexpected token/);
});

test('one runtime variant cannot approve both shipping variants', t => {
  const x = example(t);
  x.report.models[0].evidence.pop(); x.save();
  assert.throws(() => x.validate(), /both shipping runtime variants/);
});

test('raw output cannot disappear or change after its envelope is recorded', t => {
  const x = example(t);
  x.write(x.stdoutPath, 'changed');
  assert.throws(() => x.validate(), /hash mismatch/);
  fs.unlinkSync(path.join(x.root, x.stdoutPath));
  assert.throws(() => x.validate(), /ENOENT/);
});

for (const [name, mutate, expected] of [
  ['wrong runtime ID', r => { r.runtimeId = 'wrong'; }, /identity mismatch/],
  ['wrong runtime source', r => { r.sourceCommit = '0'.repeat(40); }, /source mismatch/],
  ['missing AVX2 hash', r => { delete r.avx2.sha256; }, /SHA256 is required/],
  ['zero tokenizer size', r => { r.tokenizer.bytes = 0; }, /byte count is required/],
  ['wrong baseline component name', r => { r.executable = 'wrong.exe'; }, /Unexpected runtime component/],
]) {
  test(`reviewed runtime rejects ${name} even with recomputed manifest hash`, t => {
    const x = example(t); mutate(x.runtime);
    x.write(x.runtimePath, json(x.runtime));
    x.report.runtimeManifest.sha256 = sha256(json(x.runtime)); x.save();
    assert.throws(() => x.validate(), expected);
  });
}

for (const name of sourcePaths) {
  test(`invalidates changed source: ${name}`, t => {
    const x = example(t);
    fs.appendFileSync(path.join(x.root, name), '\n// changed after review\n');
    assert.throws(() => x.validate(), /stale/);
  });
}

test('removing a critical source from both manifest and review cannot bypass the fixed allowlist', t => {
  const x = example(t);
  x.scope.sources.files = x.scope.sources.files.filter(file => !file.path.endsWith('LyricsTranslationOutput.cs'));
  x.scope.sources.sha256 = sha256(JSON.stringify(x.scope.sources.files));
  x.save();
  assert.throws(() => x.validate(), /stale/);
});

test('changed fixture or release version invalidates approval', t => {
  const x = example(t);
  x.write(fixturePath, '{}\n');
  assert.throws(() => x.validate(), /stale/);
  x.write(fixturePath, fs.readFileSync(path.join(repository, fixturePath)));
  x.write('RELEASE_VERSION', 'v9.9.9');
  assert.throws(() => x.validate(), /stale/);
});

test('changing a shipping model hash invalidates approval', t => {
  const x = example(t);
  const catalog = sourcePaths[0];
  const original = fs.readFileSync(path.join(x.root, catalog), 'utf8');
  x.write(catalog, original.replace(x.scope.shippingModels[0].sha256, '0'.repeat(64)));
  assert.throws(() => x.validate(), /stale/);
});

test('unrecognized catalog refactor cannot silently yield an empty shipping list', t => {
  const x = example(t);
  const catalog = sourcePaths[0];
  const original = fs.readFileSync(path.join(x.root, catalog), 'utf8');
  x.write(catalog, original.replace('Array.AsReadOnly(new[] { Standard, Compact })', 'BuildModels()'));
  assert.throws(() => x.validate(), /Shipping model list/);
});

test('changing production runtime source invalidates approval', t => {
  const x = example(t);
  const name = 'scripts/Build-AiLyricsRuntime.ps1';
  x.write(name, fs.readFileSync(path.join(x.root, name), 'utf8').replace(x.scope.runtime.sourceCommit, '0'.repeat(40)));
  assert.throws(() => x.validate(), /stale/);
});

test('updating an evidence hash cannot make whitespace-only evidence valid', t => {
  const x = example(t);
  x.write(x.rawPath, ' \n');
  for (const model of x.report.models) model.evidence[0].sha256 = sha256(' \n');
  x.save();
  assert.throws(() => x.validate(), /must not be empty/);
});

test('evidence symlinks cannot escape the repository', { skip: process.platform === 'win32' ? 'Windows symlinks require extra privileges; containment is also checked by canonical-path tests' : false }, t => {
  const x = example(t);
  const outside = fs.mkdtempSync(path.join(os.tmpdir(), 'dropspace-gate-outside-'));
  t.after(() => fs.rmSync(outside, { recursive: true, force: true }));
  const bytes = fs.readFileSync(path.join(x.root, x.rawPath));
  fs.writeFileSync(path.join(outside, 'raw.txt'), bytes);
  fs.unlinkSync(path.join(x.root, x.rawPath));
  fs.symlinkSync(path.join(outside, 'raw.txt'), path.join(x.root, x.rawPath));
  assert.throws(() => x.validate(), /escapes evidence directory/);
});

test('source hashes are stable across LF and Windows CRLF checkouts', t => {
  const x = example(t);
  for (const name of sourcePaths) x.write(name, fs.readFileSync(path.join(x.root, name), 'utf8').replace(/\r\n/g, '\n').replace(/\n/g, '\r\n'));
  x.validate();
});

for (const field of ['shippingModels', 'runtime', 'promptVersion', 'fixture']) {
  test(`cannot renew approval by changing only the manifest ${field}`, t => {
    const x = example(t);
    x.approval.scope = { ...x.scope, [field]: null };
    x.save();
    assert.throws(() => x.validate(), /stale/);
  });
}

test('updating manifest scope without a newly matching review fails', t => {
  const x = example(t);
  fs.appendFileSync(path.join(x.root, sourcePaths[2]), '\n// prompt changed\n');
  x.approval.scope = readScope(x.root);
  x.save();
  assert.throws(() => x.validate(), /different release inputs/);
});

test('built runtime ID and source commit must match the semantic review', t => {
  const x = example(t);
  const runtimePath = path.join(x.root, 'runtime.json');
  const runtime = { schemaVersion: 1, runtimeId: x.scope.runtime.id, sourceCommit: x.scope.runtime.sourceCommit };
  fs.writeFileSync(runtimePath, json(runtime));
  x.validate({ runtimeManifestPath: runtimePath });
  for (const [field, value] of [['runtimeId', 'wrong'], ['sourceCommit', '0'.repeat(40)], ['schemaVersion', 2]]) {
    fs.writeFileSync(runtimePath, json({ ...runtime, [field]: value }));
    assert.throws(() => x.validate({ runtimeManifestPath: runtimePath }), /runtime/);
  }
  fs.unlinkSync(runtimePath);
  assert.throws(() => x.validate({ runtimeManifestPath: runtimePath }), /ENOENT/);
});

test('CLI fails closed for unknown arguments rather than skipping validation', () => {
  const result = spawnSync(process.execPath, ['scripts/test-ai-release-approval.mjs', '--publish=false'], { cwd: repository, encoding: 'utf8' });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /AI release blocked/);
});

test('workflow tests every PR but gates only explicit publication, with a second publisher check', () => {
  const workflow = fs.readFileSync(path.join(repository, '.github/workflows/release.yml'), 'utf8');
  const validate = workflow.split('\n  validate-release:')[1].split('\n  ai-model-diagnostics:')[0];
  assert.match(validate, /- name: Test AI semantic release gate regressions\n        run: node --test scripts\/test-ai-release-approval.test.mjs/);
  assert.match(validate, /- name: Enforce AI semantic publication approval before building\n        if: github.event_name == 'workflow_dispatch' && inputs.publish == true\n        run: node scripts\/test-ai-release-approval.mjs/);
  assert.match(validate, /- name: Bind semantic approval to the built shipping runtime\n        if: github.event_name == 'workflow_dispatch' && inputs.publish == true\n        id: ai-release-approval/);
  assert.match(validate, /--runtime-manifest artifacts\/ai-runtime\/win-x64\/runtime-manifest.json\n          if \(\$LASTEXITCODE -ne 0\)/);
  assert.match(validate, /ai_semantic_approved: \$\{\{ steps.ai-release-approval.outputs.approved \}\}/);
  const publish = workflow.split('\n  publish-release:')[1];
  assert.match(publish, /needs.validate-release.outputs.ai_semantic_approved == 'true'/);
  assert.match(publish, /needs: \[validate-release, sign-release\]/);
  assert.match(publish, /- name: Recheck AI semantic publication approval\n        run: node scripts\/test-ai-release-approval.mjs\n\n      - name: Publish immutable/);
  assert.match(validate, /Assert-DropSpacePublicationCommit \$env:EXPECTED_COMMIT \$env:ACTUAL_COMMIT/);
});

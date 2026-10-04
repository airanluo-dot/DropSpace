import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { approvalPath, admissionPath, fixturePath, fixtureAdmissionDecision, readScope, sha256, sourcePaths, residentSourcePaths, productionPromptProfile, productionOutputSchema, productionCaptureMethod, validateApproval, experimentalBetaStatus, experimentalBetaVersion, publicationDecision } from './test-ai-release-approval.mjs';
import { fileIdentity, writeReleaseBinding } from './ai-runtime-publication.mjs';

const repository = fileURLToPath(new URL('../', import.meta.url));
const now = Date.parse('2026-10-02T00:00:00Z');
const json = value => JSON.stringify(value, null, 2) + '\n';

test('fixture admission retains unknown Latin and Kana, and abstains only from unconfirmed Han for a Chinese target', () => {
  const unknown = { detectedLanguage: null, confidence: 0 };
  for (const text of ['kimi no na wa', 'I love you je suis heureux', 'あ', '愛 I will wait', '\uf900']) {
    assert.equal(fixtureAdmissionDecision(text, 'zh-Hans', unknown), 'Translate', text);
  }
  for (const text of ['愛', '山谷的石门', '我不関焉', '\u{20000}']) {
    assert.equal(fixtureAdmissionDecision(text, 'zh-Hans', unknown), 'Abstain', text);
    assert.equal(fixtureAdmissionDecision(text, 'en', unknown), 'Translate', text);
  }
  assert.deepEqual(unknown, { detectedLanguage: null, confidence: 0 }, 'Abstention must not invent a language identity.');
});

test('fixture admission preserves reliable Japanese evidence and the confidence boundary for pure Han', () => {
  assert.equal(fixtureAdmissionDecision('世界', 'zh-Hans', { detectedLanguage: 'ja', confidence: 1 }), 'Translate');
  assert.equal(fixtureAdmissionDecision('世界', 'zh-Hans', { detectedLanguage: 'ja', confidence: 0.9 }), 'Translate');
  assert.equal(fixtureAdmissionDecision('世界', 'zh-Hans', { detectedLanguage: 'ja', confidence: 0.65 }), 'Abstain');
  assert.equal(fixtureAdmissionDecision('世界', 'zh-Hans', { detectedLanguage: 'mul', confidence: 1 }), 'Abstain');
  assert.equal(fixtureAdmissionDecision('世界', 'zh-Hans', { detectedLanguage: 'zh-Hant', confidence: 1 }), 'SameLanguage');
  assert.equal(fixtureAdmissionDecision('I love you', 'en', { detectedLanguage: 'en', confidence: 0.95 }), 'SameLanguage');
});

for (const [label, target, id, eligible] of [
  ['cannot relabel Chinese-target Han abstention as unknown translation', 'zh-Hans', 36, true],
  ['cannot suppress unknown Latin for a Chinese target', 'zh-Hans', 4, false],
  ['cannot suppress reliably identified Japanese', 'zh-Hans', 12, false],
  ['cannot suppress unknown Han for an English target', 'en', 36, false],
  ['cannot suppress unknown Latin for an English target', 'en', 4, false],
]) {
  test(`current fixture admission ${label}`, t => {
    const x = example(t);
    const filename = path.join(x.root, admissionPath);
    const record = JSON.parse(fs.readFileSync(filename));
    const row = record.targets[target][id];
    row.eligible = eligible;
    row.reason = eligible ? 'unknown-language-retained' : 'no-eligible-segments';
    row.segments = eligible ? [{ segmentIndex: 0, text: row.sourceText, sha256: sha256(row.sourceText) }] : [];
    fs.writeFileSync(filename, json(record));
    assert.throws(() => readScope(x.root), /v8 three-state policy/);
  });
}

for (const [label, mutate, expected] of [
  ['model inference claim', record => record.modelInferenceExecuted = true, /cannot claim model inference/],
  ['semantic approval claim', record => record.semanticApproved = true, /cannot claim semantic approval/],
  ['stale policy bytes', record => record.policySourceSha256 = '0'.repeat(64), /policy source hash is stale/],
]) {
  test(`host admission computation rejects ${label}`, t => {
    const x = example(t);
    const filename = path.join(x.root, admissionPath);
    const record = JSON.parse(fs.readFileSync(filename));
    mutate(record);
    fs.writeFileSync(filename, json(record));
    assert.throws(() => readScope(x.root), expected);
  });
}

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
  const fixtureLines = JSON.parse(fs.readFileSync(path.join(root, fixturePath), 'utf8')).Lines;
  const memory = model => ({ observedAt: '2026-10-01T00:00:00Z', availablePhysicalBytes: 32 * 1024 ** 3,
    availableCommitBytes: 32 * 1024 ** 3, requiredAvailableBytes: scope.modelProfiles[model.id].cpuMemoryAdmission.minimumAvailableBytes });
  const syntheticOutput = 'SYNTHETIC UNIT TEST ONLY: this is not actual native output or semantic approval.';
  const outputPackets = scope.shippingModels.flatMap(model => ['baseline', 'avx2'].flatMap(variant => ['en', 'zh-Hans'].map(targetLanguage => {
    const admission = structuredClone(scope.fixtureAdmission.targets[targetLanguage]);
    const eligible = admission.filter(row => row.eligible);
    return {
      schemaVersion: 2, kind: 'production-runner-output', model: { ...model }, variant, targetLanguage, outcome: 'Translated',
      admissionPolicyVersion: scope.fixtureAdmission.policyVersion, admission, hostMemoryBefore: memory(model),
      complete: true, playbackPositionSeconds: 0, residentProcessReuseConfirmed: true, firstProgressElapsedMilliseconds: 1,
      calls: eligible.flatMap(row => row.segments.map(segment => ({
        lineId: row.lineId, segmentIndex: segment.segmentIndex, segmentSha256: segment.sha256, sourceText: segment.text, verifiedModelSha256: model.sha256,
        phase: 'cold', targetLanguage, status: 'returned',
        nativeProcess: { workingSetBytes: 1024, peakWorkingSetBytes: 2048, privateMemoryBytes: 1024, id: 123, startedAt: '2026-10-01T00:00:00Z', executable: variant === 'baseline' ? 'plain-lyrics-worker.exe' : 'plain-lyrics-worker-avx2.exe' },
        prompt: scope.promptTemplate.replace('{0}', () => scope.targetNames[targetLanguage]).replace('{1}', () => segment.text),
        output: syntheticOutput,
      }))),
      finalDocument: { lines: fixtureLines.map((line, id) => ({ text: line.Text, start: line.Start, end: line.End, words: line.Words, sourceLanguage: line.SourceLanguage ?? null, secondary: admission[id].eligible ? syntheticOutput : null,
        translationOrigin: admission[id].eligible ? 2 : 0, translationLanguage: admission[id].eligible ? targetLanguage : null })) },
      progressEvents: eligible.map((row, index) => ({ requestIdentity: 'SYNTHETIC-REQUEST', cacheGeneration: 0,
        lineId: row.lineId, completedLineCount: index + 1, totalLineCount: eligible.length,
        isCurrent: true, isEphemeral: true, elapsedMilliseconds: index + 1 })),
    };
  })));
  const outputPaths = outputPackets.map((_, i) => `${evidenceDirectory}/SYNTHETIC-OUTPUT-ONLY-${i}.json`);
  outputPackets.forEach((packet, i) => write(outputPaths[i], json(packet)));
  const stdoutPath = outputPaths[0];
  const runtimePath = `${evidenceDirectory}/SYNTHETIC-RUNTIME-ONLY.json`;
  const runtime = {
    schemaVersion: 1, runtimeId: scope.runtime.id, sourceCommit: scope.runtime.sourceCommit,
    executable: 'llama-completion.exe', sha256: sha256('synthetic baseline'), bytes: 18,
    avx2: { executable: 'llama-completion-avx2.exe', sha256: sha256('synthetic avx2'), bytes: 14 },
    tokenizer: { executable: 'llama-tokenize.exe', sha256: sha256('synthetic tokenizer'), bytes: 19 },
    resident: {
      ...scope.runtime.resident,
      cpu: { executable: 'plain-lyrics-worker.exe', sha256: sha256('synthetic resident cpu'), bytes: 22 },
      avx2: { executable: 'plain-lyrics-worker-avx2.exe', sha256: sha256('synthetic resident avx2'), bytes: 23 },
      vulkan: { executable: 'plain-lyrics-worker-vulkan.exe', sha256: sha256('synthetic resident vulkan'), bytes: 25 },
    },
    producer: {
      repository: 'airanluo-dot/DropSpace', workflowPath: '.github/workflows/release.yml',
      runId: 123, runAttempt: 1, headCommit: 'a'.repeat(40), checkoutCommit: 'b'.repeat(40),
    },
  };
  for (const [variant, text] of [['cpu', 'synthetic resident cpu'], ['avx2', 'synthetic resident avx2'], ['vulkan', 'synthetic resident vulkan']]) runtime.resident[variant].bytes = Buffer.byteLength(text);
  write(runtimePath, json(runtime));
  const runtimeReference = { path: runtimePath, sha256: sha256(json(runtime)) };
  const runtimePayload = {
    'runtime-manifest.json': json(runtime), 'llama-completion.exe': 'synthetic baseline',
    'llama-completion-avx2.exe': 'synthetic avx2', 'llama-tokenize.exe': 'synthetic tokenizer',
    'LICENSE-llama.cpp': 'Synthetic license fixture; not a real runtime license.\n',
    'plain-lyrics-worker.exe': 'synthetic resident cpu', 'plain-lyrics-worker-avx2.exe': 'synthetic resident avx2',
    'plain-lyrics-worker-vulkan.exe': 'synthetic resident vulkan',
  };
  const runtimeArtifact = {
    schemaVersion: 1, ...runtime.producer, artifactId: 456,
    artifactName: 'ai-candidate-runtime-123-1', archiveSha256: sha256('synthetic archive'),
    files: Object.entries(runtimePayload).map(([name, bytes]) => ({ path: name, sha256: sha256(bytes), bytes: Buffer.byteLength(bytes) })),
  };
  const envelopes = scope.shippingModels.flatMap(model => ['baseline', 'avx2'].map(variant => ({
    schemaVersion: 3, kind: 'native-output', model: { ...model },
    fixtureSha256: scope.fixture.sha256, promptVersion: scope.promptVersion,
    promptProfile: productionPromptProfile, outputSchema: productionOutputSchema,
    backendId: scope.backendId, acceptanceVersion: scope.acceptanceVersion, samplerIdentity: scope.samplerIdentity,
    executionLimits: { ...scope.modelProfiles[model.id].executionLimits }, captureMethod: productionCaptureMethod,
    technicalChecks: { coldTargets: ['en', 'zh-Hans'], cacheTargets: ['en', 'zh-Hans'], cacheAdditionalInferenceCalls: 0,
      cancellationObserved: true, cleanupConfirmed: true, sourceIdentityUnchanged: true, modelIdentityUnchanged: true, runtimeIdentityUnchanged: true },
    sourceFingerprintSha256: scope.sources.sha256, platform: 'windows-x64', executedAt: '2026-10-01T00:00:00Z',
    runtime: {
      manifestSha256: runtimeReference.sha256, variant, mode: 'cpu', profile: scope.runtime.resident.profile,
      protocol: scope.runtime.resident.protocol, residentSourceSha256: scope.runtime.resident.sourceSha256,
      completion: variant === 'baseline' ? { sha256: runtime.resident.cpu.sha256, bytes: runtime.resident.cpu.bytes } : { sha256: runtime.resident.avx2.sha256, bytes: runtime.resident.avx2.bytes },
    },
    outputs: outputPackets.flatMap((packet, i) => packet.variant === variant && packet.model.id === model.id ? [{ kind: 'runner-output', targetLanguage: packet.targetLanguage, path: outputPaths[i], sha256: sha256(json(packet)) }] : []),
  })));
  const cachePackets = outputPackets.map(cold => ({ model: cold.model, targetLanguage: cold.targetLanguage,
    additionalInferenceCalls: 0, matchesCold: true, preflight: { outcome: 0, document: structuredClone(cold.finalDocument) },
    repeated: { outcome: 0, document: structuredClone(cold.finalDocument) } }));
  const cachePaths = cachePackets.map((packet, i) => `${evidenceDirectory}/SYNTHETIC-CACHE-ONLY-${i}.json`);
  const saveCaches = () => cachePackets.forEach((packet, index) => {
    write(cachePaths[index], json(packet));
    const envelope = envelopes[Math.floor(index / 2)];
    envelope.cacheOutputs ??= [];
    envelope.cacheOutputs[index % 2] = { targetLanguage: packet.targetLanguage, path: cachePaths[index], sha256: sha256(json(packet)) };
  });
  saveCaches();
  const gpuProbes = envelopes.map((envelope, index) => ({
    schemaVersion: 1, kind: 'production-gpu-default-probe', gpuEnabled: true, actualBackend: 'cpu', usedCpuFallback: true,
    deviceVendor: 'unverified', targetLanguage: 'en', fixtureLineId: 12, sourceText: fixtureLines[12].Text,
    prompt: scope.promptTemplate.replace('{0}', () => scope.targetNames.en).replace('{1}', () => fixtureLines[12].Text),
    output: 'SYNTHETIC UNIT TEST ONLY', outcome: 'Translated', complete: true, cleanupConfirmed: true,
    observedProcess: { workingSetBytes: 1024, peakWorkingSetBytes: 2048, privateMemoryBytes: 1024, id: 456, startedAt: '2026-10-01T00:00:00Z', executable: envelope.runtime.variant === 'baseline' ? 'plain-lyrics-worker.exe' : 'plain-lyrics-worker-avx2.exe' },
    hostMemoryBefore: memory(envelope.model), model: envelope.model, sourceFingerprintSha256: scope.sources.sha256, fixtureSha256: scope.fixture.sha256,
    runtimeManifestSha256: runtimeReference.sha256, residentSourceSha256: scope.runtime.resident.sourceSha256,
  }));
  const gpuProbePaths = gpuProbes.map((_, i) => `${evidenceDirectory}/SYNTHETIC-GPU-DEFAULT-ONLY-${i}.json`);
  gpuProbes.forEach((probe, i) => {
    write(gpuProbePaths[i], json(probe));
    envelopes[i].gpuDefaultProbe = { path: gpuProbePaths[i], sha256: sha256(json(probe)) };
  });
  const configurations = envelopes.map(envelope => ({
    schemaVersion: 3, promptProfile: productionPromptProfile, outputSchema: productionOutputSchema, loadOnly: false,
    promptVersion: scope.promptVersion, backendId: scope.backendId, acceptanceVersion: scope.acceptanceVersion,
    samplerIdentity: scope.samplerIdentity, captureMethod: productionCaptureMethod,
    executionLimits: { ...scope.modelProfiles[envelope.model.id].executionLimits }, nativeArguments: [...scope.modelProfiles[envelope.model.id].nativeArguments],
    samplerArguments: [...scope.samplerArguments], gpuEnabled: false,
    fixtureSha256: scope.fixture.sha256, sourceFingerprintSha256: scope.sources.sha256,
    runtimeManifestSha256: envelope.runtime.manifestSha256, runtimeVariant: envelope.runtime.variant,
    modelId: envelope.model.id, modelSha256: envelope.model.sha256, modelBytes: envelope.model.bytes,
    executableSha256: envelope.runtime.completion.sha256, executableBytes: envelope.runtime.completion.bytes,
  }));
  const configurationPaths = configurations.map((_, i) => `${evidenceDirectory}/SYNTHETIC-CONFIGURATION-ONLY-${i}.json`);
  configurations.forEach((configuration, i) => {
    write(configurationPaths[i], json(configuration));
    envelopes[i].configuration = { path: configurationPaths[i], sha256: sha256(json(configuration)) };
  });
  const rawPaths = envelopes.map((_, i) => `${evidenceDirectory}/SYNTHETIC-ENVELOPE-ONLY-${i}.json`);
  const rawPath = rawPaths[0];
  const report = {
    schemaVersion: 1, kind: 'semantic-review', verdict: 'approved', openDefects: [], acceptedLimitations: [],
    reviewedBy: 'SYNTHETIC TEST ONLY', reviewedAt: '2026-10-01T00:00:00Z', expiresAt: '2026-10-03T00:00:00Z',
    summary: 'Synthetic unit test; never release evidence.', scope, runtimeManifest: runtimeReference, runtimeArtifact,
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
  return { root, scope, report, approval, rawPath, reportPath, stdoutPath, runtimePath, runtime, runtimePayload, outputPackets, outputPaths, cachePackets, cachePaths, saveCaches, gpuProbes, gpuProbePaths, envelopes, configurations, configurationPaths, write, save, saveEnvelopes, validate: options => validateApproval(root, { now, ...options }) };
}

test('synthetic current approval with complete bound evidence passes', t => example(t).validate());

function experimentalExample(t) {
  const x = example(t);
  x.approval.status = experimentalBetaStatus;
  Object.assign(x.report, {
    kind: experimentalBetaStatus, verdict: experimentalBetaStatus, semanticApproved: false,
    acceptedLimitations: [
      { id: 'synthetic-timeout', kind: 'latency', summary: 'SYNTHETIC TEST ONLY: historical 300-second timeout; 600 seconds unverified.' },
      { id: 'synthetic-unverified', kind: 'quality', summary: 'SYNTHETIC TEST ONLY: both models lack complete semantic approval.' },
    ],
    userAcceptance: { releaseVersion: experimentalBetaVersion, acceptsIncompleteModelValidation: true,
      reference: 'SYNTHETIC TEST ONLY; not actual owner acceptance', acceptedAt: '2026-09-30T00:00:00Z' },
    models: x.scope.shippingModels.map(model => ({ ...model, verdict: 'unverified', summary: 'SYNTHETIC TEST ONLY: incomplete model validation.',
      validation: ['baseline', 'avx2'].flatMap(variant => ['en', 'zh-Hans'].map(targetLanguage => ({
        variant, targetLanguage, status: 'not-run', configuration: null, runnerOutput: null, technicalResults: null,
      }))),
    })),
  });
  // Retain a historical failed packet; never rewrite it to the new source or budget.
  const configuration = structuredClone(x.configurations[0]);
  configuration.executionLimits.wholeSongSeconds = 300;
  configuration.sourceFingerprintSha256 = 'f'.repeat(64);
  const output = structuredClone(x.outputPackets[0]);
  Object.assign(output, { outcome: null, complete: false, error: 'SYNTHETIC OperationCanceledException', elapsedMilliseconds: 300100 });
  const technical = { schemaVersion: 1, status: 'failed-or-incomplete', semanticStatus: 'not-evaluated', cancellationObserved: true,
    cleanupConfirmed: true, sourceIdentityUnchanged: true, modelIdentityUnchanged: true, runtimeIdentityUnchanged: true, configurationUnchanged: true };
  const observation = x.report.models[0].validation[0];
  observation.status = 'timed-out';
  const saveObservation = () => {
    for (const [key, packet] of [['configuration', configuration], ['runnerOutput', output], ['technicalResults', technical]]) {
      const name = `scripts/ai-model-qa/evidence/SYNTHETIC-EXPERIMENTAL-${key}.json`;
      x.write(name, json(packet)); observation[key] = { path: name, sha256: sha256(json(packet)) };
    }
    x.save();
  };
  saveObservation();
  return { ...x, observation, configuration, output, technical, saveObservation };
}

test('owner-accepted Beta preserves timeout and unexecuted evidence without semantic approval', t => {
  const x = experimentalExample(t); x.validate();
  assert.equal(x.scope.releaseVersion, experimentalBetaVersion);
  assert.deepEqual(publicationDecision(x.root, { now }), { authorized: true, semanticApproved: false, mode: experimentalBetaStatus });
  assert.equal(x.configuration.executionLimits.wholeSongSeconds, 300);
  assert.equal(x.output.complete, false);
  assert.equal(x.scope.executionLimits.wholeSongSeconds, 600);
  assert.equal(x.report.models[1].validation[0].status, 'not-run');
});

for (const [label, mutate, expected] of [
  ['future Beta', x => { x.write('RELEASE_VERSION', 'v0.3.1-beta.5'); x.scope.releaseVersion = 'v0.3.1-beta.5'; }, /only for v0.3.1-beta.4/],
  ['previous Beta', x => { x.write('RELEASE_VERSION', 'v0.3.1-beta.1'); x.scope.releaseVersion = 'v0.3.1-beta.1'; }, /only for v0.3.1-beta.4/],
  ['Stable', x => { x.write('RELEASE_VERSION', 'v0.3.1'); x.scope.releaseVersion = 'v0.3.1'; }, /only to a Beta/],
  ['missing owner acceptance', x => { delete x.report.userAcceptance; }, /Actual user acceptance/],
  ['no incomplete-validation acceptance', x => { x.report.userAcceptance.acceptsIncompleteModelValidation = false; }, /Explicit acceptance/],
  ['wrong accepted release', x => { x.report.userAcceptance.releaseVersion = 'v0.3.1-beta.1'; }, /exact Beta/],
  ['semantic pass claim', x => { x.report.semanticApproved = true; }, /must not claim/],
  ['approved model claim', x => { x.report.models[1].verdict = 'approved'; }, /unverified model/],
  ['missing model', x => { x.report.models.pop(); }, /Every shipping model/],
  ['missing variant', x => { x.report.models[1].validation.pop(); }, /both targets and CPU variants/],
  ['fabricated pass', x => { x.observation.status = 'pass'; }, /cannot be labeled pass/],
  ['unrun with evidence', x => { x.report.models[1].validation[0].runnerOutput = x.observation.runnerOutput; }, /Unexecuted validation/],
  ['cleanup defect waiver', x => { x.report.openDefects = ['cleanup failed']; }, /not a bug waiver/],
  ['operational limitation waiver', x => { x.report.acceptedLimitations[0].kind = 'cleanup'; }, /quality or latency/],
  ['missing runtime binding', x => { x.report.runtimeManifest = null; }, /reference is required/],
]) test(`experimental Beta rejects ${label}`, t => {
  const x = experimentalExample(t); mutate(x); x.save(); assert.throws(() => x.validate(), expected);
});

for (const [label, mutate, expected] of [
  ['timeout relabeled complete', x => { x.output.complete = true; }, /cannot be relabeled/],
  ['timeout relabeled unreviewed completion', x => { x.observation.status = 'complete-unreviewed'; }, /cannot be relabeled/],
  ['old budget overwritten', x => { x.configuration.executionLimits.wholeSongSeconds = 600; }, /predates/],
  ['wrong model identity', x => { x.output.model.sha256 = '0'.repeat(64); }, /model mismatch/],
  ['missing captured source identity', x => { delete x.configuration.sourceFingerprintSha256; }, /Captured input identity/],
  ['unconfirmed cancellation', x => { x.technical.cancellationObserved = false; }, /cannot waive operational/],
  ['unconfirmed cleanup', x => { x.technical.cleanupConfirmed = false; }, /cannot waive operational/],
  ['mutated model', x => { x.technical.modelIdentityUnchanged = false; }, /cannot waive operational/],
]) test(`experimental Beta preserves failed capture: ${label}`, t => {
  const x = experimentalExample(t); mutate(x); x.saveObservation(); assert.throws(() => x.validate(), expected);
});

test('experimental Beta still rejects changed current sources, runtime and exact release bindings', t => {
  const x = experimentalExample(t);
  x.runtimePayload['runtime-manifest.json'] += ' ';
  const directory = path.join(x.root, 'shipping-runtime');
  for (const [name, bytes] of Object.entries(x.runtimePayload)) x.write(`shipping-runtime/${name}`, bytes);
  assert.throws(() => x.validate({ runtimeManifestPath: path.join(directory, 'runtime-manifest.json') }), /differs from reviewed bytes/);
  assert.throws(() => x.validate({ releaseBundleDirectory: directory }), /Exact publication commit/);
  fs.appendFileSync(path.join(x.root, sourcePaths[0]), '\n// modified after acceptance\n');
  assert.throws(() => x.validate(), /stale/);
});

test('experimental owner acceptance cannot be promoted to ordinary semantic approval', t => {
  const x = experimentalExample(t); x.approval.status = 'approved'; x.save();
  assert.throws(() => x.validate(), /not a semantic review/);
  assert.equal(publicationDecision(example(t).root, { now }).semanticApproved, true);
});

test('resident digest matches the actual PowerShell producer across LF and CRLF source files', t => {
  const x = example(t);
  const producer = fs.readFileSync(path.join(repository, 'scripts/Build-AiLyricsRuntime.ps1'), 'utf8');
  const start = producer.indexOf('$helperSource =');
  const end = producer.indexOf('$manifest | ConvertTo-Json', start);
  assert.ok(start >= 0 && end > start, 'The runtime producer must retain its explicit digest block');
  // Execute only the producer digest block, never the native build/download path.
  const command = '$ErrorActionPreference = "Stop"; $root = $env:DROPSPACE_DIGEST_TEST_ROOT; ' +
    '$manifest = @{ resident = @{} };\n' + producer.slice(start, end) +
    '\n[Console]::WriteLine($manifest.resident.sourceSha256)';
  for (const endings of ['\n', '\r\n']) {
    for (const name of residentSourcePaths) {
      const normalized = fs.readFileSync(path.join(repository, name), 'utf8').replace(/\r\n/g, '\n');
      x.write(name, normalized.replace(/\n/g, endings));
    }
    const produced = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', command], {
      encoding: 'utf8', env: { ...process.env, DROPSPACE_DIGEST_TEST_ROOT: x.root },
    });
    assert.equal(produced.status, 0, produced.stderr || produced.error?.message);
    assert.equal(readScope(x.root).runtime.resident.sourceSha256, produced.stdout.trim());
  }
  x.write(residentSourcePaths[1], fs.readFileSync(path.join(x.root, residentSourcePaths[1]), 'utf8') + '// regression mutation\n');
  assert.notEqual(readScope(x.root).runtime.resident.sourceSha256, x.scope.runtime.resident.sourceSha256);
});

test('resident worker source mutation during a build cannot produce a trust manifest', t => {
  const x = example(t);
  const producer = fs.readFileSync(path.join(repository, 'scripts/Build-AiLyricsRuntime.ps1'), 'utf8');
  const initialStart = producer.indexOf('$helperInputs =');
  const initialEnd = producer.indexOf('function Assert-OwnedBuildDirectory', initialStart);
  const guardStart = producer.indexOf('foreach ($inputPath in $helperInputs)', producer.indexOf('$optimized ='));
  const guardEnd = producer.indexOf('# The completion-only build', guardStart);
  assert.ok(initialStart >= 0 && initialEnd > initialStart && guardStart >= 0 && guardEnd > guardStart);
  for (const mutate of [false, true]) {
    const command = '$ErrorActionPreference = "Stop"; $root = $env:DROPSPACE_DIGEST_TEST_ROOT;\n' +
      producer.slice(initialStart, initialEnd) +
      (mutate ? '\n[IO.File]::AppendAllText((Join-Path $root "tools/plain-lyrics-helper/main.cpp"), "// changed during build")\n' : '\n') +
      producer.slice(guardStart, guardEnd);
    const result = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', command], {
      encoding: 'utf8', env: { ...process.env, DROPSPACE_DIGEST_TEST_ROOT: x.root },
    });
    assert.ifError(result.error);
    if (mutate) {
      assert.notEqual(result.status, 0);
      assert.match(result.stderr, /Resident worker source changed during the build/);
    } else assert.equal(result.status, 0, result.stderr);
  }
});

test('scope selects both pinned plaintext Q8 profiles with separate resource arguments', t => {
  const x = example(t);
  assert.deepEqual(x.scope.shippingModels.map(model => model.id), ['hy-mt2-18-q8-plain-beta', 'hy-mt2-7b-q8-plain-beta']);
  assert.equal(x.scope.promptVersion, 'official-plain-per-line-v1');
  assert.equal(x.scope.outputSchema, 'host-mapped-id-text-v1');
  assert.equal(x.scope.backendId, 'hy-q8-plain-beta-v1');
  assert.equal(x.scope.executionLimits.wholeSongSeconds, 600);
  assert.equal(x.scope.executionLimits.perLineSeconds, 60);
  assert.equal(x.scope.executionLimits.memoryMiB, 3072);
  assert.deepEqual(x.scope.nativeArguments, ['--model', '$MODEL', '--mode', 'cpu']);
  assert.deepEqual(x.scope.modelProfiles['hy-mt2-7b-q8-plain-beta'].nativeArguments, [...x.scope.nativeArguments, '--model-profile', 'hy-mt2-7b-q8']);
  assert.equal(x.scope.modelProfiles['hy-mt2-7b-q8-plain-beta'].executionLimits.memoryMiB, 12288);
  assert.ok(!x.scope.samplerArguments.includes('-j'));
  assert.equal(x.scope.samplerArguments[x.scope.samplerArguments.indexOf('--seed') + 1], '42');
  assert.equal(x.scope.runtime.resident.profile, 'hy-q8-plain-resident-v1');
});

test('7B evidence cannot reuse a 1.8B runner capture', t => {
  const x = example(t);
  x.envelopes[2].outputs = structuredClone(x.envelopes[0].outputs);
  x.saveEnvelopes();
  assert.throws(() => x.validate(), /Runner output needs its own captured model identity/);
});

test('7B capture cannot silently use the ordinary startup arguments or memory budget', t => {
  for (const property of ['nativeArguments', 'executionLimits']) {
    const x = example(t);
    x.configurations[2][property] = structuredClone(x.configurations[0][property]);
    x.write(x.configurationPaths[2], json(x.configurations[2]));
    x.envelopes[2].configuration.sha256 = sha256(json(x.configurations[2]));
    x.saveEnvelopes();
    assert.throws(() => x.validate(), /Native configuration (execution limits mismatch|differs from actual production arguments)/);
  }
});

test('accepted Beta quality/latency limitations do not waive open defects', t => {
  const x = example(t);
  x.report.acceptedLimitations = [
    { id: 'synthetic-latency', kind: 'latency', summary: 'SYNTHETIC TEST ONLY: slower initial generation.' },
    { id: 'synthetic-quality', kind: 'quality', summary: 'SYNTHETIC TEST ONLY: a disclosed condition-meaning limitation.' },
  ];
  x.report.userAcceptance = { reference: 'SYNTHETIC TEST ONLY; not actual user approval', acceptedAt: '2026-09-30T00:00:00Z' };
  x.save(); x.validate();
  x.report.openDefects = ['synthetic unresolved lifecycle defect']; x.save();
  assert.throws(() => x.validate(), /not a bug waiver/);
  x.report.openDefects = []; delete x.report.userAcceptance; x.save();
  assert.throws(() => x.validate(), /Actual user acceptance reference/);
});

test('Beta limitation acceptance is not carried silently into Stable', t => {
  const x = example(t);
  x.write('RELEASE_VERSION', 'v0.3.1'); x.scope.releaseVersion = 'v0.3.1';
  x.report.acceptedLimitations = [{ id: 'synthetic', kind: 'quality', summary: 'SYNTHETIC TEST ONLY' }];
  x.report.userAcceptance = { reference: 'SYNTHETIC TEST ONLY', acceptedAt: '2026-09-30T00:00:00Z' };
  x.save(); assert.throws(() => x.validate(), /only to a Beta release/);
});

function saveOutputPacket(x, index) {
  x.write(x.outputPaths[index], json(x.outputPackets[index]));
  for (const envelope of x.envelopes) for (const output of envelope.outputs) if (output.path === x.outputPaths[index]) output.sha256 = sha256(json(x.outputPackets[index]));
  x.saveEnvelopes();
}
for (const [label, mutate, expected] of [
  ['old schema relabeled', output => output.schemaVersion = 1, /old QA cannot be relabeled/],
  ['made-up admission exclusion', output => output.admission[12].eligible = false, /independently audited/],
  ['wrong inference model', output => output.calls[0].verifiedModelSha256 = '0'.repeat(64), /Actual inference model hash/],
  ['wrong segment hash', output => output.calls[0].segmentSha256 = '0'.repeat(64), /every eligible semantic segment/],
  ['missing host memory observation', output => delete output.hostMemoryBefore, /host memory allowance/],
  ['missing actual line', output => output.calls.pop(), /every eligible semantic segment/],
  ['source-language bypass', output => output.calls.splice(0, 8), /every eligible semantic segment/],
  ['changed source text', output => output.calls[0].sourceText = 'different input', /every eligible semantic segment/],
  ['wrong host ID', output => output.calls[0].lineId = 99, /every eligible semantic segment/],
  ['extra prompt instruction', output => output.calls[0].prompt += '\nextra gold label', /actual production template/],
  ['empty output', output => output.calls[0].output = '', /runner-returned output/],
  ['failed whole song', output => output.outcome = 'Failed', /did not complete/],
  ['old raw output summary', output => output.kind = 'candidate-native-output', /actual production runner/],
]) test(`rejects ${label} despite rehashed runner-output evidence`, t => {
  const x = example(t); mutate(x.outputPackets[0]); saveOutputPacket(x, 0);
  assert.throws(() => x.validate(), expected);
});

for (const [targetIndex, lineId] of [[0, 1], [0, 3], [1, 37]]) test(`excluded original row ${lineId} for target ${targetIndex} cannot gain AI text in captured final results`, t => {
  const x = example(t);
  x.outputPackets[targetIndex].finalDocument.lines[lineId].secondary = 'AI rewrite';
  saveOutputPacket(x, targetIndex);
  assert.throws(() => x.validate(), /Excluded source row/);
});

test('cache replay must retain actual model identity, zero new inference and complete cold document', t => {
  for (const mutate of [cache => cache.model = { ...cache.model, id: 'wrong' },
    cache => cache.additionalInferenceCalls = 1, cache => cache.repeated.document.lines.pop()]) {
    const x = example(t); mutate(x.cachePackets[0]); x.saveCaches(); x.saveEnvelopes();
    assert.throws(() => x.validate(), /Cache replay/);
  }
});

test('fixture audit cannot label foreign Japanese as same-target to hide missing inference', t => {
  const x = example(t);
  const filename = path.join(x.root, x.scope.fixtureAdmission.path);
  const audit = JSON.parse(fs.readFileSync(filename));
  audit.targets.en[12].detectedLanguage = 'en'; audit.targets.en[12].eligible = false;
  fs.writeFileSync(filename, json(audit));
  assert.throws(() => readScope(x.root), /independent fixture annotation/);
});

test('unchanged copied lines remain neutral structural evidence rather than an invented semantic failure', t => {
  const x = example(t);
  x.outputPackets[0].outcome = 'NoUsefulTranslation';
  for (const call of x.outputPackets[0].calls) call.output = call.sourceText;
  for (const line of x.outputPackets[0].finalDocument.lines) { line.secondary = null; line.translationOrigin = 0; line.translationLanguage = null; }
  for (const result of [x.cachePackets[0].preflight, x.cachePackets[0].repeated]) { result.outcome = 1; result.document = structuredClone(x.outputPackets[0].finalDocument); }
  x.saveCaches();
  saveOutputPacket(x, 0); x.validate();
});

test('repository manifest is never approved by these synthetic tests', () => {
  const record = JSON.parse(fs.readFileSync(path.join(repository, approvalPath), 'utf8'));
  assert.equal(record.schemaVersion, 1);
  assert.ok(['pending', 'approved', experimentalBetaStatus].includes(record.status));
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
  for (const directory of ['src/DropSpace.Core/Lyrics', 'src/DropSpace.Infrastructure/Lyrics', 'src/DropSpace.App/Services/Media', 'src/DropSpace.App/Services/Diagnostics']) {
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
  ['old diagnostic schema', e => { e.schemaVersion = 1; }, /old QA cannot be relabeled/],
  ['wrong sampler', e => { e.samplerIdentity = 'different'; }, /samplerIdentity mismatch/],
  ['wrong capture method', e => { e.captureMethod = 'candidate-native-launcher'; }, /captureMethod mismatch/],
  ['unconfirmed native cancellation', e => { e.technicalChecks.cancellationObserved = false; }, /cancellationObserved/],
  ['cache extra inference', e => { e.technicalChecks.cacheAdditionalInferenceCalls = 1; }, /extra inference/],
  ['missing raw output references', e => { e.outputs = []; }, /output references/],
  ['wrong raw output hash', e => { e.outputs[0].sha256 = '0'.repeat(64); }, /hash mismatch/],
  ['summary used as raw output', e => { e.outputs[0].kind = 'summary'; }, /preserve runner-returned output/],
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
  ['old diagnostic configuration', c => { c.schemaVersion = 1; }, /configuration schema 3/],
  ['wrong production sampler arguments', c => { c.nativeArguments.push('-j', '{}'); }, /actual production arguments/],
  ['wrong runtime variant', c => { c.runtimeVariant = 'avx2'; }, /runtime variant mismatch/],
  ['wrong production budget', c => { c.executionLimits.wholeSongSeconds = 180; }, /execution limits mismatch/],
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
  assert.throws(() => x.validate(), /both shipping CPU runtime variants/);
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
    fs.appendFileSync(path.join(x.root, name), name === admissionPath ? '\n' : '\n// changed after review\n');
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
  assert.match(original, /All \{ get; \} = Array\.AsReadOnly\(new\[\] \{ [^}]+ \}\)/);
  x.write(catalog, original.replace(/All \{ get; \} = Array\.AsReadOnly\(new\[\] \{ [^}]+ \}\)/, 'All { get; } = BuildModels()'));
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

test('evidence symlinks cannot escape the repository', t => {
  const x = example(t);
  const outside = fs.mkdtempSync(path.join(os.tmpdir(), 'dropspace-gate-outside-'));
  t.after(() => fs.rmSync(outside, { recursive: true, force: true }));
  const bytes = fs.readFileSync(path.join(x.root, x.rawPath));
  fs.writeFileSync(path.join(outside, 'raw.txt'), bytes);
  fs.unlinkSync(path.join(x.root, x.rawPath));
  try { fs.symlinkSync(path.join(outside, 'raw.txt'), path.join(x.root, x.rawPath), 'file'); }
  catch (error) {
    if (process.platform !== 'win32' || !['EPERM', 'EACCES'].includes(error.code)) throw error;
    t.skip(`Actual Windows file-symlink creation failed: ${error.code}; no permission settings were changed`);
    return;
  }
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

test('shipping runtime must contain exactly the reviewed manifest and every reviewed binary/license byte', t => {
  const x = example(t);
  const runtimePath = path.join(x.root, 'shipping/runtime-manifest.json');
  for (const [name, bytes] of Object.entries(x.runtimePayload)) x.write(`shipping/${name}`, bytes);
  x.validate({ runtimeManifestPath: runtimePath });
  for (const [name, bytes] of Object.entries(x.runtimePayload)) {
    x.write(`shipping/${name}`, bytes + 'changed');
    assert.throws(() => x.validate({ runtimeManifestPath: runtimePath }), /runtime.*(bytes|files)/i);
    x.write(`shipping/${name}`, bytes);
  }
  x.write('shipping/runtime-manifest.json', json({ schemaVersion: 1, runtimeId: x.scope.runtime.id, sourceCommit: x.scope.runtime.sourceCommit }));
  assert.throws(() => x.validate({ runtimeManifestPath: runtimePath }), /differs from reviewed bytes/);
  x.write('shipping/runtime-manifest.json', x.runtimePayload['runtime-manifest.json']);
  x.write('shipping/extra.dll', 'unexpected dependency');
  assert.throws(() => x.validate({ runtimeManifestPath: runtimePath }), /exact reviewed runtime files/);
  fs.unlinkSync(path.join(x.root, 'shipping/extra.dll'));
  fs.unlinkSync(path.join(x.root, 'shipping/LICENSE-llama.cpp'));
  assert.throws(() => x.validate({ runtimeManifestPath: runtimePath }), /exact reviewed runtime files/);
  fs.unlinkSync(runtimePath);
  assert.throws(() => x.validate({ runtimeManifestPath: runtimePath }), /ENOENT/);
});

test('an artifact without the required license cannot become a shipping review', t => {
  const x = example(t);
  x.report.runtimeArtifact.files = x.report.runtimeArtifact.files.filter(file => file.path !== 'LICENSE-llama.cpp');
  x.save();
  assert.throws(() => x.validate(), /every shipping component and license/);
});

test('review requires exact immutable runtime artifact provenance', t => {
  const x = example(t);
  delete x.report.runtimeArtifact; x.save();
  assert.throws(() => x.validate(), /runtime artifact contract/);
});

test('producer identity inside the reviewed manifest cannot differ from artifact provenance', t => {
  const x = example(t);
  x.report.runtimeArtifact.checkoutCommit = 'c'.repeat(40); x.save();
  assert.throws(() => x.validate(), /producer metadata mismatch/);
});

test('publication binds live approval to exact final release files and source commit', t => {
  const x = example(t);
  const directory = path.join(x.root, 'release');
  const sourceCommit = 'd'.repeat(40);
  for (const name of ['DropSpace.exe', 'DropSpace-x64.msix', 'DropSpaceSetup.exe']) x.write(`release/${name}`, `synthetic ${name}`);
  const identity = name => fileIdentity(path.join(directory, name));
  const files = x.report.runtimeArtifact.files;
  const portable = { schemaVersion: 1, package: identity('DropSpace.exe'), files };
  const msix = { schemaVersion: 1, package: identity('DropSpace-x64.msix'), files };
  const installer = { schemaVersion: 1, kind: 'installer-payload', package: identity('DropSpaceSetup.exe'), installedPortable: portable.package };
  writeReleaseBinding(directory, { runtimeFiles: files, sourceCommit, portable, msix, installer });
  x.validate({ releaseBundleDirectory: directory, expectedCommit: sourceCommit });
  assert.throws(() => x.validate({ releaseBundleDirectory: directory }), /Exact publication commit/);
  assert.throws(() => x.validate({ releaseBundleDirectory: directory, expectedCommit: 'e'.repeat(40) }), /source commit mismatch/);
  fs.appendFileSync(path.join(directory, 'DropSpace.exe'), 'changed after inspection');
  assert.throws(() => x.validate({ releaseBundleDirectory: directory, expectedCommit: sourceCommit }), /Final release package changed/);
});

test('CLI fails closed for unknown arguments rather than skipping validation', () => {
  const result = spawnSync(process.execPath, ['scripts/test-ai-release-approval.mjs', '--publish=false'], { cwd: repository, encoding: 'utf8' });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /AI release blocked/);
});

test('CI tests every PR while Release gates explicit publication with a second publisher check', () => {
  const workflow = fs.readFileSync(path.join(repository, '.github/workflows/release.yml'), 'utf8');
  const validate = workflow.split('\n  validate-release:')[1].split('\n  ai-model-diagnostics:')[0];
  assert.match(validate, /- name: Test AI semantic release gate regressions\n        if: steps.ci-promotion.outputs.reuse != 'true'\n        run: node --test scripts\/test-ai-release-approval.test.mjs/);
  assert.match(validate, /- name: Enforce AI publication decision before building\n        if: github.event_name == 'workflow_dispatch' && inputs.publish == true\n        run: node scripts\/test-ai-release-approval.mjs/);
  assert.match(validate, /- name: Bind AI publication decision to exact shipping runtime bytes\n        if: github.event_name == 'workflow_dispatch' && inputs.publish == true\n        id: ai-release-approval/);
  assert.match(validate, /--runtime-manifest artifacts\/ai-runtime\/win-x64\/runtime-manifest.json\n          }\n          if \(\$LASTEXITCODE -ne 0\)/);
  assert.match(validate, /ai_semantic_approved: \$\{\{ steps.ai-release-approval.outputs.semantic_approved \}\}/);
  assert.match(validate, /ai_publication_authorized: \$\{\{ steps.ai-release-approval.outputs.authorized \}\}/);
  assert.match(validate, /node scripts\/test-ai-release-approval.mjs --github-output \$env:GITHUB_OUTPUT/);
  assert.ok(!validate.includes('"approved=true"'), 'Authorization must not fabricate semantic approval');
  for (const name of [
    'Validate native offline inference and cancellation',
    'Test installer /UPDATE, restart, upgrade, and uninstall lifecycle',
    'Smoke test portable EXE (en-US resource context) without public network access',
    'Smoke test portable EXE (zh-CN resource context) without public network access',
    'Inspect runtime bytes inside the final MSIX',
    'Enforce secret hygiene',
  ]) {
    const step = validate.split(`- name: ${name}\n`)[1]?.split('\n      - name:')[0];
    assert.ok(step, `Required operational gate is missing: ${name}`);
    assert.match(step, /if: steps.ci-promotion.outputs.reuse != 'true'/, `Only verified package reuse may skip ${name}`);
    assert.doesNotMatch(step, /continue-on-error:/);
  }
  const publish = workflow.split('\n  publish-release:')[1];
  assert.match(publish, /needs.validate-release.outputs.ai_publication_authorized == 'true'/);
  assert.match(publish, /needs: \[validate-release, sign-release\]/);
  assert.match(publish, /- name: Recheck AI publication decision\n        run: node scripts\/test-ai-release-approval.mjs --release-bundle artifacts\/release\n\n      - name: Publish immutable/);
  assert.match(validate, /Assert-DropSpacePublicationCommit \$env:EXPECTED_COMMIT \$env:ACTUAL_COMMIT/);
  assert.match(validate, /- name: Build verified offline AI inference runtime\n        if:.*ci-promotion.outputs.reuse != 'true'.*inputs.publish == true/);
  assert.match(validate, /- name: Retrieve the exact reviewed runtime\n        if:.*ci-promotion.outputs.reuse != 'true'.*inputs.publish == true/);
  assert.match(validate, /Get-ReviewedAiRuntime.ps1/);
  assert.match(validate, /record-bundle artifacts\/release runtime-directory/);
  const ci = fs.readFileSync(path.join(repository, '.github/workflows/ci.yml'), 'utf8');
  assert.match(ci, /pull_request:\n    branches: \[main\]/);
  assert.match(ci, /run: node --test scripts\/test-ai-release-approval.test.mjs/);
  assert.doesNotMatch(workflow, /\n  (?:push|pull_request):/);
  assert.match(validate, /Capture final plain-Hy production evidence on both runtime variants/);
  assert.match(validate, /foreach \(\$variant in @\('Baseline', 'Avx2'\)\)/);
  assert.match(validate, /Run-WindowsProductionEvidence.ps1 -RuntimeDirectory \$runtime -ModelPath \$env:DROPSPACE_AI_SMOKE_MODEL/);
  const sign = workflow.split('\n  sign-release:')[1].split('\n  publish-release:')[0];
  assert.match(sign, /github.ref == 'refs\/heads\/main'/);
  assert.match(sign, /verify-bundle artifacts\/release/);
  assert.match(sign, /record-bundle artifacts\/release reference-binding/);
  assert.match(sign, /Inspect-AiRuntimePayload.ps1 -MsixPath artifacts\/release\/DropSpace-x64.msix/);
  assert.equal((workflow.match(/artifacts\/release\/runtime-publication.json\n          if-no-files-found/g) ?? []).length, 2);
});

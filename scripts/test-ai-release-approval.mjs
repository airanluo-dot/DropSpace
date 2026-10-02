import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const approvalPath = 'scripts/ai-model-qa/release-approval.json';
export const fixturePath = 'scripts/ai-model-qa/inputs/source48.json';
// This list is code-owned, never selected by the approval record. Removing a
// prompt/parser/inference input from a manifest cannot weaken its binding.
export const sourcePaths = Object.freeze([
  'src/DropSpace.Core/Lyrics/AiLyricsModelCatalog.cs',
  'src/DropSpace.Core/Lyrics/LyricsModels.cs',
  'src/DropSpace.Core/Lyrics/LyricsTranslationPrompt.cs',
  'src/DropSpace.Core/Lyrics/LyricsTranslationOutput.cs',
  'src/DropSpace.Core/Lyrics/LyricsTranslationPolicy.cs',
  'src/DropSpace.Core/Lyrics/LyricsInferenceCircuit.cs',
  'src/DropSpace.Infrastructure/Lyrics/LlamaCompletionRunner.cs',
  'src/DropSpace.Infrastructure/Lyrics/LyricsTranslationCoordinator.cs',
  'src/DropSpace.Infrastructure/Lyrics/AiLyricsWorkLifetime.cs',
  'src/DropSpace.Infrastructure/Lyrics/AiLyricsCache.cs',
  'src/DropSpace.Infrastructure/Lyrics/LyricsCache.cs',
  'src/DropSpace.Infrastructure/Lyrics/LocalInferenceProcess.cs',
  'src/DropSpace.Infrastructure/Lyrics/WindowsInferenceProcess.cs',
  'src/DropSpace.Infrastructure/Lyrics/AiLyricsRuntimePackage.cs',
  'src/DropSpace.App/Services/Media/AiLyricsService.cs',
  'scripts/Build-AiLyricsRuntime.ps1',
  'scripts/ai-model-qa/Program.cs',
  'scripts/ai-model-qa/Run-WindowsModelQa.ps1',
  'scripts/ai-model-qa/WindowsModelQa.csproj',
]);
export const maximumApprovalAgeMs = 30 * 24 * 60 * 60 * 1000;
export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const hashPattern = /^[a-f0-9]{64}$/;
const rootDirectory = fileURLToPath(new URL('../', import.meta.url));
const readText = (root, name) => fs.readFileSync(path.join(root, name), 'utf8').replace(/\r\n/g, '\n');
const readJson = (root, name) => JSON.parse(readText(root, name));
const nonempty = (value, label) => assert.ok(typeof value === 'string' && value.trim().length > 0, `${label} is required`);

function singleMatch(text, pattern, label) {
  const matches = [...text.matchAll(pattern)];
  assert.equal(matches.length, 1, `${label} must have exactly one recognizable production declaration`);
  return matches[0][1];
}

export function readScope(root) {
  const catalog = readText(root, sourcePaths[0]);
  // Intentionally parse the current restricted catalog shape; a refactor must
  // update this extractor and its tests instead of silently approving no models.
  const descriptors = [...catalog.matchAll(/public static AiLyricsModelDescriptor (\w+) \{ get; \} = new\(\s*"([^"]+)",\s*"[^"]+",\s*new Uri\("[^"]+"\),\s*[\d_]+,\s*"([a-f0-9]{64})"\);/g)];
  assert.ok(descriptors.length > 0, 'No recognizable shipping model descriptors');
  const all = singleMatch(catalog, /All \{ get; \} = Array\.AsReadOnly\(new\[\] \{ ([^}]+) \}\);/g, 'Shipping model list');
  const names = all.split(',').map(name => name.trim());
  assert.equal(new Set(names).size, names.length, 'Duplicate shipping model declarations');
  const shippingModels = names.map(name => {
    const descriptor = descriptors.filter(match => match[1] === name);
    assert.equal(descriptor.length, 1, `Unknown shipping model ${name}`);
    return { id: descriptor[0][2], sha256: descriptor[0][3] };
  }).sort((a, b) => a.id.localeCompare(b.id));
  assert.equal(new Set(shippingModels.map(model => model.id)).size, shippingModels.length, 'Duplicate shipping model IDs');
  const runtimeBuild = readText(root, 'scripts/Build-AiLyricsRuntime.ps1');
  const files = sourcePaths.map(name => ({ path: name, sha256: sha256(readText(root, name)) }));
  return {
    releaseVersion: readText(root, 'RELEASE_VERSION').trim(),
    shippingModels,
    runtime: {
      id: singleMatch(runtimeBuild, /runtimeId = '([^']+)'/g, 'Runtime identity'),
      sourceCommit: singleMatch(runtimeBuild, /^\$commit = '([a-f0-9]{40})'/gm, 'Runtime source commit'),
    },
    promptVersion: singleMatch(readText(root, 'src/DropSpace.Core/Lyrics/LyricsTranslationPrompt.cs'), /public const string Version = "([^"]+)";/g, 'Prompt/cache version'),
    sources: { algorithm: 'sha256-utf8-lf-v1', files, sha256: sha256(JSON.stringify(files)) },
    fixture: { path: fixturePath, sha256: sha256(fs.readFileSync(path.join(root, fixturePath))) },
  };
}

function readEvidence(root, reference, label) {
  assert.ok(reference && typeof reference === 'object', `${label} reference is required`);
  assert.match(reference.sha256 ?? '', hashPattern, `${label} SHA256 is required`);
  assert.ok(typeof reference.path === 'string' && reference.path.startsWith('scripts/ai-model-qa/evidence/'), `${label} must be repository evidence`);
  assert.ok(!reference.path.includes('\\') && reference.path.split('/').every(part => part && part !== '.' && part !== '..'), `${label} path must be canonical`);
  const repositoryRoot = fs.realpathSync(root);
  const evidenceRoot = fs.realpathSync(path.join(root, 'scripts/ai-model-qa/evidence'));
  const rootRelative = path.relative(repositoryRoot, evidenceRoot);
  assert.ok(rootRelative && !rootRelative.startsWith('..') && !path.isAbsolute(rootRelative), `${label} evidence directory escapes repository`);
  const filename = fs.realpathSync(path.join(root, reference.path));
  const relative = path.relative(evidenceRoot, filename);
  assert.ok(relative && !relative.startsWith('..') && !path.isAbsolute(relative), `${label} escapes evidence directory`);
  const bytes = fs.readFileSync(filename);
  assert.ok(bytes.toString('utf8').trim().length > 0, `${label} must not be empty`);
  assert.equal(sha256(bytes), reference.sha256, `${label} hash mismatch`);
  return bytes;
}

function timestamp(value, label) {
  assert.ok(typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/.test(value), `${label} must be an explicit UTC timestamp`);
  const parsed = Date.parse(value);
  assert.ok(Number.isFinite(parsed) && new Date(parsed).toISOString() === value.replace('Z', '.000Z'), `${label} is invalid`);
  return parsed;
}

export function validateApproval(root, { now = Date.now(), runtimeManifestPath } = {}) {
  const approval = readJson(root, approvalPath);
  assert.equal(approval.schemaVersion, 1, 'Unsupported AI approval schema');
  assert.equal(approval.status, 'approved', 'AI semantic release approval is pending or absent; structural success is not approval');
  const scope = readScope(root);
  nonempty(scope.releaseVersion, 'Release version');
  assert.deepEqual(approval.scope, scope, 'AI approval is stale: shipping model, runtime, prompt/parser sources, release or fixture changed');
  const report = JSON.parse(readEvidence(root, approval.review, 'Semantic review').toString('utf8'));
  assert.equal(report.schemaVersion, 1, 'Unsupported semantic review schema');
  assert.equal(report.kind, 'semantic-review', 'Native diagnostics are not a semantic review');
  assert.equal(report.verdict, 'approved', 'Semantic review has not approved release');
  assert.deepEqual(report.scope, scope, 'Semantic review is bound to different release inputs');
  nonempty(report.reviewedBy, 'Actual semantic reviewer identity');
  nonempty(report.summary, 'Semantic review rationale');
  const reviewedAt = timestamp(report.reviewedAt, 'Review time');
  const expiresAt = timestamp(report.expiresAt, 'Approval expiry');
  assert.ok(Number.isFinite(now) && reviewedAt <= now, 'Semantic review cannot be future-dated');
  assert.ok(expiresAt > now && expiresAt > reviewedAt, 'Semantic approval has expired');
  assert.ok(expiresAt - reviewedAt <= maximumApprovalAgeMs, 'Semantic approval cannot last more than 30 days');
  assert.ok(Array.isArray(report.models), 'Per-model semantic reviews are required');
  assert.deepEqual(report.models.map(model => ({ id: model.id, sha256: model.sha256 })), scope.shippingModels, 'Every shipping model needs semantic approval');
  for (const model of report.models) {
    assert.equal(model.verdict, 'approved', `Model ${model.id} is not semantically approved`);
    nonempty(model.summary, `Model ${model.id} review rationale`);
    assert.ok(Array.isArray(model.evidence) && model.evidence.length > 0, `Model ${model.id} needs native evidence`);
    for (const evidence of model.evidence) {
      assert.equal(evidence.kind, 'native-output', 'Source inspection and diagnostic summaries alone are not runtime evidence');
      assert.equal(evidence.fixtureSha256, scope.fixture.sha256, 'Runtime evidence must use the pinned original QA fixture');
      readEvidence(root, evidence, `Model ${model.id} native evidence`);
    }
  }
  if (runtimeManifestPath !== undefined) {
    const runtime = JSON.parse(fs.readFileSync(runtimeManifestPath, 'utf8').replace(/^\uFEFF/, ''));
    assert.equal(runtime.schemaVersion, 1, 'Unsupported built runtime manifest');
    assert.equal(runtime.runtimeId, scope.runtime.id, 'Built runtime identity differs from semantic review');
    assert.equal(runtime.sourceCommit, scope.runtime.sourceCommit, 'Built runtime source differs from semantic review');
  }
  return scope;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const args = process.argv.slice(2);
    if (args.length === 1 && args[0] === '--print-scope') {
      // Read-only preparation aid. It never writes or approves a manifest.
      console.log(JSON.stringify(readScope(rootDirectory), null, 2));
    } else {
      assert.ok(args.length === 0 || (args.length === 2 && args[0] === '--runtime-manifest'), 'Usage: node scripts/test-ai-release-approval.mjs [--runtime-manifest PATH | --print-scope]');
      validateApproval(rootDirectory, { runtimeManifestPath: args[1] });
      console.log('AI semantic approval and evidence bindings are valid. The recorded semantic review, not this structural check, establishes quality.');
    }
  } catch (error) {
    console.error(`AI release blocked: ${error.message}`);
    process.exitCode = 1;
  }
}

import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { compareInventory, directoryInventory, validateArtifactContract, validateInventory, validateRuntimeProducer, verifyReleaseBinding } from './ai-runtime-publication.mjs';

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
  'src/DropSpace.Infrastructure/Lyrics/Ct2HelperAdapter.cs',
  'src/DropSpace.Infrastructure/Lyrics/Ct2PrivatePackage.cs',
  'src/DropSpace.App/Services/Media/AiLyricsService.cs',
  // Upstream identity/selection/provider parsing and target/display propagation.
  'src/DropSpace.Core/Lyrics/LyricsParser.cs',
  'src/DropSpace.Core/Lyrics/LyricsMatcher.cs',
  'src/DropSpace.Core/Lyrics/LyricsDisplayPolicy.cs',
  'src/DropSpace.Core/Lyrics/LyricsReloadPolicy.cs',
  'src/DropSpace.Core/Media/MediaModels.cs',
  'src/DropSpace.Core/Media/MediaPlaybackClock.cs',
  'src/DropSpace.Core/Media/MediaProcessIdentityPolicy.cs',
  'src/DropSpace.Core/Models/AppSettings.cs',
  'src/DropSpace.Core/Models/NativeIslandSettings.cs',
  'src/DropSpace.Core/Models/NativeIslandSettingsPolicy.cs',
  'src/DropSpace.Core/Models/SettingsChangePolicy.cs',
  'src/DropSpace.Core/Models/SettingsValidationPolicy.cs',
  'src/DropSpace.Core/Models/SettingsMigration14.cs',
  'src/DropSpace.Core/Policies/AppLanguagePolicy.cs',
  'src/DropSpace.Core/Abstractions/IAppStringLocalizer.cs',
  'src/DropSpace.Infrastructure/Lyrics/AmllLyricsProvider.cs',
  'src/DropSpace.Infrastructure/Lyrics/KugouLyricsProvider.cs',
  'src/DropSpace.Infrastructure/Lyrics/LocalLrcLyricsProvider.cs',
  'src/DropSpace.Infrastructure/Lyrics/LrclibLyricsProvider.cs',
  'src/DropSpace.Infrastructure/Lyrics/NetEaseLyricsProvider.cs',
  'src/DropSpace.Infrastructure/Lyrics/QqMusicLyricsProvider.cs',
  'src/DropSpace.Infrastructure/Lyrics/LyricsService.cs',
  'src/DropSpace.Infrastructure/Lyrics/LyricsHttpClient.cs',
  'src/DropSpace.Infrastructure/Lyrics/LyricsProviderRegistry.cs',
  'src/DropSpace.Infrastructure/Lyrics/AiModelPackageService.cs',
  'src/DropSpace.Infrastructure/Settings/JsonSettingsService.cs',
  'src/DropSpace.Infrastructure/Settings/SettingsIoPolicy.cs',
  'src/DropSpace.App/Services/Media/MediaExperienceService.cs',
  'src/DropSpace.App/Services/Media/WindowsMediaSessionService.cs',
  'src/DropSpace.App/Services/Media/BoundedMediaOperation.cs',
  'src/DropSpace.App/App.xaml.cs',
  'src/DropSpace.App/Services/AppLanguageService.cs',
  'src/DropSpace.App/Services/ResourceStringLocalizer.cs',
  'src/DropSpace.App/Services/SettingsApplicationCoordinator.cs',
  'src/DropSpace.App/ViewModels/MediaViewModel.cs',
  'src/DropSpace.App/ViewModels/MainViewModel.cs',
  'src/DropSpace.App/ViewModels/NativeSettingsEditor.cs',
  'src/DropSpace.App/Views/Music/MusicPage.cs',
  'src/DropSpace.App/Views/Music/AiLyricsSettingsCard.cs',
  'src/DropSpace.App/Views/Island/MediaCompactView.xaml.cs',
  'src/DropSpace.App/Views/Island/MediaCompactView.xaml',
  'src/DropSpace.App/Views/Island/MediaExpandedView.xaml.cs',
  'src/DropSpace.App/Views/Island/MediaExpandedView.xaml',
  'src/DropSpace.Core/Lyrics/LyricsGlowEnvelope.cs',
  'src/DropSpace.Core/Lyrics/LyricsGlowPolicy.cs',
  'src/DropSpace.Infrastructure/Lyrics/LocalInferenceExecutionException.cs',
  'src/DropSpace.App/Services/Media/MediaApplicationIconService.cs',
  'src/DropSpace.App/Services/Media/MediaArtworkService.cs',
  'src/DropSpace.App/Services/Media/MediaEventSubscription.cs',
  'src/DropSpace.App/Services/Media/MediaProcessResolver.cs',
  'src/DropSpace.Core/Lyrics/LyricsPreviewPolicy.cs',
  'src/DropSpace.App/Views/Island/ExpandedIslandMusicView.xaml',
  'src/DropSpace.App/Views/Island/ExpandedIslandMusicView.xaml.cs',
  'src/DropSpace.App/Services/Media/MediaSoftRestartOperation.cs',
  'src/DropSpace.App/Services/Media/RetirableMediaWork.cs',
  'src/DropSpace.App/Services/Media/MediaSessionOwner.cs',
  'src/DropSpace.App/Services/Media/MediaSubscriptionAdmission.cs',
  'src/DropSpace.App/OverlayWindow.xaml',
  'scripts/ai-model-qa/profiles/minimal-target-only.json',
  'scripts/Build-AiLyricsRuntime.ps1',
  'scripts/ai-model-qa/Program.cs',
  'scripts/ai-model-qa/Run-WindowsModelQa.ps1',
  'scripts/ai-model-qa/WindowsModelQa.csproj',
  // Embedding/packaging declarations are also part of the reviewed shipping input.
  'src/DropSpace.App/DropSpace.App.csproj',
  'scripts/Build-PortableExe.ps1',
  'scripts/Build-UnsignedPackage.ps1',
  'scripts/Build-Installer.ps1',
  'scripts/Collect-AiRuntimeNotices.ps1',
  'tools/ct2-helper/helper.py',
  'tools/ct2-helper/build.ps1',
  'tools/ct2-helper/dependencies.schema.json',
  'tools/ct2-helper/private-package-manifest.schema.json',
]);
export const productionPromptProfile = 'production';
export const productionOutputSchema = 'production-id-text-json-v1';
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
  const descriptors = [...catalog.matchAll(/public static AiLyricsModelDescriptor (\w+) \{ get; \} = new\(\s*"([^"]+)",\s*"[^"]+",\s*new Uri\("[^"]+"\),\s*([\d_]+),\s*"([a-f0-9]{64})"\);/g)];
  assert.ok(descriptors.length > 0, 'No recognizable shipping model descriptors');
  const all = singleMatch(catalog, /All \{ get; \} = Array\.AsReadOnly\(new\[\] \{ ([^}]+) \}\);/g, 'Shipping model list');
  const names = all.split(',').map(name => name.trim());
  assert.equal(new Set(names).size, names.length, 'Duplicate shipping model declarations');
  const shippingModels = names.map(name => {
    const descriptor = descriptors.filter(match => match[1] === name);
    assert.equal(descriptor.length, 1, `Unknown shipping model ${name}`);
    const bytes = Number(descriptor[0][3].replaceAll('_', ''));
    assert.ok(Number.isSafeInteger(bytes) && bytes > 0, 'Invalid shipping model byte count');
    return { id: descriptor[0][2], sha256: descriptor[0][4], bytes };
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
    promptProfile: productionPromptProfile,
    outputSchema: productionOutputSchema,
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

function componentIdentity(component, executable) {
  assert.equal(component?.executable, executable, `Unexpected runtime component ${executable}`);
  assert.match(component.sha256 ?? '', hashPattern, `Runtime component ${executable} SHA256 is required`);
  assert.ok(Number.isSafeInteger(component.bytes) && component.bytes > 0, `Runtime component ${executable} byte count is required`);
  return { sha256: component.sha256, bytes: component.bytes };
}

function readReviewedRuntime(root, report, scope) {
  const manifestBytes = readEvidence(root, report.runtimeManifest, 'Reviewed runtime manifest');
  const runtime = JSON.parse(manifestBytes.toString('utf8').replace(/^\uFEFF/, ''));
  assert.equal(runtime.schemaVersion, 1, 'Unsupported reviewed runtime schema');
  assert.equal(runtime.runtimeId, scope.runtime.id, 'Reviewed runtime identity mismatch');
  assert.equal(runtime.sourceCommit, scope.runtime.sourceCommit, 'Reviewed runtime source mismatch');
  const reviewed = {
    manifestSha256: report.runtimeManifest.sha256,
    baseline: componentIdentity(runtime, 'llama-completion.exe'),
    avx2: componentIdentity(runtime.avx2, 'llama-completion-avx2.exe'),
    tokenizer: componentIdentity(runtime.tokenizer, 'llama-tokenize.exe'),
  };
  const artifact = validateArtifactContract(report.runtimeArtifact);
  const files = validateInventory(artifact.files);
  // Current llama adapter. Retrieval and final-package binding use the generic
  // inventory; another backend must explicitly declare its required components.
  assert.deepEqual(files.map(file => file.path).sort(), [
    'LICENSE-llama.cpp', 'llama-completion-avx2.exe', 'llama-completion.exe',
    'llama-tokenize.exe', 'runtime-manifest.json',
  ].sort(), 'Reviewed runtime inventory must contain every shipping component and license');
  for (const [name, identity] of [
    ['runtime-manifest.json', { sha256: report.runtimeManifest.sha256, bytes: manifestBytes.length }],
    ['llama-completion.exe', reviewed.baseline], ['llama-completion-avx2.exe', reviewed.avx2],
    ['llama-tokenize.exe', reviewed.tokenizer],
  ]) {
    const file = files.find(item => item.path === name);
    assert.deepEqual({ sha256: file.sha256, bytes: file.bytes }, identity, `Reviewed runtime inventory mismatch: ${name}`);
  }
  validateRuntimeProducer(runtime, artifact);
  return { ...reviewed, files, artifact };
}

function validateNativeEvidence(root, reference, model, scope, runtime, reviewedAt) {
  assert.equal(reference.kind, 'native-output', 'Source inspection and diagnostic summaries alone are not runtime evidence');
  assert.equal(reference.fixtureSha256, scope.fixture.sha256, 'Runtime evidence must use the pinned original QA fixture');
  const envelope = JSON.parse(readEvidence(root, reference, `Model ${model.id} native evidence`).toString('utf8'));
  assert.equal(envelope.schemaVersion, 1, 'Unsupported native evidence schema');
  assert.equal(envelope.kind, 'native-output', 'Native evidence must be a provenance envelope');
  assert.deepEqual(envelope.model, { id: model.id, sha256: model.sha256, bytes: model.bytes }, 'Native evidence model identity mismatch');
  assert.equal(envelope.fixtureSha256, scope.fixture.sha256, 'Native evidence fixture mismatch');
  assert.equal(envelope.promptVersion, scope.promptVersion, 'Native evidence prompt version mismatch');
  assert.equal(envelope.promptProfile, productionPromptProfile, 'Only the production prompt profile can support shipping approval');
  assert.equal(envelope.outputSchema, productionOutputSchema, 'Native evidence must use the production output schema');
  assert.equal(envelope.sourceFingerprintSha256, scope.sources.sha256, 'Native evidence source fingerprint mismatch');
  assert.equal(envelope.platform, 'windows-x64', 'Native evidence must use the shipping Windows x64 platform');
  assert.ok(timestamp(envelope.executedAt, 'Native evidence execution time') <= reviewedAt, 'Native evidence postdates its semantic review');
  assert.equal(envelope.runtime?.manifestSha256, runtime.manifestSha256, 'Native evidence runtime manifest mismatch');
  const variant = envelope.runtime.variant;
  assert.ok(['baseline', 'avx2'].includes(variant), 'Native evidence runtime variant is required');
  assert.deepEqual(envelope.runtime.completion, runtime[variant], 'Native evidence completion hash/bytes mismatch');
  assert.deepEqual(envelope.runtime.tokenizer, runtime.tokenizer, 'Native evidence tokenizer hash/bytes mismatch');
  // Read the preserved configuration itself. A production label on an envelope
  // cannot relabel a minimal-target-only/loader-only run after the fact.
  const configuration = JSON.parse(readEvidence(root, envelope.configuration, `Model ${model.id} native configuration`).toString('utf8').replace(/^\uFEFF/, ''));
  assert.equal(configuration.promptProfile, productionPromptProfile, 'Native configuration is not the production prompt profile');
  assert.equal(configuration.outputSchema, productionOutputSchema, 'Native configuration lacks the production output schema identity');
  assert.equal(configuration.loadOnly, false, 'Loader-only configuration is not shipping quality evidence');
  assert.deepEqual({ id: configuration.modelId, sha256: configuration.modelSha256, bytes: configuration.modelBytes }, envelope.model, 'Native configuration model identity mismatch');
  assert.equal(configuration.executableSha256, runtime[variant].sha256, 'Native configuration completion hash mismatch');
  assert.equal(configuration.tokenizerSha256, runtime.tokenizer.sha256, 'Native configuration tokenizer hash mismatch');
  assert.ok(Array.isArray(envelope.outputs) && envelope.outputs.length > 0, 'Native evidence needs original output references');
  const targets = new Set();
  for (const output of envelope.outputs) {
    assert.equal(output.kind, 'raw-output', 'Native evidence output must be preserved raw output');
    assert.ok(['en', 'zh-Hans'].includes(output.targetLanguage), 'Native evidence output target is unsupported');
    readEvidence(root, output, `Model ${model.id} raw output`);
    targets.add(output.targetLanguage);
  }
  assert.deepEqual([...targets].sort(), ['en', 'zh-Hans'], 'Native evidence needs both shipping target languages');
  return variant;
}

function timestamp(value, label) {
  assert.ok(typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/.test(value), `${label} must be an explicit UTC timestamp`);
  const parsed = Date.parse(value);
  assert.ok(Number.isFinite(parsed) && new Date(parsed).toISOString() === value.replace('Z', '.000Z'), `${label} is invalid`);
  return parsed;
}

export function validateApproval(root, { now = Date.now(), runtimeManifestPath, releaseBundleDirectory, expectedCommit } = {}) {
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
  // Accountability metadata within the trusted repository, not identity authentication.
  nonempty(report.reviewedBy, 'Recorded semantic reviewer identity');
  nonempty(report.summary, 'Semantic review rationale');
  const reviewedAt = timestamp(report.reviewedAt, 'Review time');
  const expiresAt = timestamp(report.expiresAt, 'Approval expiry');
  assert.ok(Number.isFinite(now) && reviewedAt <= now, 'Semantic review cannot be future-dated');
  assert.ok(expiresAt > now && expiresAt > reviewedAt, 'Semantic approval has expired');
  assert.ok(expiresAt - reviewedAt <= maximumApprovalAgeMs, 'Semantic approval cannot last more than 30 days');
  const reviewedRuntime = readReviewedRuntime(root, report, scope);
  assert.ok(Array.isArray(report.models), 'Per-model semantic reviews are required');
  assert.deepEqual(report.models.map(model => ({ id: model.id, sha256: model.sha256, bytes: model.bytes })), scope.shippingModels, 'Every shipping model needs semantic approval');
  for (const model of report.models) {
    assert.equal(model.verdict, 'approved', `Model ${model.id} is not semantically approved`);
    nonempty(model.summary, `Model ${model.id} review rationale`);
    assert.ok(Array.isArray(model.evidence) && model.evidence.length > 0, `Model ${model.id} needs native evidence`);
    const variants = new Set(model.evidence.map(evidence =>
      validateNativeEvidence(root, evidence, model, scope, reviewedRuntime, reviewedAt)));
    assert.deepEqual([...variants].sort(), ['avx2', 'baseline'], `Model ${model.id} needs both shipping runtime variants`);
  }

  if (runtimeManifestPath !== undefined) {
    assert.equal(path.basename(runtimeManifestPath), 'runtime-manifest.json', 'Unexpected shipping runtime manifest path');
    assert.equal(sha256(fs.readFileSync(runtimeManifestPath)), reviewedRuntime.manifestSha256, 'Shipping runtime manifest differs from reviewed bytes');
    compareInventory(directoryInventory(path.dirname(runtimeManifestPath)), reviewedRuntime.files, 'Shipping runtime');
  }
  if (releaseBundleDirectory !== undefined) {
    assert.match(expectedCommit ?? '', /^[a-f0-9]{40}$/, 'Exact publication commit is required');
    verifyReleaseBinding(releaseBundleDirectory, { expectedInventory: reviewedRuntime.files, expectedCommit });
  }
  return scope;
}

export function readApprovedRuntimeContract(root, options = {}) {
  validateApproval(root, options);
  const approval = readJson(root, approvalPath);
  const report = JSON.parse(readEvidence(root, approval.review, 'Semantic review').toString('utf8'));
  return report.runtimeArtifact;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const args = process.argv.slice(2);
    if (args.length === 1 && args[0] === '--print-scope') {
      // Read-only preparation aid. It never writes or approves a manifest.
      console.log(JSON.stringify(readScope(rootDirectory), null, 2));
    } else {
      assert.ok(args.length === 0 || (args.length === 2 && ['--runtime-manifest', '--release-bundle', '--export-runtime-contract'].includes(args[0])), 'Usage: node scripts/test-ai-release-approval.mjs [--runtime-manifest PATH | --release-bundle DIRECTORY | --export-runtime-contract PATH | --print-scope]');
      if (args[0] === '--export-runtime-contract') {
        const contract = readApprovedRuntimeContract(rootDirectory);
        fs.mkdirSync(path.dirname(path.resolve(args[1])), { recursive: true });
        fs.writeFileSync(args[1], JSON.stringify(contract, null, 2) + '\n');
      } else {
        validateApproval(rootDirectory, {
          runtimeManifestPath: args[0] === '--runtime-manifest' ? args[1] : undefined,
          releaseBundleDirectory: args[0] === '--release-bundle' ? args[1] : undefined,
          expectedCommit: process.env.GITHUB_SHA,
        });
      }
      console.log('AI semantic approval and evidence bindings are valid. The recorded semantic review, not this structural check, establishes quality.');
    }
  } catch (error) {
    console.error(`AI release blocked: ${error.message}`);
    process.exitCode = 1;
  }
}

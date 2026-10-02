import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { compareInventory, directoryInventory, validateArtifactContract, validateInventory, validateRuntimeProducer, verifyReleaseBinding } from './ai-runtime-publication.mjs';

export const approvalPath = 'scripts/ai-model-qa/release-approval.json';
export const fixturePath = 'scripts/ai-model-qa/inputs/source48.json';
export const residentSourcePaths = Object.freeze([
  'tools/plain-lyrics-helper/CMakeLists.txt',
  'tools/plain-lyrics-helper/gpu-policy.h',
  'tools/plain-lyrics-helper/main.cpp',
]);
// This list is code-owned, never selected by the approval record. Removing a
// prompt/parser/inference input from a manifest cannot weaken its binding.
export const sourcePaths = Object.freeze([
  'src/DropSpace.Core/Lyrics/AiLyricsModelCatalog.cs',
  'src/DropSpace.Core/Lyrics/LyricsModels.cs',
  'src/DropSpace.Core/Lyrics/LyricsTranslationPrompt.cs',
  'src/DropSpace.Core/Lyrics/LyricsTranslationOutput.cs',
  'src/DropSpace.Core/Lyrics/LyricsTranslationPolicy.cs',
  'src/DropSpace.Core/Lyrics/LyricsInferenceCircuit.cs',
  'src/DropSpace.Core/Lyrics/PlainHyLyricsProtocol.cs',
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
  'src/DropSpace.Infrastructure/Lyrics/AiLyricsBackend.cs',
  'src/DropSpace.Infrastructure/Lyrics/Ct2LyricsPipeline.cs',
  'src/DropSpace.Infrastructure/Lyrics/Ct2PackageInstaller.cs',
  'src/DropSpace.Infrastructure/Lyrics/PlainHyLyricsBackend.cs',
  'src/DropSpace.Infrastructure/Lyrics/LyricsTranslationProgress.cs',
  'src/DropSpace.Infrastructure/Lyrics/PersistentPlainLyricsRunner.cs',
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
  'src/DropSpace.App/Services/Diagnostics/MusicVisualSmoke.cs',
  'src/DropSpace.App/Services/Diagnostics/MusicVisualSmokeOptions.cs',
  'src/DropSpace.App/Views/MainPage.xaml',
  'src/DropSpace.App/Views/MainPage.xaml.cs',
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
  'scripts/plain-hy-production-evidence/PlainHyProductionEvidence.csproj',
  'scripts/plain-hy-production-evidence/packages.lock.json',
  'scripts/plain-hy-production-evidence/Program.cs',
  'scripts/plain-hy-production-evidence/ContractTests.cs',
  'scripts/plain-hy-production-evidence/Run-WindowsProductionEvidence.ps1',
  'scripts/plain-hy-production-evidence/Test-ProductionEvidenceContract.ps1',
  'scripts/Get-AiLyricsSmokeModel.ps1',
  'scripts/Test-AiLyricsRuntime.ps1',
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
  ...residentSourcePaths,
]);
export const productionPromptProfile = 'production-plain-hy';
export const productionOutputSchema = 'host-mapped-id-text-v1';
export const productionCaptureMethod = 'PlainHyLyricsBackend+PlainHyLyricsCoordinator+PersistentPlainLyricsRunner.RunPlainAsync';
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
  const protocol = readText(root, 'src/DropSpace.Core/Lyrics/PlainHyLyricsProtocol.cs');
  const runner = readText(root, 'src/DropSpace.Infrastructure/Lyrics/PersistentPlainLyricsRunner.cs');
  const legacyRunner = readText(root, 'src/DropSpace.Infrastructure/Lyrics/LlamaCompletionRunner.cs');
  const stringConstant = name => JSON.parse('"' + singleMatch(protocol, new RegExp(`public const string ${name} = "((?:[^"\\\\]|\\\\.)*)";`, 'g'), `Plain protocol ${name}`) + '"');
  const integerConstant = name => Number(singleMatch(protocol, new RegExp(`public const int ${name} = ([0-9_]+);`, 'g'), `Plain protocol ${name}`).replaceAll('_', ''));
  assert.equal(stringConstant('HostMappingVersion'), productionOutputSchema, 'Production host mapping changed; update the evidence adapter explicitly');
  // Check the actual worker launch builder and parse the compiled helper's frozen
  // sampler flags. A legacy one-shot launch is no longer production evidence.
  assert.match(runner, /BuildArguments\(string modelPath, bool gpu\) =>\s*Array\.AsReadOnly\(new\[\] \{ "--model", Path\.GetFullPath\(modelPath\), "--mode", gpu \? "vulkan" : "cpu" \}\);/, 'Unrecognized resident startup arguments');
  const nativeArguments = ['--model', '$MODEL', '--mode', 'cpu'];
  const helper = readText(root, residentSourcePaths[2]);
  const argumentBody = singleMatch(helper, /std::vector<std::string> args = \{([\s\S]*?)\};/g, 'Compiled resident sampler arguments');
  const samplerArguments = argumentBody.split(',').map(part => {
    const value = part.trim();
    if (value === 'argv[2]') return '$MODEL';
    assert.ok(/^"(?:[^"\\]|\\.)*"$/.test(value), 'Unrecognized production native argument');
    return JSON.parse(value);
  });
  assert.ok(!samplerArguments.includes('-j'), 'Plain production evidence cannot use a JSON grammar');
  const resident = {
    protocol: Number(singleMatch(runner, /public const int ProtocolVersion = (\d+);/g, 'Resident protocol version')),
    profile: singleMatch(runner, /public const string ResidentProfileId = "([^"]+)";/g, 'Resident profile identity'),
    sourceSha256: sha256(residentSourcePaths.map(name => readText(root, name)).join('')),
  };
  const files = sourcePaths.map(name => ({ path: name, sha256: sha256(readText(root, name)) }));
  return {
    releaseVersion: readText(root, 'RELEASE_VERSION').trim(),
    shippingModels,
    runtime: {
      id: singleMatch(runtimeBuild, /runtimeId = '([^']+)'/g, 'Runtime identity'),
      sourceCommit: singleMatch(runtimeBuild, /^\$commit = '([a-f0-9]{40})'/gm, 'Runtime source commit'),
      resident,
    },
    promptProfile: productionPromptProfile,
    outputSchema: productionOutputSchema,
    promptVersion: stringConstant('Version'),
    backendId: singleMatch(readText(root, 'src/DropSpace.Infrastructure/Lyrics/PlainHyLyricsBackend.cs'), /public const string BackendId = "([^"]+)";/g, 'Production backend identity'),
    acceptanceVersion: stringConstant('AcceptanceVersion'),
    samplerIdentity: stringConstant('SamplerIdentity'),
    captureMethod: productionCaptureMethod,
    nativeArguments,
    samplerArguments,
    promptTemplate: stringConstant('Template'),
    targetNames: { en: stringConstant('EnglishTarget'), 'zh-Hans': stringConstant('ChineseTarget') },
    executionLimits: {
      wholeSongSeconds: integerConstant('WholeSongSeconds'),
      perLineSeconds: Number(singleMatch(runner, /ObjectDisposedException\.ThrowIf\(_disposed, this\);\s*deadline\.CancelAfter\(TimeSpan\.FromSeconds\((\d+)\)\);/g, 'Production per-line deadline')),
      memoryMiB: Number(singleMatch(legacyRunner, /\? 1536L \* 1024 \* 1024 : (\d+)L \* 1024 \* 1024 \* 1024;/g, 'Production ordinary-model memory budget')) * 1024,
      maximumPromptBytes: integerConstant('MaximumPromptBytes'), maximumOutputBytes: integerConstant('MaximumOutputBytes'),
    },
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
  assert.deepEqual({ protocol: runtime.resident?.protocol, profile: runtime.resident?.profile, sourceSha256: runtime.resident?.sourceSha256 },
    scope.runtime.resident, 'Reviewed resident runtime protocol/profile/source mismatch');
  const reviewed = {
    manifestSha256: report.runtimeManifest.sha256,
    baseline: componentIdentity(runtime.resident.cpu, 'plain-lyrics-worker.exe'),
    avx2: componentIdentity(runtime.resident.avx2, 'plain-lyrics-worker-avx2.exe'),
    vulkan: componentIdentity(runtime.resident.vulkan, 'plain-lyrics-worker-vulkan.exe'),
    legacyBaseline: componentIdentity(runtime, 'llama-completion.exe'),
    legacyAvx2: componentIdentity(runtime.avx2, 'llama-completion-avx2.exe'),
    tokenizer: componentIdentity(runtime.tokenizer, 'llama-tokenize.exe'),
  };
  const artifact = validateArtifactContract(report.runtimeArtifact);
  const files = validateInventory(artifact.files);
  // Current resident llama adapter. All packaged workers are byte-bound; CPU
  // native evidence must not be relabeled as proof of physical Vulkan execution.
  assert.deepEqual(files.map(file => file.path).sort(), [
    'LICENSE-llama.cpp', 'llama-completion-avx2.exe', 'llama-completion.exe',
    'llama-tokenize.exe', 'runtime-manifest.json',
    'plain-lyrics-worker.exe', 'plain-lyrics-worker-avx2.exe', 'plain-lyrics-worker-vulkan.exe',
  ].sort(), 'Reviewed runtime inventory must contain every shipping component and license');
  for (const [name, identity] of [
    ['runtime-manifest.json', { sha256: report.runtimeManifest.sha256, bytes: manifestBytes.length }],
    ['llama-completion.exe', reviewed.legacyBaseline], ['llama-completion-avx2.exe', reviewed.legacyAvx2],
    ['llama-tokenize.exe', reviewed.tokenizer],
    ['plain-lyrics-worker.exe', reviewed.baseline], ['plain-lyrics-worker-avx2.exe', reviewed.avx2],
    ['plain-lyrics-worker-vulkan.exe', reviewed.vulkan],
  ]) {
    const file = files.find(item => item.path === name);
    assert.deepEqual({ sha256: file.sha256, bytes: file.bytes }, identity, `Reviewed runtime inventory mismatch: ${name}`);
  }
  validateRuntimeProducer(runtime, artifact);
  return { ...reviewed, files, artifact };
}

function validateRunnerOutput(root, reference, scope, variant) {
  const output = JSON.parse(readEvidence(root, reference, 'Production runner output').toString('utf8').replace(/^\uFEFF/, ''));
  assert.equal(output.schemaVersion, 1, 'Unsupported production runner output schema');
  assert.equal(output.kind, 'production-runner-output', 'Output must preserve actual production runner results');
  assert.equal(output.targetLanguage, reference.targetLanguage, 'Runner output target mismatch');
  assert.ok(['Translated', 'NoUsefulTranslation'].includes(output.outcome), 'Runner output did not complete the production song');
  assert.equal(output.complete, true, 'Production song capture is incomplete');
  assert.equal(output.playbackPositionSeconds, 0, 'Production capture playback position must be controlled');
  assert.equal(output.residentProcessReuseConfirmed, true, 'Production capture did not confirm resident reuse');
  const fixture = readJson(root, fixturePath);
  const lines = fixture.Lines.map((line, lineId) => ({ lineId, sourceText: line.Text })).filter(line => line.sourceText.trim());
  assert.ok(Array.isArray(output.calls), 'Production native call records are required');
  assert.deepEqual(output.calls.map(call => ({ lineId: call.lineId, sourceText: call.sourceText })), lines,
    'Production capture must contain every actual fixture line once, in order, without a source-language bypass');
  const processes = new Set();
  for (const call of output.calls) {
    const prompt = scope.promptTemplate.replace('{0}', () => scope.targetNames[reference.targetLanguage]).replace('{1}', () => call.sourceText);
    assert.equal(call.prompt, prompt, 'Captured prompt differs from the actual production template/source');
    nonempty(call.output, 'Complete runner-returned output');
    assert.ok(Buffer.byteLength(call.output, 'utf8') <= scope.executionLimits.maximumOutputBytes, 'Runner output exceeds production byte limit');
    assert.equal(call.status, 'returned', 'Production native call did not return successfully');
    assert.equal(call.phase, 'cold', 'Production native call is not the cold generation');
    assert.equal(call.targetLanguage, reference.targetLanguage, 'Production native call target mismatch');
    assert.ok(Number.isSafeInteger(call.nativeProcess?.id) && call.nativeProcess.id > 0, 'Observed resident PID is required');
    assert.ok(Number.isFinite(Date.parse(call.nativeProcess.startedAt)), 'Observed resident process start is required');
    const executable = call.nativeProcess.executable?.split(/[\\/]/).at(-1);
    assert.equal(executable, variant === 'baseline' ? 'plain-lyrics-worker.exe' : 'plain-lyrics-worker-avx2.exe', 'Observed resident executable differs from the tested CPU variant');
    processes.add(`${call.nativeProcess.id}:${call.nativeProcess.startedAt}`);
  }
  assert.equal(processes.size, 1, 'Cold song did not reuse one actual resident process');
  assert.ok(Array.isArray(output.progressEvents) && output.progressEvents.length === lines.length, 'Every completed line needs an observed progressive callback');
  let previousTime = 0;
  const requests = new Set();
  for (const [index, progress] of output.progressEvents.entries()) {
    assert.equal(progress.lineId, lines[index].lineId, 'Progress callback line mapping mismatch');
    assert.equal(progress.completedLineCount, index + 1, 'Progress callback completion count mismatch');
    assert.equal(progress.totalLineCount, lines.length, 'Progress callback song count mismatch');
    assert.equal(progress.isCurrent, true, 'Progress callback request fence is stale');
    assert.equal(progress.isEphemeral, true, 'Progress callback must not claim a complete cache result');
    nonempty(progress.requestIdentity, 'Progress request identity');
    requests.add(progress.requestIdentity);
    assert.ok(Number.isSafeInteger(progress.cacheGeneration) && progress.cacheGeneration >= 0, 'Progress cache generation is required');
    assert.ok(Number.isFinite(progress.elapsedMilliseconds) && progress.elapsedMilliseconds >= previousTime, 'Progress callback timing must be monotonic');
    previousTime = progress.elapsedMilliseconds;
  }
  assert.equal(requests.size, 1, 'Progress callbacks belong to different requests');
  assert.equal(output.firstProgressElapsedMilliseconds, output.progressEvents[0].elapsedMilliseconds, 'First progressive result timing mismatch');
}

function validateGpuDefaultProbe(root, reference, model, scope, runtime, variant) {
  const probe = JSON.parse(readEvidence(root, reference, 'GPU-default probe').toString('utf8').replace(/^\uFEFF/, ''));
  assert.equal(probe.schemaVersion, 1, 'Unsupported GPU-default probe schema');
  assert.equal(probe.kind, 'production-gpu-default-probe', 'A real production GPU-default probe is required');
  assert.equal(probe.gpuEnabled, true, 'GPU-default probe must request the actual enabled setting');
  assert.equal(probe.complete, true, 'GPU-default probe did not complete');
  assert.equal(probe.cleanupConfirmed, true, 'GPU-default probe cleanup is unconfirmed');
  assert.ok(['cpu', 'vulkan'].includes(probe.actualBackend), 'GPU-default probe must report its actual backend');
  assert.equal(probe.usedCpuFallback, probe.actualBackend === 'cpu', 'GPU-default fallback observation is inconsistent');
  assert.equal(probe.deviceVendor, 'unverified', 'This probe cannot certify NVIDIA/AMD hardware');
  assert.deepEqual(probe.model, { id: model.id, sha256: model.sha256, bytes: model.bytes }, 'GPU-default probe model identity mismatch');
  assert.equal(probe.sourceFingerprintSha256, scope.sources.sha256, 'GPU-default probe source mismatch');
  assert.equal(probe.fixtureSha256, scope.fixture.sha256, 'GPU-default probe fixture mismatch');
  assert.equal(probe.runtimeManifestSha256, runtime.manifestSha256, 'GPU-default probe runtime mismatch');
  assert.equal(probe.residentSourceSha256, scope.runtime.resident.sourceSha256, 'GPU-default probe resident source mismatch');
  assert.equal(probe.targetLanguage, 'en', 'GPU-default probe target mismatch');
  assert.equal(probe.fixtureLineId, 12, 'GPU-default probe fixture line mismatch');
  const source = readJson(root, fixturePath).Lines[12].Text;
  assert.equal(probe.sourceText, source, 'GPU-default probe source text mismatch');
  assert.equal(probe.prompt, scope.promptTemplate.replace('{0}', () => scope.targetNames.en).replace('{1}', () => source), 'GPU-default probe prompt mismatch');
  nonempty(probe.output, 'GPU-default probe returned output');
  assert.ok(['Translated', 'NoUsefulTranslation'].includes(probe.outcome), 'GPU-default probe outcome is incomplete');
  assert.ok(Number.isSafeInteger(probe.observedProcess?.id) && probe.observedProcess.id > 0, 'GPU-default probe actual process is required');
  const executable = probe.observedProcess.executable?.split(/[\\/]/).at(-1);
  assert.equal(executable, probe.actualBackend === 'vulkan' ? 'plain-lyrics-worker-vulkan.exe' : variant === 'baseline' ? 'plain-lyrics-worker.exe' : 'plain-lyrics-worker-avx2.exe', 'GPU-default mode differs from observed executable');
}

function validateNativeEvidence(root, reference, model, scope, runtime, reviewedAt) {
  assert.equal(reference.kind, 'native-output', 'Source inspection and diagnostic summaries alone are not runtime evidence');
  assert.equal(reference.fixtureSha256, scope.fixture.sha256, 'Runtime evidence must use the pinned original QA fixture');
  const envelope = JSON.parse(readEvidence(root, reference, `Model ${model.id} native evidence`).toString('utf8'));
  assert.equal(envelope.schemaVersion, 2, 'Production plaintext capture requires native evidence schema 2; old QA cannot be relabeled');
  assert.equal(envelope.kind, 'native-output', 'Native evidence must be a provenance envelope');
  assert.deepEqual(envelope.model, { id: model.id, sha256: model.sha256, bytes: model.bytes }, 'Native evidence model identity mismatch');
  assert.equal(envelope.fixtureSha256, scope.fixture.sha256, 'Native evidence fixture mismatch');
  assert.equal(envelope.promptVersion, scope.promptVersion, 'Native evidence prompt version mismatch');
  assert.equal(envelope.promptProfile, productionPromptProfile, 'Only the production prompt profile can support shipping approval');
  assert.equal(envelope.outputSchema, productionOutputSchema, 'Native evidence must use the production output schema');
  for (const key of ['backendId', 'acceptanceVersion', 'samplerIdentity', 'captureMethod'])
    assert.equal(envelope[key], scope[key], `Native evidence ${key} mismatch`);
  assert.deepEqual(envelope.executionLimits, scope.executionLimits, 'Native evidence execution limits mismatch');
  assert.equal(envelope.sourceFingerprintSha256, scope.sources.sha256, 'Native evidence source fingerprint mismatch');
  assert.equal(envelope.platform, 'windows-x64', 'Native evidence must use the shipping Windows x64 platform');
  assert.ok(timestamp(envelope.executedAt, 'Native evidence execution time') <= reviewedAt, 'Native evidence postdates its semantic review');
  assert.equal(envelope.runtime?.manifestSha256, runtime.manifestSha256, 'Native evidence runtime manifest mismatch');
  assert.equal(envelope.runtime.mode, 'cpu', 'CPU capture must state its actual execution mode');
  assert.equal(envelope.runtime.protocol, scope.runtime.resident.protocol, 'Native evidence resident protocol mismatch');
  assert.equal(envelope.runtime.profile, scope.runtime.resident.profile, 'Native evidence resident profile mismatch');
  assert.equal(envelope.runtime.residentSourceSha256, scope.runtime.resident.sourceSha256, 'Native evidence resident source mismatch');
  const variant = envelope.runtime.variant;
  assert.ok(['baseline', 'avx2'].includes(variant), 'Native evidence runtime variant is required');
  assert.deepEqual(envelope.runtime.completion, runtime[variant], 'Native evidence completion hash/bytes mismatch');
  // The plain production path does not invoke a tokenizer. Its packaged bytes
  // remain verified by the artifact inventory; do not claim it was exercised.
  const configuration = JSON.parse(readEvidence(root, envelope.configuration, `Model ${model.id} native configuration`).toString('utf8').replace(/^\uFEFF/, ''));
  assert.equal(configuration.schemaVersion, 2, 'Production plaintext configuration schema 2 is required');
  assert.equal(configuration.promptProfile, productionPromptProfile, 'Native configuration is not the production prompt profile');
  assert.equal(configuration.outputSchema, productionOutputSchema, 'Native configuration lacks the production output schema identity');
  assert.equal(configuration.loadOnly, false, 'Loader-only configuration is not shipping quality evidence');
  assert.deepEqual({ id: configuration.modelId, sha256: configuration.modelSha256, bytes: configuration.modelBytes }, envelope.model, 'Native configuration model identity mismatch');
  assert.equal(configuration.executableSha256, runtime[variant].sha256, 'Native configuration completion hash mismatch');
  assert.equal(configuration.executableBytes, runtime[variant].bytes, 'Native configuration completion bytes mismatch');
  assert.equal(configuration.runtimeManifestSha256, runtime.manifestSha256, 'Native configuration runtime manifest mismatch');
  assert.equal(configuration.runtimeVariant, variant, 'Native configuration runtime variant mismatch');
  assert.equal(configuration.gpuEnabled, false, 'CPU capture cannot claim GPU execution');
  for (const key of ['promptVersion', 'backendId', 'acceptanceVersion', 'samplerIdentity', 'captureMethod'])
    assert.equal(configuration[key], scope[key], `Native configuration ${key} mismatch`);
  assert.equal(configuration.fixtureSha256, scope.fixture.sha256, 'Native configuration fixture mismatch');
  assert.equal(configuration.sourceFingerprintSha256, scope.sources.sha256, 'Native configuration source fingerprint mismatch');
  assert.deepEqual(configuration.executionLimits, scope.executionLimits, 'Native configuration execution limits mismatch');
  assert.deepEqual(configuration.nativeArguments, scope.nativeArguments, 'Native configuration differs from actual production arguments');
  assert.deepEqual(configuration.samplerArguments, scope.samplerArguments, 'Native configuration differs from compiled resident sampler arguments');
  const checks = envelope.technicalChecks;
  assert.deepEqual(checks?.coldTargets, ['en', 'zh-Hans'], 'Both complete cold production target runs are required');
  assert.deepEqual(checks?.cacheTargets, ['en', 'zh-Hans'], 'Both production cache replays are required');
  assert.equal(checks.cacheAdditionalInferenceCalls, 0, 'Production cache replay launched extra inference');
  for (const key of ['cancellationObserved', 'cleanupConfirmed', 'sourceIdentityUnchanged', 'modelIdentityUnchanged', 'runtimeIdentityUnchanged'])
    assert.equal(checks[key], true, `Production native check did not pass: ${key}`);
  assert.ok(Array.isArray(envelope.outputs) && envelope.outputs.length > 0, 'Native evidence needs original output references');
  const targets = new Set();
  for (const output of envelope.outputs) {
    assert.equal(output.kind, 'runner-output', 'Native evidence must preserve runner-returned output, not relabeled raw output');
    assert.ok(['en', 'zh-Hans'].includes(output.targetLanguage), 'Native evidence output target is unsupported');
    validateRunnerOutput(root, output, scope, variant);
    targets.add(output.targetLanguage);
  }
  assert.deepEqual([...targets].sort(), ['en', 'zh-Hans'], 'Native evidence needs both shipping target languages');
  assert.equal(envelope.outputs.length, 2, 'Native evidence needs exactly one complete capture for both shipping target languages');
  validateGpuDefaultProbe(root, envelope.gpuDefaultProbe, model, scope, runtime, variant);
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
  assert.ok(Array.isArray(report.openDefects) && report.openDefects.length === 0, 'Known defects must be cleared; accepted Beta limitations are not a bug waiver');
  assert.ok(Array.isArray(report.acceptedLimitations), 'An explicit accepted-limitations list is required');
  if (report.acceptedLimitations.length > 0) {
    assert.match(scope.releaseVersion, /-beta\.\d+$/, 'Accepted experimental limitations apply only to a Beta release');
    for (const limitation of report.acceptedLimitations) {
      nonempty(limitation.id, 'Accepted limitation ID');
      nonempty(limitation.summary, 'Accepted limitation summary');
      assert.ok(['quality', 'latency'].includes(limitation.kind), 'Accepted limitations must describe quality or latency, not unresolved defects');
    }
    nonempty(report.userAcceptance?.reference, 'Actual user acceptance reference');
    assert.ok(timestamp(report.userAcceptance.acceptedAt, 'User acceptance time') <= reviewedAt, 'User acceptance postdates the review');
  }
  const reviewedRuntime = readReviewedRuntime(root, report, scope);
  assert.ok(Array.isArray(report.models), 'Per-model semantic reviews are required');
  assert.deepEqual(report.models.map(model => ({ id: model.id, sha256: model.sha256, bytes: model.bytes })), scope.shippingModels, 'Every shipping model needs semantic approval');
  for (const model of report.models) {
    assert.equal(model.verdict, 'approved', `Model ${model.id} is not semantically approved`);
    nonempty(model.summary, `Model ${model.id} review rationale`);
    assert.ok(Array.isArray(model.evidence) && model.evidence.length > 0, `Model ${model.id} needs native evidence`);
    const variants = new Set(model.evidence.map(evidence =>
      validateNativeEvidence(root, evidence, model, scope, reviewedRuntime, reviewedAt)));
    assert.deepEqual([...variants].sort(), ['avx2', 'baseline'], `Model ${model.id} needs both shipping CPU runtime variants; Vulkan execution is separate coverage`);
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

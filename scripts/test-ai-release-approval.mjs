import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { compareInventory, directoryInventory, validateArtifactContract, validateInventory, validateRuntimeProducer, verifyReleaseBinding } from './ai-runtime-publication.mjs';

export const approvalPath = 'scripts/ai-model-qa/release-approval.json';
export const admissionPath = 'scripts/plain-hy-production-evidence/source48-admission-v12.json';
export const fixturePath = 'scripts/ai-model-qa/inputs/source48.json';
export const residentSourcePaths = Object.freeze([
  'tools/plain-lyrics-helper/CMakeLists.txt',
  'tools/plain-lyrics-helper/gpu-policy.h',
  'tools/plain-lyrics-helper/main.cpp',
]);
// Embedded transport metadata and its authorization code are reviewed inputs
// even when the identity of the executed model remains unchanged.
export const modelDeliverySourcePaths = Object.freeze([
  'src/DropSpace.Infrastructure/Lyrics/AiModelDeliveryManifest.cs',
  'src/DropSpace.Infrastructure/DropSpace.Infrastructure.csproj',
  'src/DropSpace.Infrastructure/Lyrics/Manifests/models-hy-mt2-q8-v1.json',
]);
export const languageIdentificationSourcePaths = Object.freeze([
  'src/DropSpace.Core/Lyrics/LyricsWholeTrackAdmission.cs',
  'src/DropSpace.Infrastructure/Lyrics/FastTextLanguageIdentifier.cs',
  'src/DropSpace.Infrastructure/Lyrics/Manifests/fasttext-lid176-bin-v1.json',
  'Directory.Packages.props',
  'src/DropSpace.Infrastructure/packages.lock.json',
  'src/DropSpace.App/packages.lock.json',
  'scripts/Stage-LyricsLanguageModel.ps1',
  'scripts/Inspect-LyricsLanguagePayload.ps1',
  'src/DropSpace.App/Services/Diagnostics/LyricsLanguageSmoke.cs',
  'docs/licenses/fasttext-engine-MIT.txt',
  'docs/licenses/fasttext-panlingo-MIT.txt',
  'docs/licenses/fasttext-lid176-CC-BY-SA-3.0.txt',
]);
// This list is code-owned, never selected by the approval record. Removing a
// prompt/parser/inference input from a manifest cannot weaken its binding.
export const sourcePaths = Object.freeze([
  'src/DropSpace.Core/Lyrics/AiLyricsModelCatalog.cs',
  'src/DropSpace.Core/Lyrics/AiLyricsSelectionModelCatalog.cs',
  'src/DropSpace.Core/Lyrics/LyricsModels.cs',
  'src/DropSpace.Core/Lyrics/LyricsTranslationPrompt.cs',
  'src/DropSpace.Core/Lyrics/LyricsTranslationOutput.cs',
  'src/DropSpace.Core/Lyrics/LyricsTranslationPolicy.cs',
  'src/DropSpace.Core/Lyrics/LyricsInferenceCircuit.cs',
  'src/DropSpace.Core/Lyrics/PlainHyLyricsProtocol.cs',
  'src/DropSpace.Infrastructure/Lyrics/LlamaCompletionRunner.cs',
  'src/DropSpace.Infrastructure/Lyrics/CpuInferenceMemoryPolicy.cs',
  'src/DropSpace.Infrastructure/Lyrics/InferenceResourcesUnavailableException.cs',
  'src/DropSpace.Infrastructure/Lyrics/LyricsTranslationCoordinator.cs',
  'src/DropSpace.Infrastructure/Lyrics/AiLyricsWorkLifetime.cs',
  'src/DropSpace.Infrastructure/Lyrics/AiLyricsCache.cs',
  'src/DropSpace.Infrastructure/Lyrics/LyricsCache.cs',
  'src/DropSpace.Infrastructure/Lyrics/LocalInferenceProcess.cs',
  'src/DropSpace.Infrastructure/Lyrics/WindowsInferenceProcess.cs',
  'src/DropSpace.Infrastructure/Lyrics/AiLyricsRuntimePackage.cs',
  'src/DropSpace.Infrastructure/Storage/ReparseSafeFileOpen.cs',
  'src/DropSpace.Infrastructure/Lyrics/Ct2HelperAdapter.cs',
  'src/DropSpace.Infrastructure/Lyrics/Ct2PrivatePackage.cs',
  'src/DropSpace.Infrastructure/Lyrics/AiLyricsBackend.cs',
  'src/DropSpace.Infrastructure/Lyrics/Ct2LyricsPipeline.cs',
  'src/DropSpace.Infrastructure/Lyrics/Ct2PackageInstaller.cs',
  'src/DropSpace.Infrastructure/Lyrics/PlainHyLyricsBackend.cs',
  'src/DropSpace.Infrastructure/Lyrics/PlainLyricsSegmentMemo.cs',
  'src/DropSpace.Infrastructure/Lyrics/PlainLyricsMetrics.cs',
  'src/DropSpace.Infrastructure/Lyrics/LyricsTranslationProgress.cs',
  'src/DropSpace.Infrastructure/Lyrics/PersistentPlainLyricsRunner.cs',
  'src/DropSpace.App/Services/Media/AiLyricsService.cs',
  // CUDA download/ownership and shared DLC management are production inputs.
  'src/DropSpace.Core/Abstractions/IDlcPackageProvider.cs',
  'src/DropSpace.Infrastructure/Lyrics/CudaDriverAvailability.cs',
  'src/DropSpace.Infrastructure/Lyrics/CudaLyricsRuntimePackage.cs',
  'src/DropSpace.Infrastructure/Lyrics/CudaPlainHyLyricsBackend.cs',
  'src/DropSpace.App/Services/Dlc/AiModelDlcProvider.cs',
  'src/DropSpace.App/Services/Dlc/CudaRuntimeDlcProvider.cs',
  'src/DropSpace.App/Services/Dlc/DlcManagerService.cs',
  'src/DropSpace.App/Views/Settings/DlcPage.cs',
  'src/DropSpace.App/Views/MainPage.Settings.cs',
  'src/DropSpace.App/MainWindow.xaml.cs',
  'tools/cuda-lyrics-helper/CMakeLists.txt',
  'tools/cuda-lyrics-helper/adapt_worker.py',
  'tools/cuda-lyrics-helper/cuda-device.h',
  'scripts/Build-CudaLyricsExperiment.ps1',
  'scripts/cuda_runtime_contract.py',
  'scripts/package-cuda-runtime.py',
  'scripts/stage-reviewed-cuda-metadata.py',
  'docs/dev/evidence/beta11-local-cuda/cuda13-producer-report.json',
  'docs/dev/evidence/beta11-local-cuda/cuda13-runtime-manifest.json',
  'scripts/test-ai-release-approval.mjs',
  'scripts/ai-model-qa/evidence/runtime-37095011004-1/runtime-manifest.json',
  // Upstream identity/selection/provider parsing and target/display propagation.
  'src/DropSpace.Core/Lyrics/LyricsParser.cs',
  'src/DropSpace.Core/Lyrics/LyricsMatcher.cs',
  'src/DropSpace.Core/Lyrics/LyricsCandidateSelection.cs',
  'src/DropSpace.Core/Lyrics/ArtistCreditOrthography.cs',
  'src/DropSpace.Core/Lyrics/Data/TSCharacters.txt',
  'src/DropSpace.Core/Lyrics/Data/OpenCC-LICENSE.txt',
  'src/DropSpace.Core/Lyrics/LyricsDisplayPolicy.cs',
  'src/DropSpace.Core/Lyrics/LyricsReloadPolicy.cs',
  'src/DropSpace.Core/Lyrics/LyricsLanguagePolicy.cs',
  'src/DropSpace.Core/Lyrics/LyricsBodyQualityPolicy.cs',
  'src/DropSpace.Core/Lyrics/LyricsMarqueePolicy.cs',
  'src/DropSpace.Core/Media/MediaModels.cs',
  'src/DropSpace.Core/Media/MediaPlaybackClock.cs',
  'src/DropSpace.Core/Media/SpectrumFreshnessPolicy.cs',
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
  'src/DropSpace.Infrastructure/Lyrics/NetEaseResponseCache.cs',
  'src/DropSpace.Infrastructure/Lyrics/LyricsDiagnostic.cs',
  'src/DropSpace.Infrastructure/Lyrics/QqMusicLyricsProvider.cs',
  'src/DropSpace.Infrastructure/Lyrics/LyricsService.cs',
  'src/DropSpace.Infrastructure/Lyrics/LyricsCandidateRequests.cs',
  'src/DropSpace.Infrastructure/Lyrics/ILyricsSelectionRuntime.cs',
  'src/DropSpace.Infrastructure/Lyrics/LyricsCandidateSelector.cs',
  'src/DropSpace.Infrastructure/Lyrics/KugouKrcParser.cs',
  'src/DropSpace.Infrastructure/Lyrics/LyricsHttpClient.cs',
  'src/DropSpace.Infrastructure/Lyrics/LyricsProviderRegistry.cs',
  'src/DropSpace.Infrastructure/Lyrics/AiModelPackageService.cs',
  ...modelDeliverySourcePaths,
  ...languageIdentificationSourcePaths,
  'src/DropSpace.Infrastructure/Settings/JsonSettingsService.cs',
  'src/DropSpace.Infrastructure/Settings/SettingsIoPolicy.cs',
  'src/DropSpace.App/Services/Media/MediaExperienceService.cs',
  'src/DropSpace.App/Services/Media/WindowsMediaSessionService.cs',
  'src/DropSpace.App/Services/Media/BoundedMediaOperation.cs',
  'src/DropSpace.App/Services/NativeAsyncLifetime.cs',
  'src/DropSpace.App/Services/ImageDecoderPreflight.cs',
  'src/DropSpace.App/Services/ThumbnailService.cs',
  'src/DropSpace.App/App.xaml.cs',
  'src/DropSpace.App/Services/Diagnostics/MusicVisualSmoke.cs',
  'src/DropSpace.App/Services/Diagnostics/MusicVisualSmokeOptions.cs',
  'src/DropSpace.App/Views/MainPage.xaml',
  'src/DropSpace.App/Views/MainPage.xaml.cs',
  'src/DropSpace.App/Services/AppLanguageService.cs',
  'src/DropSpace.App/Services/ResourceStringLocalizer.cs',
  'src/DropSpace.App/Services/SettingsApplicationCoordinator.cs',
  'src/DropSpace.App/ViewModels/MediaViewModel.cs',
  'src/DropSpace.App/ViewModels/OverlayViewModel.cs',
  'src/DropSpace.Core/Island/IslandExperienceCoordinator.cs',
  'src/DropSpace.Core/Island/IslandPresencePolicy.cs',
  'src/DropSpace.App/ViewModels/MainViewModel.cs',
  'src/DropSpace.App/ViewModels/NativeSettingsEditor.cs',
  'src/DropSpace.App/Views/Music/MusicPage.cs',
  'src/DropSpace.App/Views/Settings/SettingsEditBehavior.cs',
  'src/DropSpace.App/Views/Settings/SettingsValueSlider.cs',
  'src/DropSpace.App/Views/Settings/SettingsForm.cs',
  'src/DropSpace.App/Views/Music/LyricsRowCollection.cs',
  'src/DropSpace.App/Views/Music/AiLyricsSettingsCard.cs',
  'src/DropSpace.App/Views/Island/MediaCompactView.xaml.cs',
  'src/DropSpace.App/Views/Island/MediaCompactView.xaml',
  'src/DropSpace.App/Views/Island/MediaExpandedView.xaml.cs',
  'src/DropSpace.App/Views/Island/MediaRenderQueue.cs',
  'src/DropSpace.App/Views/Island/MediaExpandedView.xaml',
  'src/DropSpace.Core/Lyrics/LyricsGlowEnvelope.cs',
  'src/DropSpace.Core/Lyrics/LyricsGlowAudioResponse.cs',
  'src/DropSpace.Core/Lyrics/LyricsGlowHandoff.cs',
  'src/DropSpace.Core/Lyrics/LyricsGlowPolicy.cs',
  'src/DropSpace.App/Services/IslandGlowController.cs',
  'src/DropSpace.App/Services/IslandGlowRasterizer.cs',
  'src/DropSpace.App/Services/IslandGlowWindow.cs',
  // Final Beta.2 overlay material, geometry and scheduling shipping inputs.
  'src/DropSpace.App/OverlayWindow.xaml.cs',
  'src/DropSpace.App/Services/AcrylicCoverageBackdropTarget.cs',
  'src/DropSpace.App/Services/AcrylicCoverageMask.cs',
  'src/DropSpace.App/Services/AcrylicShutterBlur.cs',
  'src/DropSpace.App/Services/CompositionEffectDescriptions.cs',
  'src/DropSpace.App/Services/DisplayIdentityService.cs',
  'src/DropSpace.App/Services/IslandAcrylicBackdrop.cs',
  'src/DropSpace.App/Services/IslandMotionBlurPolicy.cs',
  'src/DropSpace.App/Services/IslandTransparentBackdrop.cs',
  'src/DropSpace.App/Services/MonitorLayoutService.cs',
  'src/DropSpace.App/Services/OverlayMaterialController.cs',
  'src/DropSpace.App/Services/OverlayMotionOrchestrator.cs',
  'src/DropSpace.App/Services/OverlayNativeRegionController.cs',
  'src/DropSpace.App/Services/OverlayTransparentHostController.cs',
  'src/DropSpace.App/Services/OverlayWindowInterop.cs',
  'src/DropSpace.App/Services/OverlayWindowService.cs',
  'src/DropSpace.Core/Overlay/OverlayFrameGeometry.cs',
  'src/DropSpace.Core/Overlay/OverlayFramePacer.cs',
  'src/DropSpace.Core/Overlay/OverlayMotionController.cs',
  'src/DropSpace.Core/Overlay/OverlayRegionSignature.cs',
  // Offline interface catalog, independent target binding and actual package recipe.
  'localization/languages.json',
  'src/DropSpace.Core/Policies/AppLanguageCatalog.cs',
  'src/DropSpace.Core/Policies/LyricsTranslationTargetPolicy.cs',
  'scripts/Build-NextBetaCandidate.ps1',
  'scripts/next-beta-release-validation.mjs',
  'scripts/Test-PackagedLocalization.ps1',
  'src/DropSpace.App/Strings/zh-TW/Resources.resw',
  'src/DropSpace.App/Strings/ja-JP/Resources.resw',
  'src/DropSpace.App/Strings/ko-KR/Resources.resw',
  'src/DropSpace.App/Strings/de-DE/Resources.resw',
  'src/DropSpace.App/Strings/fr-FR/Resources.resw',
  'src/DropSpace.App/Strings/es-ES/Resources.resw',
  'src/DropSpace.App/Strings/pt-BR/Resources.resw',
  'src/DropSpace.App/Strings/ru-RU/Resources.resw',
  'src/DropSpace.App/Strings/en-US/Resources.resw',
  'src/DropSpace.App/Strings/zh-CN/Resources.resw',
  'src/DropSpace.Infrastructure/Lyrics/LocalInferenceExecutionException.cs',
  'src/DropSpace.App/Services/Media/MediaApplicationIconService.cs',
  'src/DropSpace.App/Services/Media/MediaArtworkService.cs',
  'src/DropSpace.App/Services/Media/MediaEventSubscription.cs',
  'src/DropSpace.App/Services/Media/MediaProcessResolver.cs',
  'src/DropSpace.Core/Lyrics/LyricsPreviewPolicy.cs',
  'src/DropSpace.App/Views/Island/ExpandedIslandMusicView.xaml',
  'src/DropSpace.App/Views/Island/ExpandedIslandMusicView.xaml.cs',
  'src/DropSpace.App/Services/Media/MediaSoftRestartOperation.cs',
  'src/DropSpace.App/Services/Media/MediaLyricsRefreshRequest.cs',
  'src/DropSpace.App/Services/Media/RetirableMediaWork.cs',
  'src/DropSpace.App/Services/Media/MediaSessionOwner.cs',
  'src/DropSpace.App/Services/Media/MediaSubscriptionAdmission.cs',
  'src/DropSpace.App/OverlayWindow.xaml',
  'scripts/ai-model-qa/profiles/minimal-target-only.json',
  'scripts/Build-AiLyricsRuntime.ps1',
  'scripts/Prepare-VulkanBuildDependencies.ps1',
  'scripts/build-dependencies/vulkan/vcpkg.json',
  'scripts/ai-model-qa/Program.cs',
  'scripts/ai-model-qa/Run-WindowsModelQa.ps1',
  'scripts/ai-model-qa/WindowsModelQa.csproj',
  'scripts/plain-hy-production-evidence/PlainHyProductionEvidence.csproj',
  'scripts/plain-hy-production-evidence/packages.lock.json',
  'scripts/plain-hy-production-evidence/Program.cs',
  // Retain the prior reviewed computation as an immutable historical input;
  // the active host computation below has its own path and honest provenance.
  'scripts/plain-hy-production-evidence/source48-admission-v10.json',
  'scripts/plain-hy-production-evidence/source48-admission-v11.json',
  admissionPath,
  'scripts/plain-hy-production-evidence/ContractTests.cs',
  'scripts/plain-hy-production-evidence/Run-WindowsProductionEvidence.ps1',
  'scripts/plain-hy-production-evidence/Test-ProductionEvidenceContract.ps1',
  'scripts/Get-AiLyricsSmokeModel.ps1',
  'scripts/Test-AiLyricsRuntime.ps1',
  // Embedding/packaging declarations are also part of the reviewed shipping input.
  'src/DropSpace.Core/DropSpace.Core.csproj',
  'src/DropSpace.App/DropSpace.App.csproj',
  'scripts/Build-PortableExe.ps1',
  'scripts/Build-UnsignedPackage.ps1',
  'scripts/Build-Installer.ps1',
  // Exact Beta17 producer, asset reuse and validation/promotion are shipping inputs.
  'scripts/Build-Beta17Candidate.ps1',
  'scripts/Build-Beta18Candidate.ps1',
  'scripts/Reuse-BundledLyricsLanguageModel.ps1',
  'scripts/beta17-release-validation.mjs',
  'scripts/beta18-release-validation.mjs',
  'scripts/ci-release-promotion.mjs',
  'scripts/Inspect-OwnerWaivedPackages.ps1',
  'scripts/Collect-AiRuntimeNotices.ps1',
  'tools/ct2-helper/helper.py',
  'tools/ct2-helper/build.ps1',
  'tools/ct2-helper/dependencies.schema.json',
  'tools/ct2-helper/private-package-manifest.schema.json',
  // Beta12 shared downloads, session persistence, and runtime state inputs.
  'src/DropSpace.App/Services/Dlc/NeteaseComponentsDlcProvider.cs',
  'src/DropSpace.App/Services/Media/LyricsRapidSkipDiagnostic.cs',
  'src/DropSpace.App/Services/Media/QqMusicLoginService.cs',
  'src/DropSpace.App/Services/NativeFolderPickerService.cs',
  'src/DropSpace.App/Services/NeteaseEnhancement/InfLinkDeploymentService.IO.cs',
  'src/DropSpace.App/Services/NeteaseEnhancement/InfLinkDeploymentService.Packages.cs',
  'src/DropSpace.App/Services/NeteaseEnhancement/InfLinkDeploymentService.cs',
  'src/DropSpace.App/Services/NeteaseEnhancement/NeteaseRuntimeInstaller.cs',
  'src/DropSpace.App/Views/Music/QqMusicLoginCard.cs',
  'src/DropSpace.App/Views/Settings/DownloadPanel.cs',
  'src/DropSpace.Core/Downloads/DownloadModels.cs',
  'src/DropSpace.Infrastructure/Downloads/AdaptiveDownloadScheduler.cs',
  'src/DropSpace.Infrastructure/Downloads/DirectFileRequestFactory.cs',
  'src/DropSpace.Infrastructure/Downloads/DownloadBandwidthLimiter.cs',
  'src/DropSpace.Infrastructure/Downloads/DownloadConnectionBudget.cs',
  'src/DropSpace.Infrastructure/Downloads/DownloadManager.cs',
  'src/DropSpace.Infrastructure/Downloads/DownloadPersistenceQueue.cs',
  'src/DropSpace.Infrastructure/Downloads/DownloadRequestPolicy.cs',
  'src/DropSpace.Infrastructure/Downloads/DownloadStorage.cs',
  'src/DropSpace.Infrastructure/Downloads/DownloadTaskRepository.cs',
  'src/DropSpace.Infrastructure/Downloads/DownloadTransferBudget.cs',
  'src/DropSpace.Infrastructure/Downloads/FileNameSanitizer.cs',
  'src/DropSpace.Infrastructure/Downloads/HttpByteRangePlanner.cs',
  'src/DropSpace.Infrastructure/Downloads/HttpRangeDownloader.cs',
  'src/DropSpace.Infrastructure/Downloads/HttpTransferDeadline.cs',
  'src/DropSpace.Infrastructure/Downloads/OutputReservationService.cs',
  'src/DropSpace.Infrastructure/Downloads/ParallelHttpFileDownloader.cs',
  'src/DropSpace.Infrastructure/Downloads/RetryExecutor.cs',
  'src/DropSpace.Infrastructure/Lyrics/KugouResponseStatus.cs',
  'src/DropSpace.Infrastructure/Lyrics/LyricsRequestTrace.cs',
  'src/DropSpace.Infrastructure/Lyrics/PlainLyricsExecutionStatus.cs',
  'src/DropSpace.Infrastructure/Lyrics/QqMusicSession.cs',
  'src/DropSpace.Infrastructure/Lyrics/ResidentInferenceMemoryPolicy.cs',
  'src/DropSpace.Infrastructure/Updates/HttpUpdateDownloader.cs',
  'scripts/Test-CudaBuildBinding.ps1',
  'scripts/ai-runtime-publication.mjs',
  ...residentSourcePaths,
]);
export const productionPromptProfile = 'production-plain-hy';
export const productionOutputSchema = 'host-mapped-id-text-v1';
export const productionCaptureMethod = 'PlainHyLyricsBackend+PlainHyLyricsCoordinator+PersistentPlainLyricsRunner.RunPlainAsync';
export const maximumApprovalAgeMs = 30 * 24 * 60 * 60 * 1000;
export const experimentalBetaStatus = 'owner-accepted-experimental-beta';
export const experimentalBetaVersion = 'v0.3.1-beta.19';
export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const hashPattern = /^[a-f0-9]{64}$/;
const rootDirectory = fileURLToPath(new URL('../', import.meta.url));
const readText = (root, name) => fs.readFileSync(path.join(root, name), 'utf8').replace(/\r\n/g, '\n');
const readJson = (root, name) => JSON.parse(readText(root, name));
// Preserve the Beta15 v12 host computation as historical evidence only.
// Beta16 uses whole-track fastText admission; this helper is not production policy.
export function fixtureAdmissionDecision(text, target, { detectedLanguage, confidence }) {
  const confident = detectedLanguage !== null && confidence >= 0.9;
  if (confident && (detectedLanguage === target || detectedLanguage.startsWith('zh-') && target.startsWith('zh-'))) return 'SameLanguage';
  if (!confident || detectedLanguage === 'mul') return 'Abstain';
  return 'Translate';
}
const nonempty = (value, label) => assert.ok(typeof value === 'string' && value.trim().length > 0, `${label} is required`);

function singleMatch(text, pattern, label) {
  const matches = [...text.matchAll(pattern)];
  assert.equal(matches.length, 1, `${label} must have exactly one recognizable production declaration`);
  return matches[0][1];
}

export function readSourceFingerprint(root) {
  assert.equal(new Set(sourcePaths).size, sourcePaths.length, 'Duplicate fingerprint source paths');
  const projectPath = 'src/DropSpace.Infrastructure/DropSpace.Infrastructure.csproj';
  const project = readText(root, projectPath).replace(/<!--[\s\S]*?-->/g, '');
  const namespace = singleMatch(project, /<RootNamespace>([^<]+)<\/RootNamespace>/g, 'Infrastructure resource namespace');
  const resources = [...project.matchAll(/<EmbeddedResource\b([^>]*?)(?:\/\s*>|>([\s\S]*?)<\/EmbeddedResource>)/g)];
  const embeddedJson = resources.flatMap(([, attributes, body = '']) => {
    const include = /\bInclude\s*=\s*["']([^"']+)["']/.exec(attributes)?.[1];
    assert.ok(include, 'Embedded resource must use a recognizable literal Include');
    assert.ok(!/[*$?;]/.test(include), 'Embedded resource expansion requires an explicit fingerprint extractor update');
    if (!include.toLowerCase().endsWith('.json')) return [];
    const relative = include.replaceAll('\\', '/');
    assert.ok(!path.posix.isAbsolute(relative) && !relative.split('/').includes('..'), 'Embedded resource path must stay within Infrastructure');
    const name = path.posix.join(path.posix.dirname(projectPath), relative);
    assert.ok([...modelDeliverySourcePaths, ...languageIdentificationSourcePaths].includes(name), `Embedded JSON needs code-owned fingerprint coverage: ${name}`);
    const logicalName = /\bLogicalName\s*=\s*["']([^"']+)["']/.exec(attributes)?.[1]
      ?? /<LogicalName>([^<]+)<\/LogicalName>/.exec(body)?.[1]
      ?? `${namespace}.${relative.replaceAll('/', '.')}`;
    return [{ path: name, logicalName }];
  });
  const manifests = [...modelDeliverySourcePaths, ...languageIdentificationSourcePaths].filter(name => name.includes('/Manifests/') && name.endsWith('.json'));
  assert.deepEqual(embeddedJson.map(resource => resource.path).sort(), [...manifests].sort(), 'Reviewed model delivery JSON must actually be embedded exactly once');
  const delivery = readText(root, 'src/DropSpace.Infrastructure/Lyrics/AiModelDeliveryManifest.cs');
  const resourceNames = [...delivery.matchAll(/GetManifestResourceStream\(\s*"([^"]+)"\)/g)].map(match => match[1]);
  assert.deepEqual(resourceNames.sort(), embeddedJson.filter(resource => modelDeliverySourcePaths.includes(resource.path)).map(resource => resource.logicalName).sort(), 'Model delivery resource binding does not match the production reader');
  const languageReader = readText(root, 'src/DropSpace.Infrastructure/Lyrics/FastTextLanguageIdentifier.cs');
  const languageManifestName = singleMatch(languageReader, /private const string ManifestResource = "([^"]+)";/g, 'Language model manifest resource');
  assert.deepEqual(embeddedJson.filter(resource => languageIdentificationSourcePaths.includes(resource.path)).map(resource => resource.logicalName), [languageManifestName], 'Language manifest binding does not match the production reader');
  const languageManifestPath = languageIdentificationSourcePaths.find(name => name.endsWith('.json'));
  const languageManifest = readJson(root, languageManifestPath);
  assert.equal(languageManifest.schemaVersion, 1, 'Unsupported bundled language manifest');
  assert.equal(languageManifest.modelFile, 'lid.176.bin', 'Whole-track language identification must use the bin model');
  assert.equal(languageManifest.source, 'https://dl.fbaipublicfiles.com/fasttext/supervised-models/lid.176.bin', 'Language model must be staged from the official source');
  assert.equal(languageManifest.nativeRid, 'win-x64', 'Bundled native language engine must match the App architecture');
  assert.equal(languageManifest.nativePackage, 'Panlingo.LanguageIdentification.FastText.Native', 'Use only the selected native package, without unused default ftz');
  for (const field of ['sha256', 'nativeSha256']) assert.match(languageManifest[field] ?? '', hashPattern, `Missing language identity ${field}`);
  for (const field of ['bytes', 'nativeBytes']) assert.ok(Number.isSafeInteger(languageManifest[field]) && languageManifest[field] > 0, `Invalid language length ${field}`);
  const appProject = readText(root, 'src/DropSpace.App/DropSpace.App.csproj').replace(/<!--[\s\S]*?-->/g, '');
  const languageResources = [...appProject.matchAll(/<EmbeddedResource\b(?=[^>]*\bLogicalName="DropSpace\.LyricsLanguage\.lid\.176\.bin")([^>]+)\/>/g)];
  assert.equal(languageResources.length, 1, 'App must embed exactly one bundled lid.176.bin');
  assert.match(languageResources[0][1], /Include="\.\.\\\.\.\\artifacts\\lyrics-language\\lid\.176\.bin"/, 'App bin resource must use the verified staging path');
  assert.doesNotMatch(project, /PackageReference\b[^>]*Include="Panlingo\.LanguageIdentification\.FastText"/, 'The full wrapper would package unused default ftz');
  const packages = readText(root, 'Directory.Packages.props');
  assert.equal(singleMatch(packages, /<PackageVersion\b(?=[^>]*\bInclude="Panlingo\.LanguageIdentification\.FastText\.Native")(?=[^>]*\bVersion="([^"]+)")[^>]*\/>/g, 'Native language package version'), languageManifest.nativePackageVersion, 'Language native version differs from the manifest');
  // A BOM or CRLF changes the checked embedded resource identity. Normalize
  // source text only; hash manifest resources exactly as they are packaged.
  const files = sourcePaths.map(name => ({ path: name, sha256: sha256(manifests.includes(name)
    ? fs.readFileSync(path.join(root, name)) : readText(root, name)) }));
  return { algorithm: 'sha256-source-lf-embedded-bytes-v2', files, sha256: sha256(JSON.stringify(files)) };
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
  assert.match(runner, /BuildArguments\(string modelPath, bool gpu\) =>\s*BuildArguments\(modelPath, gpu, AiLyricsModelCatalog\.ExperimentalPlain\.Sha256\);/, 'Unrecognized default resident startup arguments');
  assert.match(runner, /var model = AiLyricsModelCatalog\.FindSelectableByHash\(verifiedModelSha256\) \?\?\s*throw new InvalidDataException/, 'Resident model arguments require a verified selectable identity');
  assert.match(runner, /var arguments = new List<string> \{ "--model", Path\.GetFullPath\(modelPath\), "--mode", gpu \? gpuBackend : "cpu" \};\s*if \(model == AiLyricsModelCatalog\.ExperimentalLargePlain\)\s*arguments\.AddRange\(\["--model-profile", "hy-mt2-7b-q8"\]\);\s*return arguments\.AsReadOnly\(\);/, 'Unrecognized model-specific resident startup arguments');
  assert.match(runner, /return BuildArguments\(modelPath, gpu, verifiedModelSha256, "vulkan"\);/, 'Default GPU routing must preserve Vulkan');
  assert.match(runner, /if \(gpuBackend is not \("vulkan" or "cuda"\)\) throw new ArgumentOutOfRangeException\(nameof\(gpuBackend\)\);/, 'Resident GPU routing requires the validated CUDA/Vulkan allowlist');
  const nativeArguments = ['--model', '$MODEL', '--mode', 'cpu'];
  const windowsProcess = readText(root, 'src/DropSpace.Infrastructure/Lyrics/WindowsInferenceProcess.cs');
  const memoryMiB = name => Number(singleMatch(windowsProcess, new RegExp(`internal const long ${name} = ([0-9]+)L \\* 1024 \\* 1024 \\* 1024;`, 'g'), `Production ${name}`)) * 1024;
  assert.match(legacyRunner, /ExperimentalLargePlain\.Sha256, StringComparison\.OrdinalIgnoreCase\)\s*\? WindowsInferenceProcess\.Hy7BMaximumMemoryBytes : WindowsInferenceProcess\.MaximumMemoryBytes;/, 'Unrecognized model-specific process memory budgets');
  const perLineSeconds = Number(singleMatch(runner, /_requestTimeout = requestTimeout \?\? TimeSpan\.FromSeconds\((\d+)\);/g, 'Production per-line deadline'));
  assert.match(runner, /private readonly TimeSpan _requestTimeout;/, 'Resident request deadline must be privately owned');
  assert.match(runner, /internal PersistentPlainLyricsRunner\([\s\S]*?TimeSpan\? requestTimeout = null\)/, 'Test deadline injection must remain internal');
  assert.match(runner, new RegExp(`_requestTimeout <= TimeSpan\\.Zero \\|\\| _requestTimeout > TimeSpan\\.FromSeconds\\(${perLineSeconds}\\)`), 'Injected request deadline cannot exceed the production ceiling');
  assert.match(runner, /ObjectDisposedException\.ThrowIf\(_disposed, this\);\s*deadline\.CancelAfter\(_requestTimeout\);/, 'Production request must use the reviewed deadline');
  const executionLimits = {
    wholeSongSeconds: integerConstant('WholeSongSeconds'),
    perLineSeconds,
    memoryMiB: memoryMiB('MaximumMemoryBytes'),
    maximumPromptBytes: integerConstant('MaximumPromptBytes'), maximumOutputBytes: integerConstant('MaximumOutputBytes'),
  };
  const largeModelId = descriptors.find(match => match[1] === 'ExperimentalLargePlain')?.[2];
  const ordinaryModelId = descriptors.find(match => match[1] === 'ExperimentalPlain')?.[2];
  assert.match(readText(root, 'src/DropSpace.Infrastructure/Lyrics/CpuInferenceMemoryPolicy.cs'), /internal const long SystemReserveBytes = 1024L \* 1024 \* 1024;/, 'CPU memory admission reserve changed; update capture adapter');
  const modelProfiles = Object.fromEntries(shippingModels.map(model => {
    assert.ok([ordinaryModelId, largeModelId].includes(model.id), 'Unrecognized shipping model resource profile');
    const large = model.id === largeModelId;
    return [model.id, {
      nativeArguments: large ? [...nativeArguments, '--model-profile', 'hy-mt2-7b-q8'] : nativeArguments,
      cpuMemoryAdmission: { minimumAvailableBytes: (memoryMiB(large ? 'Hy7BMaximumMemoryBytes' : 'MaximumMemoryBytes') + 1024) * 1024 * 1024, policy: 'process-cap-plus-1GiB-reserve' },
      executionLimits: { ...executionLimits, memoryMiB: memoryMiB(large ? 'Hy7BMaximumMemoryBytes' : 'MaximumMemoryBytes') },
    }];
  }));
  const helper = readText(root, residentSourcePaths[2]);
  const argumentBody = singleMatch(helper, /std::vector<std::string> args = \{([\s\S]*?)\};/g, 'Compiled resident sampler arguments');
  const samplerArguments = argumentBody.split(',').map(part => {
    const value = part.trim();
    if (value === 'argv[2]') return '$MODEL';
    assert.ok(/^"(?:[^"\\]|\\.)*"$/.test(value), 'Unrecognized production native argument');
    return JSON.parse(value);
  });
  assert.ok(!samplerArguments.includes('-j'), 'Plain production evidence cannot use a JSON grammar');
  // Beta11 reuses the original reviewed CPU/Vulkan bytes. Never relabel those
  // binaries as compiled from the newer optional research/CUDA helper source.
  // This pin is code-owned; an approval record cannot choose a different runtime.
  const reusedManifestBytes = fs.readFileSync(path.join(root, 'scripts/ai-model-qa/evidence/runtime-37095011004-1/runtime-manifest.json'));
  assert.equal(sha256(reusedManifestBytes), 'b492e2f0413449d69e0e37536b941e9e30c2b31c692732a5385ae8c9c6f9deab', 'Reused native runtime manifest changed');
  const reusedManifest = JSON.parse(reusedManifestBytes.toString('utf8').replace(/^\uFEFF/, ''));
  const currentNativeWorkerSourceSha256 = sha256(residentSourcePaths.map(name => readText(root, name)).join('\n'));
  const resident = {
    protocol: Number(singleMatch(runner, /public const int ProtocolVersion = (\d+);/g, 'Resident protocol version')),
    profile: singleMatch(runner, /public const string ResidentProfileId = "([^"]+)";/g, 'Resident profile identity'),
    sourceSha256: reusedManifest.resident.sourceSha256,
  };
  const admission = readJson(root, admissionPath);
  const fixtureBytes = fs.readFileSync(path.join(root, fixturePath));
  const fixture = JSON.parse(fixtureBytes);
  assert.equal(admission.schemaVersion, 1, 'Unknown audited fixture admission schema');
  assert.equal(admission.recordKind, 'host-fixture-admission-v12', 'Expected the preserved historical v12 host computation');
  assert.equal(admission.modelInferenceExecuted, false, 'Host admission cannot claim model inference');
  assert.equal(admission.semanticApproved, false, 'Host admission cannot claim semantic approval');
  assert.equal(admission.fixtureSha256, sha256(fixtureBytes), 'Audited admission fixture is stale');
  assert.equal(admission.policyVersion, 'contextual-line-admission-v12', 'Historical fixture admission policy version changed');
  assert.equal(admission.policySourceSha256, 'd0537cdf00831d18eac2fb86352443ad4e069802a91180ef7135c7d3ba6ad2df', 'Historical fixture admission policy source hash is stale');
  assert.deepEqual(Object.keys(admission.targets).sort(), ['en', 'zh-Hans']);
  assert.deepEqual(admission.semanticLanguages, [
    { firstLineId: 0, lastLineId: 11, language: 'en' }, { firstLineId: 12, lastLineId: 23, language: 'ja' },
    { firstLineId: 24, lastLineId: 35, language: 'ko' }, { firstLineId: 36, lastLineId: 47, language: 'zh-Hans' },
  ], 'Independent original fixture language annotations changed');
  for (const [target, rows] of Object.entries(admission.targets)) {
    assert.equal(rows.length, fixture.Lines.length, 'Admission audit must cover every source row');
    for (const [id, row] of rows.entries()) {
      assert.equal(row.lineId, id); assert.equal(row.sourceText, fixture.Lines[id].Text);
      assert.ok(['Unknown', 'Lexical', 'Context', 'Explicit'].includes(row.evidenceKind));
      assert.ok(Number.isFinite(row.confidence) && row.confidence >= 0 && row.confidence <= 1, 'Invalid admission confidence');
      assert.ok(row.detectedLanguage === null || typeof row.detectedLanguage === 'string' && row.detectedLanguage.length > 0, 'Invalid detected language');
      assert.ok(typeof row.eligible === 'boolean');
      if (row.confidence >= 0.9) assert.equal(row.detectedLanguage, admission.semanticLanguages.find(group => id >= group.firstLineId && id <= group.lastLineId).language, 'Confident admission language conflicts with independent fixture annotation');
      // The fixture has no credits. Unknown language abstains for every target;
      // confident foreign evidence remains eligible.
      const decision = fixtureAdmissionDecision(row.sourceText, target, row);
      assert.equal(row.eligible, decision === 'Translate', 'Historical fixture admission must match the v12 three-state policy');
      assert.equal(row.reason, decision === 'SameLanguage' ? 'same-target-language' : decision === 'Abstain' ? 'no-eligible-segments' : row.confidence >= 0.9 ? 'identified-foreign-language' : 'unknown-language-retained');
      assert.deepEqual(row.segments, row.eligible ? [{ segmentIndex: 0, text: row.sourceText, sha256: sha256(row.sourceText) }] : []);
    }
  }
  const sources = readSourceFingerprint(root);
  return {
    releaseVersion: readText(root, 'RELEASE_VERSION').trim(),
    shippingModels,
    runtime: {
      id: singleMatch(runtimeBuild, /runtimeId = '([^']+)'/g, 'Runtime identity'),
      sourceCommit: singleMatch(runtimeBuild, /^\$commit = '([a-f0-9]{40})'/gm, 'Runtime source commit'),
      resident,
    },
    currentNativeWorkerSourceSha256,
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
    executionLimits,
    modelProfiles,
    sources,
    fixtureAdmission: { path: admissionPath, sha256: sha256(readText(root, admissionPath)), scopeBoundary: 'historical-v12-host-computation-only; not Beta16 policy or language-model qualification', ...admission },
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

function validateRunnerOutput(root, reference, scope, variant, model) {
  const output = JSON.parse(readEvidence(root, reference, 'Production runner output').toString('utf8').replace(/^\uFEFF/, ''));
  assert.equal(output.schemaVersion, 2, 'Admission-aware runner output schema 2 is required; old QA cannot be relabeled');
  assert.equal(output.kind, 'production-runner-output', 'Output must preserve actual production runner results');
  assert.deepEqual(output.model, { id: model.id, sha256: model.sha256, bytes: model.bytes }, 'Runner output needs its own captured model identity');
  assert.equal(output.targetLanguage, reference.targetLanguage, 'Runner output target mismatch');
  assert.ok(['Translated', 'NoUsefulTranslation'].includes(output.outcome), 'Runner output did not complete the production song');
  assert.equal(output.complete, true, 'Production song capture is incomplete');
  assert.equal(output.playbackPositionSeconds, 0, 'Production capture playback position must be controlled');
  assert.equal(output.residentProcessReuseConfirmed, true, 'Production capture did not confirm resident reuse');
  const fixture = readJson(root, fixturePath);
  const expectedAdmission = scope.fixtureAdmission.targets[reference.targetLanguage];
  assert.equal(output.admissionPolicyVersion, scope.fixtureAdmission.policyVersion, 'Runner admission policy mismatch');
  assert.deepEqual(output.admission, expectedAdmission, 'Actual admission differs from independently audited fixture expectations');
  const lines = expectedAdmission.filter(row => row.eligible);
  const segments = lines.flatMap(row => row.segments.map(segment => ({ lineId: row.lineId, segmentIndex: segment.segmentIndex, segmentSha256: segment.sha256, sourceText: segment.text })));
  assert.ok(Array.isArray(output.calls), 'Production native call records are required');
  assert.deepEqual(output.calls.map(call => ({ lineId: call.lineId, segmentIndex: call.segmentIndex, segmentSha256: call.segmentSha256, sourceText: call.sourceText })), segments,
    'Production capture must contain every eligible semantic segment once, with original row IDs');
  validateHostMemory(output.hostMemoryBefore, scope, model);
  const processes = new Set();
  for (const call of output.calls) {
    assert.equal(call.verifiedModelSha256, model.sha256, 'Actual inference model hash differs from captured model');
    const prompt = scope.promptTemplate.replace('{0}', () => scope.targetNames[reference.targetLanguage]).replace('{1}', () => call.sourceText);
    assert.equal(call.prompt, prompt, 'Captured prompt differs from the actual production template/source');
    nonempty(call.output, 'Complete runner-returned output');
    assert.ok(Buffer.byteLength(call.output, 'utf8') <= scope.executionLimits.maximumOutputBytes, 'Runner output exceeds production byte limit');
    assert.equal(call.status, 'returned', 'Production native call did not return successfully');
    assert.equal(call.phase, 'cold', 'Production native call is not the cold generation');
    assert.equal(call.targetLanguage, reference.targetLanguage, 'Production native call target mismatch');
    assert.ok(Number.isSafeInteger(call.nativeProcess?.id) && call.nativeProcess.id > 0, 'Observed resident PID is required');
    validateProcessMemory(call.nativeProcess);
    assert.ok(Number.isFinite(Date.parse(call.nativeProcess.startedAt)), 'Observed resident process start is required');
    const executable = call.nativeProcess.executable?.split(/[\\/]/).at(-1);
    assert.equal(executable, variant === 'baseline' ? 'plain-lyrics-worker.exe' : 'plain-lyrics-worker-avx2.exe', 'Observed resident executable differs from the tested CPU variant');
    processes.add(`${call.nativeProcess.id}:${call.nativeProcess.startedAt}`);
  }
  assert.equal(output.finalDocument?.lines?.length, fixture.Lines.length, 'Final document lost display rows');
  for (const [id, line] of output.finalDocument.lines.entries()) {
    assert.equal(line.text, fixture.Lines[id].Text, 'Final original lyric text changed');
    assert.equal(line.start, fixture.Lines[id].Start, 'Final original lyric timing changed');
    assert.equal(line.end, fixture.Lines[id].End, 'Final original lyric timing changed');
    assert.deepEqual(line.words, fixture.Lines[id].Words, 'Final original lyric word timing changed');
    assert.equal(line.sourceLanguage, fixture.Lines[id].SourceLanguage ?? null, 'Final original source metadata changed');
    if (expectedAdmission[id].eligible) {
      const calls = output.calls.filter(call => call.lineId === id);
      const aggregate = calls.map(call => call.output).join(' ');
      const unchanged = aggregate.trim() === expectedAdmission[id].segments.map(segment => segment.text).join(' ').trim();
      assert.equal(line.secondary, unchanged ? null : aggregate, 'Final translated row differs from observed runner output');
      assert.equal(line.translationOrigin, unchanged ? 0 : 2, 'Final translation provenance mismatch');
      assert.equal(line.translationLanguage, unchanged ? null : reference.targetLanguage, 'Final translation target mismatch');
    } else {
      assert.equal(line.secondary, null, 'Excluded source row gained a translation');
      assert.equal(line.translationOrigin, 0, 'Excluded source row gained AI provenance');
    }
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
  return output;
}

function validateProcessMemory(process) {
  for (const field of ['workingSetBytes', 'peakWorkingSetBytes', 'privateMemoryBytes'])
    assert.ok(Number.isSafeInteger(process?.[field]) && process[field] > 0, 'Observed native process memory is required');
}

function validateHostMemory(memory, scope, model) {
  assert.equal(memory?.requiredAvailableBytes, scope.modelProfiles[model.id].cpuMemoryAdmission.minimumAvailableBytes, 'CPU host memory allowance mismatch');
  for (const field of ['availablePhysicalBytes', 'availableCommitBytes']) assert.ok(Number.isSafeInteger(memory[field]) && memory[field] >= 0, 'Host memory observation is required');
  timestamp(memory.observedAt, 'Host memory observation time');
}

function validateGpuDefaultProbe(root, reference, model, scope, runtime, variant) {
  const probe = JSON.parse(readEvidence(root, reference, 'GPU-default probe').toString('utf8').replace(/^\uFEFF/, ''));
  assert.equal(probe.schemaVersion, 1, 'Unsupported GPU-default probe schema');
  assert.equal(probe.kind, 'production-gpu-default-probe', 'A real production GPU-default probe is required');
  validateHostMemory(probe.hostMemoryBefore, scope, model);
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
  validateProcessMemory(probe.observedProcess);
  const executable = probe.observedProcess.executable?.split(/[\\/]/).at(-1);
  assert.equal(executable, probe.actualBackend === 'vulkan' ? 'plain-lyrics-worker-vulkan.exe' : variant === 'baseline' ? 'plain-lyrics-worker.exe' : 'plain-lyrics-worker-avx2.exe', 'GPU-default mode differs from observed executable');
}

function validateNativeEvidence(root, reference, model, scope, runtime, reviewedAt) {
  assert.equal(reference.kind, 'native-output', 'Source inspection and diagnostic summaries alone are not runtime evidence');
  assert.equal(reference.fixtureSha256, scope.fixture.sha256, 'Runtime evidence must use the pinned original QA fixture');
  const envelope = JSON.parse(readEvidence(root, reference, `Model ${model.id} native evidence`).toString('utf8'));
  assert.equal(envelope.schemaVersion, 3, 'Admission-aware capture requires native evidence schema 3; old QA cannot be relabeled');
  assert.equal(envelope.kind, 'native-output', 'Native evidence must be a provenance envelope');
  assert.deepEqual(envelope.model, { id: model.id, sha256: model.sha256, bytes: model.bytes }, 'Native evidence model identity mismatch');
  assert.equal(envelope.fixtureSha256, scope.fixture.sha256, 'Native evidence fixture mismatch');
  assert.equal(envelope.promptVersion, scope.promptVersion, 'Native evidence prompt version mismatch');
  assert.equal(envelope.promptProfile, productionPromptProfile, 'Only the production prompt profile can support shipping approval');
  assert.equal(envelope.outputSchema, productionOutputSchema, 'Native evidence must use the production output schema');
  for (const key of ['backendId', 'acceptanceVersion', 'samplerIdentity', 'captureMethod'])
    assert.equal(envelope[key], scope[key], `Native evidence ${key} mismatch`);
  const modelProfile = scope.modelProfiles[model.id];
  assert.deepEqual(envelope.executionLimits, modelProfile.executionLimits, 'Native evidence execution limits mismatch');
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
  assert.equal(configuration.schemaVersion, 3, 'Admission-aware configuration schema 3 is required');
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
  assert.deepEqual(configuration.executionLimits, modelProfile.executionLimits, 'Native configuration execution limits mismatch');
  assert.deepEqual(configuration.nativeArguments, modelProfile.nativeArguments, 'Native configuration differs from actual production arguments');
  assert.deepEqual(configuration.samplerArguments, scope.samplerArguments, 'Native configuration differs from compiled resident sampler arguments');
  const checks = envelope.technicalChecks;
  assert.deepEqual(checks?.coldTargets, ['en', 'zh-Hans'], 'Both complete cold production target runs are required');
  assert.deepEqual(checks?.cacheTargets, ['en', 'zh-Hans'], 'Both production cache replays are required');
  assert.equal(checks.cacheAdditionalInferenceCalls, 0, 'Production cache replay launched extra inference');
  for (const key of ['cancellationObserved', 'cleanupConfirmed', 'sourceIdentityUnchanged', 'modelIdentityUnchanged', 'runtimeIdentityUnchanged'])
    assert.equal(checks[key], true, `Production native check did not pass: ${key}`);
  assert.ok(Array.isArray(envelope.outputs) && envelope.outputs.length > 0, 'Native evidence needs original output references');
  const targets = new Set();
  const coldOutputs = new Map();
  for (const output of envelope.outputs) {
    assert.equal(output.kind, 'runner-output', 'Native evidence must preserve runner-returned output, not relabeled raw output');
    assert.ok(['en', 'zh-Hans'].includes(output.targetLanguage), 'Native evidence output target is unsupported');
    coldOutputs.set(output.targetLanguage, validateRunnerOutput(root, output, scope, variant, model));
    targets.add(output.targetLanguage);
  }
  assert.deepEqual([...targets].sort(), ['en', 'zh-Hans'], 'Native evidence needs both shipping target languages');
  assert.equal(envelope.outputs.length, 2, 'Native evidence needs exactly one complete capture for both shipping target languages');
  assert.deepEqual(envelope.cacheOutputs?.map(reference => reference.targetLanguage), ['en', 'zh-Hans'], 'Actual per-target cache observations are required');
  for (const reference of envelope.cacheOutputs) {
    const cache = JSON.parse(readEvidence(root, reference, 'Cache replay output'));
    const cold = coldOutputs.get(reference.targetLanguage);
    assert.deepEqual(cache.model, envelope.model, 'Cache replay model mismatch');
    assert.equal(cache.targetLanguage, reference.targetLanguage);
    assert.equal(cache.additionalInferenceCalls, 0, 'Cache replay launched extra inference');
    assert.equal(cache.matchesCold, true, 'Cache replay did not match cold generation');
    for (const result of [cache.preflight, cache.repeated]) {
      assert.equal(result?.outcome, cold.outcome === 'Translated' ? 0 : 1, 'Cache replay outcome mismatch');
      assert.deepEqual(result.document, cold.finalDocument, 'Cache replay changed the final document');
    }
  }
  validateGpuDefaultProbe(root, envelope.gpuDefaultProbe, model, scope, runtime, variant);
  return variant;
}

function timestamp(value, label) {
  assert.ok(typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/.test(value), `${label} must be an explicit UTC timestamp`);
  const parsed = Date.parse(value);
  assert.ok(Number.isFinite(parsed) && new Date(parsed).toISOString() === value.replace('Z', '.000Z'), `${label} is invalid`);
  return parsed;
}

function validateExperimentalBeta(root, report, scope, reviewedAt) {
  assert.equal(scope.releaseVersion, experimentalBetaVersion, `Owner acceptance is only for ${experimentalBetaVersion}`);
  assert.equal(report.semanticApproved, false, 'Experimental Beta must not claim semantic approval');
  assert.equal(scope.executionLimits.wholeSongSeconds, 600, 'Owner acceptance covers the 600-second processing ceiling');
  assert.equal(report.userAcceptance?.releaseVersion, experimentalBetaVersion, 'Owner acceptance must name this exact Beta');
  assert.equal(report.userAcceptance?.acceptsIncompleteModelValidation, true, 'Explicit acceptance of incomplete model validation is required');
  assert.ok(report.acceptedLimitations.length > 0, 'Experimental Beta needs disclosed limitations');
  assert.deepEqual(scope.shippingModels.map(model => model.id), ['hy-mt2-18-q8-plain-beta', 'hy-mt2-7b-q8-plain-beta'], 'Owner acceptance covers only the existing Q8 models');
  assert.match(readText(root, 'src/DropSpace.Core/Models/NativeIslandSettings.cs'), /public bool AiTranslationEnabled \{ get; init; \}\s*\n/, 'AI must remain off by default');
  assert.deepEqual(report.models?.map(({ id, sha256, bytes }) => ({ id, sha256, bytes })), scope.shippingModels, 'Every shipping model needs an honest validation record');
  for (const model of report.models) {
    assert.equal(model.verdict, 'unverified', 'Owner acceptance cannot mark an unverified model approved');
    nonempty(model.summary, `Model ${model.id} limitations`);
    assert.deepEqual(model.validation?.map(({ variant, targetLanguage }) => ({ variant, targetLanguage })),
      ['baseline', 'avx2'].flatMap(variant => ['en', 'zh-Hans'].map(targetLanguage => ({ variant, targetLanguage }))),
      'Record both targets and CPU variants, including unexecuted validation');
    for (const observation of model.validation) {
      assert.ok(['not-run', 'timed-out', 'incomplete', 'complete-unreviewed'].includes(observation.status), 'Experimental validation cannot be labeled pass or approved');
      if (observation.status === 'not-run') {
        for (const key of ['configuration', 'runnerOutput', 'technicalResults'])
          assert.equal(observation[key], null, 'Unexecuted validation must not claim captured evidence');
        continue;
      }
      // Historical failed captures retain their own source/runtime/budget. They
      // disclose limitations; they never qualify the new shipping inputs.
      const configuration = JSON.parse(readEvidence(root, observation.configuration, 'Experimental capture configuration').toString('utf8').replace(/^\uFEFF/, ''));
      const output = JSON.parse(readEvidence(root, observation.runnerOutput, 'Experimental runner output').toString('utf8').replace(/^\uFEFF/, ''));
      assert.equal(configuration.schemaVersion, 3, 'Experimental capture configuration schema mismatch');
      assert.equal(configuration.captureMethod, productionCaptureMethod, 'Experimental capture must use the production path');
      assert.deepEqual({ id: configuration.modelId, sha256: configuration.modelSha256, bytes: configuration.modelBytes },
        { id: model.id, sha256: model.sha256, bytes: model.bytes }, 'Experimental capture model mismatch');
      assert.equal(configuration.runtimeVariant, observation.variant, 'Experimental capture variant mismatch');
      for (const key of ['sourceFingerprintSha256', 'runtimeManifestSha256']) assert.match(configuration[key] ?? '', hashPattern, 'Captured input identity is required');
      assert.ok([300, 600].includes(configuration.executionLimits?.wholeSongSeconds), 'Preserve the actual historical or current processing budget');
      assert.equal(output.schemaVersion, 2, 'Experimental runner schema mismatch');
      assert.equal(output.kind, 'production-runner-output', 'Preserve actual runner output');
      assert.deepEqual(output.model, { id: model.id, sha256: model.sha256, bytes: model.bytes }, 'Experimental output model mismatch');
      assert.equal(output.targetLanguage, observation.targetLanguage, 'Experimental output target mismatch');
      assert.equal(output.complete, observation.status === 'complete-unreviewed', 'Incomplete output cannot be relabeled complete');
      if (output.complete) assert.ok(['Translated', 'NoUsefulTranslation'].includes(output.outcome), 'Completed output must retain its actual outcome');
      if (observation.status === 'timed-out') {
        assert.match(output.error ?? '', /OperationCanceledException|TaskCanceledException|TimeoutException/, 'Timeout needs the original cancellation/timeout failure');
        assert.ok(output.elapsedMilliseconds >= configuration.executionLimits.wholeSongSeconds * 1000, 'Timeout predates the captured processing ceiling');
      }
      const technical = JSON.parse(readEvidence(root, observation.technicalResults, 'Experimental operational checks').toString('utf8').replace(/^\uFEFF/, ''));
      assert.equal(technical.schemaVersion, 1, 'Experimental operational schema mismatch');
      assert.equal(technical.semanticStatus, 'not-evaluated', 'Operational checks do not establish semantic approval');
      for (const key of ['cancellationObserved', 'cleanupConfirmed', 'sourceIdentityUnchanged', 'modelIdentityUnchanged', 'runtimeIdentityUnchanged', 'configurationUnchanged'])
        assert.equal(technical[key], true, `Experimental acceptance cannot waive operational check: ${key}`);
    }
  }
  assert.ok(timestamp(report.userAcceptance.acceptedAt, 'User acceptance time') <= reviewedAt, 'User acceptance postdates the review');
}

export function validateApproval(root, { now = Date.now(), runtimeManifestPath, releaseBundleDirectory, expectedCommit } = {}) {
  const approval = readJson(root, approvalPath);
  assert.equal(approval.schemaVersion, 1, 'Unsupported AI approval schema');
  const experimental = approval.status === experimentalBetaStatus;
  assert.ok(approval.status === 'approved' || experimental, 'AI semantic release approval is pending or absent; structural success is not approval');
  const scope = readScope(root);
  nonempty(scope.releaseVersion, 'Release version');
  assert.deepEqual(approval.scope, scope, 'AI approval is stale: shipping model, runtime, prompt/parser sources, release or fixture changed');
  const report = JSON.parse(readEvidence(root, approval.review, 'Semantic review').toString('utf8'));
  assert.equal(report.schemaVersion, 1, 'Unsupported semantic review schema');
  assert.equal(report.kind, experimental ? experimentalBetaStatus : 'semantic-review', 'Native diagnostics are not a semantic review or owner acceptance');
  assert.equal(report.verdict, experimental ? experimentalBetaStatus : 'approved', 'Semantic review has not approved release or owner acceptance is absent');
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
  if (experimental) validateExperimentalBeta(root, report, scope, reviewedAt);
  else {
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

export function publicationDecision(root, options = {}) {
  validateApproval(root, options);
  const status = readJson(root, approvalPath).status;
  return { authorized: true, semanticApproved: status === 'approved', mode: status === 'approved' ? 'semantic-approved' : experimentalBetaStatus };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const args = process.argv.slice(2);
    if (args.length === 1 && args[0] === '--print-scope') {
      // Read-only preparation aid. It never writes or approves a manifest.
      console.log(JSON.stringify(readScope(rootDirectory), null, 2));
    } else {
      assert.ok(args.length === 0 || (args.length === 2 && ['--runtime-manifest', '--release-bundle', '--export-runtime-contract', '--github-output'].includes(args[0])), 'Usage: node scripts/test-ai-release-approval.mjs [--runtime-manifest PATH | --release-bundle DIRECTORY | --export-runtime-contract PATH | --github-output PATH | --print-scope]');
      if (args[0] === '--export-runtime-contract') {
        const contract = readApprovedRuntimeContract(rootDirectory);
        fs.mkdirSync(path.dirname(path.resolve(args[1])), { recursive: true });
        fs.writeFileSync(args[1], JSON.stringify(contract, null, 2) + '\n');
      } else {
        const decision = publicationDecision(rootDirectory, {
          runtimeManifestPath: args[0] === '--runtime-manifest' ? args[1] : undefined,
          releaseBundleDirectory: args[0] === '--release-bundle' ? args[1] : undefined,
          expectedCommit: process.env.GITHUB_SHA,
        });
        if (args[0] === '--github-output') fs.appendFileSync(args[1], `authorized=${decision.authorized}\nsemantic_approved=${decision.semanticApproved}\nmode=${decision.mode}\n`);
      }
      console.log(readJson(rootDirectory, approvalPath).status === experimentalBetaStatus
        ? `Owner-accepted experimental ${experimentalBetaVersion} bindings are valid. Model validation remains unverified; no semantic approval is claimed.`
        : 'AI semantic approval and evidence bindings are valid. The recorded semantic review, not this structural check, establishes quality.');
    }
  } catch (error) {
    console.error(`AI release blocked: ${error.message}`);
    process.exitCode = 1;
  }
}

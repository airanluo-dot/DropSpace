# Round 3 — App Services complete-source review

Reviewed the fresh immutable snapshot at **`f22bcc4bdd5c2d477a7880fe02c87f6035392ab9`** under `/workspace/scratch/beta18-round3/src`. This is the third sequential review, after the PR 107/108/109 integration, both Island features, and the first/second review fixes represented by that commit. Prior reports, their findings, and their source-read counts were not reused as fresh evidence.

## Full-read coverage and identity

All **120 files / 27,316 physical lines** in `round3-app-services-scope.txt` were physically read from first to final line, including unchanged files and diagnostic/smoke source. The partition combines distinct, nonoverlapping fresh reads:

- App Services owner: **85 files / 18,100 lines**. This includes the entire 1,929-line ClipboardCaptureService, the entire 1,427-line OLE service, all virtual-file/classifier code, every glow/motion/material/native-placement service, the entire 1,584-line OverlayWindowService including diagnostics, settings/recovery, update verification/launcher, activation/share/undo, image/thumbnail services, and all remaining scope files.
- Media supplemental owner: **33 files / 6,473 lines**, documented in [round3-app-services-media.md](round3-app-services-media.md). This read was performed after that owner's Core partition finished and is credited only to Services here.
- Drag supplemental owner: **2 files / 2,743 lines**, documented in [round3-app-services-drag.md](round3-app-services-drag.md): the complete DragSessionDetector and EphemeralOleDragProbe, including diagnostics/native declarations.

Line-numbered source was emitted in full-file or consecutive chunks. Truncated combined outputs were repaired by full rereads of AcrylicShutterBlur/AppLanguageService and IslandGlowRasterizer/IslandGlowWindow; the drag owner separately reread its truncated 1,301–1,625 range. Hashing is an identity check, not the source-reading method.

Frozen bytes match the canonical Round 3 source manifest for **120/120 files**. Exact scope has no duplicate paths or exclusions. Supplemental caller/context reads and historical comparisons are excluded from the unique 120-file / 27,316-line total.

## Fresh confirmed finding

### R3-SVC-01 — P2: a monitor-edge Smart Drag probe covers the pointer instead of leaving it in its hole

`EphemeralOleDragProbe.CalculateMonitorAwareCenter` (361–380) clamps the **window center** inward by half the outer size. `ApplyHollowRegion` (313–319) leaves its hole at that clamped center, while the class's native-input contract says the cursor starts inside the real region hole (25–26). These conditions diverge near a display edge. With the shipping 144-pixel outer size / 12-pixel hole and a pointer at `monitor.Left + 1`, the window begins at `monitor.Left`; its pointer-local X is 1, while the centered hole spans local X 66 through 77. The native ring therefore covers the pointer. The same geometry occurs at the top edge, where Smart Drag wake is commonly requested.

The shipping caller samples the live cursor and passes it unchanged into this constructor (`OleDragDropService` 108–120, 128, 134–139). No earlier caller constraint excludes display edges. `VerifyNativeContract` (216–221) checks the artificial center rather than the actual pointer, so its source-level assertion does not catch the edge mismatch. Default and allowed sizes are established in `SmartDragRuntimePolicy` 5–14 and `SmartDragProbeOptions` 15–25.

The observed defect here is the native input-region geometry, established from source and arithmetic. Windows OLE callback timing/target-transfer behavior was not executed and is not claimed as proven. The minimal fix is to preserve the bounded window placement but build the hole around the actual pointer's local coordinates, including its clipped edge/corner intersection, and update the existing native-contract assertion to check that pointer. The initial frozen-source review made no production edits; the separately authorized correction follows.

## Authorized correction and independent source review

After all **425 files** across the four Round 3 partitions had completed fresh full reads, root authorized the narrow R3-SVC-01 correction. The drag supplemental owner implemented it in live `src/DropSpace.App/Services/Ole/EphemeralOleDragProbe.cs`; the Services owner independently read the complete source diff and affected region/contract paths.

The existing centered hole offset is translated by `_origin - _probeCenter` independently in X and Y (live 317–326). The same outer-minus-inner region operation clips the removed hole to the monitor-clamped window at edges and corners. In the default left-edge example, the hole moves from local `[66, 78)` to `[-5, 7)`, whose intersection with the window removes `[0, 7)` and contains the original cursor at X = 1. When the probe is not clamped, the translation is zero, retaining its prior centered geometry and odd/even size behavior. The native contract check now converts the original cursor to actual window-local coordinates and verifies that point is excluded; its positive ring sample moves to the opposite X side so it cannot become a sample inside the shifted hole (live 216–224).

The inward monitor clamp, native region-handle transfer and cleanup, OLE callback lifetime, timing, Smart Drag evidence policy, and intake path are unchanged. The live source diff contains **12 added / 5 removed lines**; the reviewed immutable file remains **810 lines** in the coverage ledger, while the corrected live file has **817 lines**. The detailed correction is documented in [round3-app-services-drag.md](round3-app-services-drag.md). Scoped `git diff --check` passed independently for the source and supporting report. **Executed test cases remain 0**; no builds, probes, native execution, or commits were performed. This closes the source geometry mismatch without claiming Windows OLE runtime qualification.

Separately, the Services owner independently source-checked the Infrastructure owner's DropLink rollback finding and its authorized diff. A published destination is public and no longer held in exact-file custody; reparse containment cannot justify deleting the current file at that path. The narrow correction preserves published partial outputs with the existing failed response's completed-path list. Its bookkeeping records a successful `File.Move` before post-publication revalidation can throw. Failure state, single-flight finalization, and generated temporary cleanup remain intact; the shipping UI still requires `Completed` state to report success. These contextual reads add no files or lines to the Services partition and no duplicate finding. No Infrastructure production edit was made by the Services owner.

## Glow / animation steering and excluded observations

The user subsequently confirmed reduced motion and canceled the dedicated glow/animation investigation. This source trace is retained as review evidence; no glow, motion, material, or preference implementation change is authorized by that investigation.

The user's new report says both Regular and Simple glow cannot open, and then clarifies that all Island animations fail in the latest Beta while Beta 16 worked. Fresh full reads found positive rasterized alpha for an eligible envelope even without loopback audio; Regular/Simple share the controller/window presentation and native-resource path. Media capture absence is not itself a complete glow veto. Core and UI owners trace eligibility and the shared animation gates separately.

Read-only historical comparison of `v0.3.1-beta.16..v0.3.1-beta.17` found **no changes** to IslandGlowController, IslandGlowWindow, IslandGlowRasterizer, OverlayWindowInterop, WindowsCompatibilityService, OverlayMaterialController, OverlayMotionOrchestrator or OverlayCompositionAnimator. Those native glow services also have no changes from Beta 17 to this frozen commit. The changed surfaces are shared preference snapshotting and media presentation/activation. This rules out attributing a new native ABI edit to the known-good-to-bad transition; it does not prove live ABI or resource behavior correct.

`IslandGlowController` 178–185 permanently sets `_failed`, stops its timer and disposes its window after a Win32/overflow/argument presentation error; changing either glow style cannot recover that controller. That recovery limitation is source-established, but there is no source-confirmed first native error explaining the reported Beta 16→17 regression. It is therefore **not promoted to the regression root cause or used to justify a speculative fix**. The existing POINT/SIZE/BLENDFUNCTION/BITMAPINFOHEADER declarations, DIB/DC selection, premultiplied pixel production, `UpdateLayeredWindow`, visibility gates and Z-order operations were read end-to-end; no runtime success is inferred from this inspection.

Other excluded observations: media final-artwork publication has a narrow native-event window but serialized consumption does not establish overwriting a newer published track; externally mutable file metadata can race share preparation but ownership/transport validation remains with Infrastructure; unbounded nearby-peer response concerns were forwarded to the Infrastructure owner for primary adjudication. No duplicate cross-partition findings are counted here. CPU/GPU/model latency or semantic quality is not inferred from source.

## What actually ran and honest limits

Only source reads, file enumeration, physical-line accounting, SHA-256 identity checks, and read-only Git history/diffs ran. **Actual executed test cases: 0.** The hard ceiling remains **21** against the original **2,124** cases. No tests, fixtures, probes, builds, app launch, Windows/native/audio/GPU execution, AI inference, remote mutation, commits or production edits occurred during this review. Diagnostic code was read but not invoked.

This report establishes complete fresh source coverage, source-supported defects and minimum fix candidates. It cannot qualify live Windows animation/glow/COM/OLE behavior, monitor/DPI transitions, the actual deployed Beta package, music-player response, native-resource growth, or GPU/model performance. The dedicated animation/glow investigation was closed by the user after the reduced-motion clarification; no claim of a production fix is made.

## Complete physical-read ledger

Every range below is a full physical read. “Match” means agreement with `round3-source-manifest.json` for the immutable commit. Detailed media and drag evidence lives in the linked supplemental reports. The App Services owner's machine-readable ledger is `/workspace/scratch/beta18-round3/scope/app-services-owner-fullread.json`.

| File | Physical read | Fresh owner | Frozen manifest |
|---|---:|---|---|
| `src/DropSpace.App/Services/AcrylicCoverageBackdropTarget.cs` | 1–147 | App Services owner | Match |
| `src/DropSpace.App/Services/AcrylicCoverageMask.cs` | 1–254 | App Services owner | Match |
| `src/DropSpace.App/Services/AcrylicShutterBlur.cs` | 1–212 | App Services owner | Match |
| `src/DropSpace.App/Services/AppLanguageService.cs` | 1–49 | App Services owner | Match |
| `src/DropSpace.App/Services/Audio/ProcessLoopbackInterop.cs` | 1–65 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Audio/WindowsProcessLoopbackService.cs` | 1–207 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/AuthenticodeTrustedUpdateVerifier.cs` | 1–123 | App Services owner | Match |
| `src/DropSpace.App/Services/BackdropLifetimeSmoke.cs` | 1–60 | App Services owner | Match |
| `src/DropSpace.App/Services/ClipboardAccessPolicy.cs` | 1–44 | App Services owner | Match |
| `src/DropSpace.App/Services/ClipboardCaptureDiagnostics.cs` | 1–267 | App Services owner | Match |
| `src/DropSpace.App/Services/ClipboardCaptureService.cs` | 1–1929 | App Services owner | Match |
| `src/DropSpace.App/Services/ClipboardIntegrationSmoke.cs` | 1–424 | App Services owner | Match |
| `src/DropSpace.App/Services/ClipboardNotificationService.cs` | 1–179 | App Services owner | Match |
| `src/DropSpace.App/Services/ClipboardProviderReadLifetime.cs` | 1–51 | App Services owner | Match |
| `src/DropSpace.App/Services/CompositionEffectDescriptions.cs` | 1–344 | App Services owner | Match |
| `src/DropSpace.App/Services/CrashDiagnosticsService.cs` | 1–82 | App Services owner | Match |
| `src/DropSpace.App/Services/CrossDeviceClipboardService.cs` | 1–290 | App Services owner | Match |
| `src/DropSpace.App/Services/DeploymentModeService.cs` | 1–52 | App Services owner | Match |
| `src/DropSpace.App/Services/DeviceHandoffService.cs` | 1–314 | App Services owner | Match |
| `src/DropSpace.App/Services/DeviceHandoffUseCase.cs` | 1–54 | App Services owner | Match |
| `src/DropSpace.App/Services/Diagnostics/LyricsLanguageSmoke.cs` | 1–66 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Diagnostics/MusicVisualSmoke.cs` | 1–717 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Diagnostics/MusicVisualSmokeOptions.cs` | 1–56 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/DispatcherQueueExtensions.cs` | 1–55 | App Services owner | Match |
| `src/DropSpace.App/Services/DisplayIdentityService.cs` | 1–251 | App Services owner | Match |
| `src/DropSpace.App/Services/DisplayTopologyWatcher.cs` | 1–164 | App Services owner | Match |
| `src/DropSpace.App/Services/Dlc/AiModelDlcProvider.cs` | 1–41 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Dlc/CudaRuntimeDlcProvider.cs` | 1–49 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Dlc/DlcManagerService.cs` | 1–312 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Dlc/DlcProgressPresentation.cs` | 1–20 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Dlc/NeteaseComponentsDlcProvider.cs` | 1–111 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/DragSessionDetector.cs` | 1–1933 | Drag supplemental owner | Match |
| `src/DropSpace.App/Services/DragStorageItemService.cs` | 1–44 | App Services owner | Match |
| `src/DropSpace.App/Services/ForegroundWindowMonitor.cs` | 1–95 | App Services owner | Match |
| `src/DropSpace.App/Services/GlobalQuickPanelHotkeyService.cs` | 1–364 | App Services owner | Match |
| `src/DropSpace.App/Services/ImageDecoderPreflight.cs` | 1–24 | App Services owner | Match |
| `src/DropSpace.App/Services/InnoUpdateInstallerLauncher.cs` | 1–26 | App Services owner | Match |
| `src/DropSpace.App/Services/IslandAcrylicBackdrop.cs` | 1–143 | App Services owner | Match |
| `src/DropSpace.App/Services/IslandGlowController.cs` | 1–208 | App Services owner | Match |
| `src/DropSpace.App/Services/IslandGlowRasterizer.cs` | 1–295 | App Services owner | Match |
| `src/DropSpace.App/Services/IslandGlowWindow.cs` | 1–175 | App Services owner | Match |
| `src/DropSpace.App/Services/IslandMotionBlurPolicy.cs` | 1–159 | App Services owner | Match |
| `src/DropSpace.App/Services/IslandTransparentBackdrop.cs` | 1–42 | App Services owner | Match |
| `src/DropSpace.App/Services/ItemProjectionService.cs` | 1–39 | App Services owner | Match |
| `src/DropSpace.App/Services/ItemSharingService.cs` | 1–190 | App Services owner | Match |
| `src/DropSpace.App/Services/MaintenanceShutdownService.cs` | 1–110 | App Services owner | Match |
| `src/DropSpace.App/Services/Media/AiLyricsService.cs` | 1–525 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Media/BoundedMediaOperation.cs` | 1–103 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Media/LyricsRapidSkipDiagnostic.cs` | 1–70 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Media/MediaApplicationIconService.cs` | 1–48 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Media/MediaArtworkService.cs` | 1–38 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Media/MediaEventSubscription.cs` | 1–49 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Media/MediaExperienceService.cs` | 1–777 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Media/MediaLyricsRefreshRequest.cs` | 1–19 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Media/MediaProcessResolver.cs` | 1–61 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Media/MediaSessionOwner.cs` | 1–53 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Media/MediaSoftRestartOperation.cs` | 1–67 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Media/MediaSubscriptionAdmission.cs` | 1–12 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Media/QqMusicLoginService.cs` | 1–198 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Media/RetirableMediaWork.cs` | 1–127 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Media/WindowsMediaSessionService.cs` | 1–882 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/MonitorLayoutService.cs` | 1–294 | App Services owner | Match |
| `src/DropSpace.App/Services/NativeApplicationIcon.cs` | 1–80 | App Services owner | Match |
| `src/DropSpace.App/Services/NativeAsyncLifetime.cs` | 1–44 | App Services owner | Match |
| `src/DropSpace.App/Services/NativeFolderPickerService.cs` | 1–34 | App Services owner | Match |
| `src/DropSpace.App/Services/NativeSubscriberNotification.cs` | 1–32 | App Services owner | Match |
| `src/DropSpace.App/Services/NativeTrayService.cs` | 1–349 | App Services owner | Match |
| `src/DropSpace.App/Services/NeteaseEnhancement/BetterNcmProbe.cs` | 1–31 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/NeteaseEnhancement/InfLinkDeploymentService.IO.cs` | 1–278 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/NeteaseEnhancement/InfLinkDeploymentService.Packages.cs` | 1–170 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/NeteaseEnhancement/InfLinkDeploymentService.cs` | 1–224 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/NeteaseEnhancement/NeteaseEnhancementService.cs` | 1–236 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/NeteaseEnhancement/NeteaseInstallationProbe.cs` | 1–90 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/NeteaseEnhancement/NeteaseRuntimeInstaller.cs` | 1–79 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/NeteaseEnhancement/NeteaseSmtcVerifier.cs` | 1–692 | Media supplemental owner | Match |
| `src/DropSpace.App/Services/Notifications/WindowsNotificationActivityService.cs` | 1–136 | App Services owner | Match |
| `src/DropSpace.App/Services/Ole/EphemeralOleDragProbe.cs` | 1–810 | Drag supplemental owner | Match |
| `src/DropSpace.App/Services/Ole/OleDropTargetNative.cs` | 1–32 | App Services owner | Match |
| `src/DropSpace.App/Services/Ole/OleFileDataClassifier.cs` | 1–418 | App Services owner | Match |
| `src/DropSpace.App/Services/Ole/QueryOnlyDataObject.cs` | 1–74 | App Services owner | Match |
| `src/DropSpace.App/Services/Ole/SmartDragProbeOptions.cs` | 1–43 | App Services owner | Match |
| `src/DropSpace.App/Services/Ole/SmartDragRuntimePolicy.cs` | 1–41 | App Services owner | Match |
| `src/DropSpace.App/Services/Ole/VirtualFileMaterializer.cs` | 1–670 | App Services owner | Match |
| `src/DropSpace.App/Services/OleDragDropService.cs` | 1–1427 | App Services owner | Match |
| `src/DropSpace.App/Services/OverlayCompositionAnimator.cs` | 1–110 | App Services owner | Match |
| `src/DropSpace.App/Services/OverlayMaterialController.cs` | 1–118 | App Services owner | Match |
| `src/DropSpace.App/Services/OverlayMotionOrchestrator.cs` | 1–95 | App Services owner | Match |
| `src/DropSpace.App/Services/OverlayNativeRegionController.cs` | 1–80 | App Services owner | Match |
| `src/DropSpace.App/Services/OverlayTransparentHostController.cs` | 1–92 | App Services owner | Match |
| `src/DropSpace.App/Services/OverlayWindowInterop.cs` | 1–740 | App Services owner | Match |
| `src/DropSpace.App/Services/OverlayWindowService.cs` | 1–1584 | App Services owner | Match |
| `src/DropSpace.App/Services/PinItemsUseCase.cs` | 1–35 | App Services owner | Match |
| `src/DropSpace.App/Services/QuickActionDialogService.cs` | 1–410 | App Services owner | Match |
| `src/DropSpace.App/Services/QuickPreviewService.cs` | 1–67 | App Services owner | Match |
| `src/DropSpace.App/Services/ReleaseBuildInfo.cs` | 1–20 | App Services owner | Match |
| `src/DropSpace.App/Services/ResourceStringLocalizer.cs` | 1–117 | App Services owner | Match |
| `src/DropSpace.App/Services/SecureInternetShareService.cs` | 1–141 | App Services owner | Match |
| `src/DropSpace.App/Services/SettingsApplicationCoordinator.cs` | 1–283 | App Services owner | Match |
| `src/DropSpace.App/Services/SettingsTransactionRollbackCoordinator.cs` | 1–26 | App Services owner | Match |
| `src/DropSpace.App/Services/SettingsUpdateException.cs` | 1–8 | App Services owner | Match |
| `src/DropSpace.App/Services/ShareFolderEnumeration.cs` | 1–40 | App Services owner | Match |
| `src/DropSpace.App/Services/ShareTargetActivationService.cs` | 1–251 | App Services owner | Match |
| `src/DropSpace.App/Services/SharingUseCase.cs` | 1–32 | App Services owner | Match |
| `src/DropSpace.App/Services/ShellActionService.cs` | 1–130 | App Services owner | Match |
| `src/DropSpace.App/Services/ShellIntakeActivationService.cs` | 1–137 | App Services owner | Match |
| `src/DropSpace.App/Services/StartupRegistrationService.cs` | 1–55 | App Services owner | Match |
| `src/DropSpace.App/Services/SystemActivityExperienceService.cs` | 1–103 | App Services owner | Match |
| `src/DropSpace.App/Services/SystemVisualPreferenceService.cs` | 1–216 | App Services owner | Match |
| `src/DropSpace.App/Services/ThumbnailService.cs` | 1–100 | App Services owner | Match |
| `src/DropSpace.App/Services/TrustedPublisherIdentityPolicy.cs` | 1–146 | App Services owner | Match |
| `src/DropSpace.App/Services/UndoCoordinator.cs` | 1–420 | App Services owner | Match |
| `src/DropSpace.App/Services/Volume/VolumeInterop.cs` | 1–59 | App Services owner | Match |
| `src/DropSpace.App/Services/Volume/WindowsVolumeActivityService.cs` | 1–176 | App Services owner | Match |
| `src/DropSpace.App/Services/Widgets/NativeWidgetDataService.cs` | 1–115 | App Services owner | Match |
| `src/DropSpace.App/Services/WindowsCompatibilityService.cs` | 1–416 | App Services owner | Match |
| `src/DropSpace.App/Services/WindowsImageCodecPreflight.cs` | 1–203 | App Services owner | Match |
| `src/DropSpace.App/Services/WindowsImageTransformService.cs` | 1–427 | App Services owner | Match |
| `src/DropSpace.App/Services/WindowsShareIntegrationService.cs` | 1–66 | App Services owner | Match |
| `src/DropSpace.App/Services/WorkspaceMutationUseCase.cs` | 1–29 | App Services owner | Match |
| `src/DropSpace.App/Services/XamlResourceOverride.cs` | 1–120 | App Services owner | Match |

Scope SHA-256: `8eab7128cd696807cc3f0039b0fb4957490b8f7ded408e0f5ffc127a06c0c1e1`. Manifest SHA-256: `5717765305d2e94d874c3bc0a0e95d2d1f5cf5626da3eaeaa0af59b2dc78b7ed`.

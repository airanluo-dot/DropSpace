# Beta 18 round 1 — complete App Services review

Source reviewed: immutable `/workspace/scratch/beta18-round1`, including the integrated independent island appearance and content-priority changes after PRs 107/108/109. The repository `AGENTS.md` and canonical `.agents/skills/dropspace-maintainer/SKILL.md` were read, with App/UI and architecture guidance. The immutable review did not edit production; subsequent authorized fixes are recorded below. No tests, builds, native smoke checks, or real-model execution were run.

This partition fully read every file in `scope/app-services-scope.txt`: **120 files / 26,999 lines**. The partition owner read 94 files / 18,336 lines; the media/audio child reviewer read 17 files / 3,289 lines and the OLE child reviewer read 9 files / 5,374 lines. Reads covered complete bodies, including diagnostic and interop files, rather than only changed hunks or search hits. Truncated read batches were followed by complete reads of omitted portions. Coverage is listed below with the immutable scope digest.

## Confirmed findings

### R1-SVC-01 — Expiration failure leaves Undo pointing to a disposed cancellation source

Priority: **P2**. Evidence: `src/DropSpace.App/Services/UndoCoordinator.cs:277–307,310–330,379–383`.

If the eight-second expiration reaches `FinalizePendingRemovalAsync` and that repository operation fails, `ExpireAsync` catches the failure, keeps `_active` for retry, and then disposes the timer's cancellation source at line 305. It never clears the matching `_expirationCancellation` field. A later expired Undo, a new removal, explicit finalization, or shutdown retries the active repository operation and calls `CancelExpiration`, which calls `Cancel()` on that disposed source and throws `ObjectDisposedException`. When the retry has already committed deletion at line 320, this exception also skips clearing `_active`, publishing the cleared state, and draining cleanup, leaving a phantom undo state until restart. The error path contradicts the promised retryable behavior.

Minimal fix: detach a matching timer cancellation source under the coordinator's ownership before disposing it, and make cancellation detach the field before attempting cancellation. A narrowly handled disposal race is appropriate if ownership is not otherwise serialized. Keep repository failures retryable and keep the transaction-success boundary clearing the active state.

### R1-MEDIA-01 — Retired provider callbacks can cancel the newer song's AI translation

Priority: **P2**. Evidence: `src/DropSpace.App/Services/Media/MediaExperienceService.cs:509–522,544–554`; `src/DropSpace.App/Services/Media/AiLyricsService.cs:227–243`.

The provider callback and final-source path check the media generation before entering their per-request source lock, then call the unfenced `ObserveSource`. Different songs have separate locks. A delayed A callback can therefore pass its check, resume after B has started, install A's admission globally and cancel B. `ObserveSource` passes an always-true predicate into the existing state-gated observer; later UI checks protect display but cannot undo the shared AI-state mutation. B loses its in-flight progress/final source fence without automatically restarting.

Minimal fix: expose/use the existing `TryObserveSource` form with the current media request predicate at both call sites, evaluated inside `AiLyricsService._stateGate` before mutating admission or selecting the cancellation target. Details and supporting callers are in [the full media/audio report](round1-app-services-media.md).

### R1-MEDIA-02 — Same-title recording changes do not wake dismissed music

Priority: **P2**. Evidence: `src/DropSpace.App/Services/Media/MediaExperienceService.cs:396–399`; supporting `src/DropSpace.Core/Island/IslandExperienceCoordinator.cs:63–69,89–96`; runtime key `src/DropSpace.Core/Media/MediaModels.cs:42–43,56–66`.

The island content identity contains only session ID, source application and track title. If a continuously playing player moves to a different artist/album recording with that title after the user dismisses the music island, Core sees neither a new identity nor an inactive-to-playing transition, retains dismissal and leaves the new track hidden. Other app track handling already recognizes that recording change.

Minimal fix: supply `session.TrackIdentity` to `UpdateMedia`, using the existing complete runtime identity. The Core review independently found this issue; the complete media source and dismissal policy confirm it.

### R1-OLE-01 — Mode changes can fail before stopping drag observers

Priority: **P2**. Evidence: `src/DropSpace.App/Services/DragSessionDetector.cs:413–428,1171–1177,1211–1217`.

`StopCoreAsync` calls `_completionGrace?.Cancel()` and `_sessionTimeout?.Cancel()` without guarding against disposal, while independent timer continuations clear and dispose those same sources. Stop can read a nonnull source, lose the race to its disposal and then throw from `Cancel()` before canceling the observer run or posting `WM_QUIT`. This can retain live Smart Drag native observers after the requested Disabled/Classic transition. The preceding candidate-timeout loop already handles this disposal race, but these two fields do not.

Minimal fix: use a safe cancellation helper catching only `ObjectDisposedException`, including the other direct grace/timeout calls at lines 977–978, 1042–1043 and 1139, or synchronize field detach/cancel/dispose ownership. See [the full OLE report](round1-app-services-ole.md) for supporting lifetime paths.

### R1-OLE-02 — A second accepted virtual-file drop cancels the first import

Priority: **P2**. Evidence: `src/DropSpace.App/Services/OleDragDropService.cs:1030–1038`; `src/DropSpace.App/Services/Ole/VirtualFileMaterializer.cs:78–108,133–170`.

A virtual-file drop is acknowledged with `DROPEFFECT_COPY` while materialization continues asynchronously, either after yielding for an asynchronous OLE provider or copying already-owned media on a background worker. When a second virtual-file drop arrives at the same target before that work finishes, `Drop` unconditionally cancels and disposes the prior `_dropCancellation` before allocating the new one. The first accepted operation consequently rolls its staging back and never reaches the owned-item ingestion path, although the first drag source already saw a successful copy. Merely starting the next drag should retire visual completion state, not an import already accepted by OLE.

Minimal fix: retain independently owned accepted-drop cancellation/completion lifetimes under bounded capacity, or reject a later drop with `DROPEFFECT_NONE` while the existing import is active. Shutdown/target disposal must drain or cancel the retained accepted owners. See [the full OLE report](round1-app-services-ole.md).

### R1-SVC-02 — NetEase reinstallation retains every prior deployment backup

Priority: **P3**. Evidence: `src/DropSpace.App/Services/NeteaseEnhancement/InfLinkDeploymentService.cs:118–142,162–172,175–195,198–215`; restoration references `InfLinkDeploymentService.IO.cs:176–193`.

Every install allocates a new `backups/<transaction>` directory. Reinstallation copies the prior owned loader and plugin into it and writes `previous.json`. A successful commit only marks the receipt committed; rollback and removal likewise never retire these directories. Repeating the explicit reinstall action therefore accumulates full binary backup copies indefinitely, and removing the enhancement leaves all historical copies behind. The prepared-download directory has an explicit cap; the backup inventory has no analogous bound or retirement path.

Minimal fix: add confined, fixed-child backup retirement after successful commit/rollback/removal when a transaction is no longer referenced by a pending rollback chain. Preserve every backup needed by an active or failed transaction, and retain cleanup failures as retryable maintenance. A bounded session/startup sweep can remove orphan transaction directories only after receipt reachability is established.

## Reviewed behavior and limits

The independent `IslandAppearance.Theme` reaches existing and rebuilt monitor windows and the Acrylic controller uses the root's actual theme. Solid fallback colors remain ThemeResource-owned. No additional appearance/content-priority regression was sufficiently evidenced in this partition. The full App/UI and Core reviewers own the views and selection policy respectively.

The review covered clipboard read pools, native cancellation/close ownership, cross-device propagation gates, settings rollback, DLC inventory/download lifetimes, media/lyrics/model publication, drag/virtual-file ownership, overlay regions/backdrops/glow/motion, display rebuilds, notification/volume/widget sampling, shell/share intake, update verification and tray/shutdown paths. It is source evidence, not native Windows performance, COM/apartment behavior, real GPU/model correctness, mixed-DPI or visual qualification.

The synchronous cache-policy gate concern was traced to `SettingsApplicationCoordinator.ApplyCachePolicy` and handed to the Infrastructure reviewer that owns `LyricsCache`; its disposition belongs in that report. A suspected per-frame foreground process probe was rejected: playback frame publication does not itself call the island coordinator's state publication, and unchanged snapshots suppress `Changed` events. The separate 500 ms foreground polling and 250 ms preference capability probing remain performance observations without a demonstrated user-facing defect in this no-test pass.

## Authorized production fix notes

All six confirmed findings received minimal fixes in `/workspace/DropSpace`; the immutable round 1 snapshot and its coverage counts remain unchanged. The original findings above retain snapshot line numbers. The following references describe the resulting production source.

- **R1-SVC-01:** `UndoCoordinator.cs:303–309,382–387` atomically detaches the matching expiration source before the task disposes it. Explicit cancellation atomically takes the current source before calling `Cancel`, narrowly handles an already-completed source, and leaves repository failure/retry behavior intact. An old task cannot clear a newly installed timer. No new lock or synchronous wait was added to the expiration-finally path.
- **R1-MEDIA-01:** `AiLyricsService.cs:229–243` exposes its existing fenced observer internally; both provider-source paths pass the originating request predicate (`MediaExperienceService.cs:520–522,553–556`). The predicate is evaluated under `_stateGate` before AI admission or cancellation-target mutation. Rejected work returns before replacing the observed source. See the media report's fix notes.
- **R1-MEDIA-02:** `MediaExperienceService.cs:396–399` supplies `session.TrackIdentity`, reusing the full recording identity already used by runtime track changes.
- **R1-OLE-01:** `DragSessionDetector.cs:426–427,977–978,1042–1043,1139,1213–1220` routes every independently retired grace/timeout cancellation through a helper that catches only `ObjectDisposedException`. Timer-finally paths atomically clear only their own field (`1172,1208`) before disposal; the existing shutdown still cancels the run, posts native retirement and awaits scheduled work. No accepted work is disposed before it drains.
- **R1-OLE-02:** `OleDragDropService.cs:923–935,986–988,1020–1026,1040–1059,1176–1178,1189–1245` retains one accepted virtual-import owner through materialization, shared intake and lease cleanup. Later gestures at that native target return `DROPEFFECT_NONE` without advancing its generation or triggering visual cleanup; this bounded admission also rejects ordinary file drops there until completion. Completion atomically clears/disposes its own source; explicit registration disposal safely requests cancellation. This is a documented retry-after-completion policy, with no new queue. See the OLE report's fix notes.
- **R1-SVC-02:** `InfLinkDeploymentService.Packages.cs:23–103` adds confined backup retirement, invoked under the deployment gate before a new transaction and after successful publication, restored rollback, or removal (`InfLinkDeploymentService.cs:115,157,173,197,218`). The pass validates at most 256 canonical receipt files and preserves each current transaction; pending transactions also preserve their immediate committed predecessor, which is the only predecessor `InstallAsync` admits. It processes at most 256 historical directories plus retained-directory allowance. Only unreferenced GUID directories containing the fixed owned files `0.bak`, `1.bak`, and `previous.json` are eligible; all children are checked before deletion, and recursive deletion is avoided. Invalid, oversized or ambiguous receipt metadata aborts maintenance without undoing the deployment result. Unknown entries, reparse paths and cleanup failures are retained for a later pass. A normal managed installation retains one committed backup generation, temporarily two while its replacement is pending.

Source-only verification reviewed each changed lifetime/state boundary and the NetEase pending-recovery callers and existing rollback contract. The Infrastructure reviewer independently checked the retirement diff and predecessor/receipt flow and found no blocking reachability issue. The bounded pass can defer older cleanup behind many unknown or inaccessible entries; safety takes precedence over pruning that uncertain inventory. Scoped `git diff --check` passed for the changed service files, with existing CRLF conventions preserved. No tests, builds, Windows execution, remote operation, or commit occurred. Executable regression evidence and Windows OLE/provider behavior remain outstanding; these notes do not claim runtime acceptance.

## Full file coverage

Scope list SHA-256: `8eab7128cd696807cc3f0039b0fb4957490b8f7ded408e0f5ffc127a06c0c1e1`. Every line count is from the immutable source snapshot.

| File under `src/DropSpace.App/Services` | Lines | Full-read owner |
| --- | ---: | --- |
| `AcrylicCoverageBackdropTarget.cs` | 147 | Partition owner |
| `AcrylicCoverageMask.cs` | 254 | Partition owner |
| `AcrylicShutterBlur.cs` | 212 | Partition owner |
| `AppLanguageService.cs` | 49 | Partition owner |
| `Audio/ProcessLoopbackInterop.cs` | 65 | Media/audio child |
| `Audio/WindowsProcessLoopbackService.cs` | 207 | Media/audio child |
| `AuthenticodeTrustedUpdateVerifier.cs` | 123 | Partition owner |
| `BackdropLifetimeSmoke.cs` | 60 | Partition owner |
| `ClipboardAccessPolicy.cs` | 44 | Partition owner |
| `ClipboardCaptureDiagnostics.cs` | 267 | Partition owner |
| `ClipboardCaptureService.cs` | 1928 | Partition owner |
| `ClipboardIntegrationSmoke.cs` | 424 | Partition owner |
| `ClipboardNotificationService.cs` | 179 | Partition owner |
| `ClipboardProviderReadLifetime.cs` | 51 | Partition owner |
| `CompositionEffectDescriptions.cs` | 344 | Partition owner |
| `CrashDiagnosticsService.cs` | 82 | Partition owner |
| `CrossDeviceClipboardService.cs` | 290 | Partition owner |
| `DeploymentModeService.cs` | 52 | Partition owner |
| `DeviceHandoffService.cs` | 308 | Partition owner |
| `DeviceHandoffUseCase.cs` | 54 | Partition owner |
| `Diagnostics/LyricsLanguageSmoke.cs` | 66 | Partition owner |
| `Diagnostics/MusicVisualSmoke.cs` | 717 | Partition owner |
| `Diagnostics/MusicVisualSmokeOptions.cs` | 56 | Partition owner |
| `DispatcherQueueExtensions.cs` | 55 | Partition owner |
| `DisplayIdentityService.cs` | 251 | Partition owner |
| `DisplayTopologyWatcher.cs` | 164 | Partition owner |
| `Dlc/AiModelDlcProvider.cs` | 41 | Partition owner |
| `Dlc/CudaRuntimeDlcProvider.cs` | 49 | Partition owner |
| `Dlc/DlcManagerService.cs` | 312 | Partition owner |
| `Dlc/DlcProgressPresentation.cs` | 20 | Partition owner |
| `Dlc/NeteaseComponentsDlcProvider.cs` | 111 | Partition owner |
| `DragSessionDetector.cs` | 1933 | OLE child |
| `DragStorageItemService.cs` | 44 | Partition owner |
| `ForegroundWindowMonitor.cs` | 95 | Partition owner |
| `GlobalQuickPanelHotkeyService.cs` | 361 | Partition owner |
| `ImageDecoderPreflight.cs` | 24 | Partition owner |
| `InnoUpdateInstallerLauncher.cs` | 26 | Partition owner |
| `IslandAcrylicBackdrop.cs` | 143 | Partition owner |
| `IslandGlowController.cs` | 208 | Partition owner |
| `IslandGlowRasterizer.cs` | 295 | Partition owner |
| `IslandGlowWindow.cs` | 175 | Partition owner |
| `IslandMotionBlurPolicy.cs` | 159 | Partition owner |
| `IslandTransparentBackdrop.cs` | 42 | Partition owner |
| `ItemProjectionService.cs` | 39 | Partition owner |
| `ItemSharingService.cs` | 190 | Partition owner |
| `MaintenanceShutdownService.cs` | 110 | Partition owner |
| `Media/AiLyricsService.cs` | 525 | Media/audio child |
| `Media/BoundedMediaOperation.cs` | 93 | Media/audio child |
| `Media/LyricsRapidSkipDiagnostic.cs` | 70 | Media/audio child |
| `Media/MediaApplicationIconService.cs` | 48 | Media/audio child |
| `Media/MediaArtworkService.cs` | 38 | Media/audio child |
| `Media/MediaEventSubscription.cs` | 49 | Media/audio child |
| `Media/MediaExperienceService.cs` | 775 | Media/audio child |
| `Media/MediaLyricsRefreshRequest.cs` | 19 | Media/audio child |
| `Media/MediaProcessResolver.cs` | 61 | Media/audio child |
| `Media/MediaSessionOwner.cs` | 53 | Media/audio child |
| `Media/MediaSoftRestartOperation.cs` | 67 | Media/audio child |
| `Media/MediaSubscriptionAdmission.cs` | 12 | Media/audio child |
| `Media/QqMusicLoginService.cs` | 198 | Media/audio child |
| `Media/RetirableMediaWork.cs` | 127 | Media/audio child |
| `Media/WindowsMediaSessionService.cs` | 882 | Media/audio child |
| `MonitorLayoutService.cs` | 294 | Partition owner |
| `NativeApplicationIcon.cs` | 80 | Partition owner |
| `NativeAsyncLifetime.cs` | 44 | Partition owner |
| `NativeFolderPickerService.cs` | 34 | Partition owner |
| `NativeSubscriberNotification.cs` | 32 | Partition owner |
| `NativeTrayService.cs` | 349 | Partition owner |
| `NeteaseEnhancement/BetterNcmProbe.cs` | 31 | Partition owner |
| `NeteaseEnhancement/InfLinkDeploymentService.IO.cs` | 278 | Partition owner |
| `NeteaseEnhancement/InfLinkDeploymentService.Packages.cs` | 88 | Partition owner |
| `NeteaseEnhancement/InfLinkDeploymentService.cs` | 219 | Partition owner |
| `NeteaseEnhancement/NeteaseEnhancementService.cs` | 236 | Partition owner |
| `NeteaseEnhancement/NeteaseInstallationProbe.cs` | 90 | Partition owner |
| `NeteaseEnhancement/NeteaseRuntimeInstaller.cs` | 79 | Partition owner |
| `NeteaseEnhancement/NeteaseSmtcVerifier.cs` | 608 | Partition owner |
| `Notifications/WindowsNotificationActivityService.cs` | 136 | Partition owner |
| `Ole/EphemeralOleDragProbe.cs` | 810 | OLE child |
| `Ole/OleDropTargetNative.cs` | 32 | OLE child |
| `Ole/OleFileDataClassifier.cs` | 418 | OLE child |
| `Ole/QueryOnlyDataObject.cs` | 74 | OLE child |
| `Ole/SmartDragProbeOptions.cs` | 43 | OLE child |
| `Ole/SmartDragRuntimePolicy.cs` | 41 | OLE child |
| `Ole/VirtualFileMaterializer.cs` | 670 | OLE child |
| `OleDragDropService.cs` | 1353 | OLE child |
| `OverlayCompositionAnimator.cs` | 110 | Partition owner |
| `OverlayMaterialController.cs` | 118 | Partition owner |
| `OverlayMotionOrchestrator.cs` | 95 | Partition owner |
| `OverlayNativeRegionController.cs` | 80 | Partition owner |
| `OverlayTransparentHostController.cs` | 92 | Partition owner |
| `OverlayWindowInterop.cs` | 740 | Partition owner |
| `OverlayWindowService.cs` | 1603 | Partition owner |
| `PinItemsUseCase.cs` | 35 | Partition owner |
| `QuickActionDialogService.cs` | 410 | Partition owner |
| `QuickPreviewService.cs` | 67 | Partition owner |
| `ReleaseBuildInfo.cs` | 20 | Partition owner |
| `ResourceStringLocalizer.cs` | 117 | Partition owner |
| `SecureInternetShareService.cs` | 141 | Partition owner |
| `SettingsApplicationCoordinator.cs` | 273 | Partition owner |
| `SettingsTransactionRollbackCoordinator.cs` | 26 | Partition owner |
| `SettingsUpdateException.cs` | 8 | Partition owner |
| `ShareFolderEnumeration.cs` | 40 | Partition owner |
| `ShareTargetActivationService.cs` | 251 | Partition owner |
| `SharingUseCase.cs` | 32 | Partition owner |
| `ShellActionService.cs` | 130 | Partition owner |
| `ShellIntakeActivationService.cs` | 137 | Partition owner |
| `StartupRegistrationService.cs` | 55 | Partition owner |
| `SystemActivityExperienceService.cs` | 103 | Partition owner |
| `SystemVisualPreferenceService.cs` | 216 | Partition owner |
| `ThumbnailService.cs` | 100 | Partition owner |
| `TrustedPublisherIdentityPolicy.cs` | 146 | Partition owner |
| `UndoCoordinator.cs` | 416 | Partition owner |
| `Volume/VolumeInterop.cs` | 59 | Partition owner |
| `Volume/WindowsVolumeActivityService.cs` | 156 | Partition owner |
| `Widgets/NativeWidgetDataService.cs` | 115 | Partition owner |
| `WindowsCompatibilityService.cs` | 416 | Partition owner |
| `WindowsImageCodecPreflight.cs` | 203 | Partition owner |
| `WindowsImageTransformService.cs` | 392 | Partition owner |
| `WindowsShareIntegrationService.cs` | 66 | Partition owner |
| `WorkspaceMutationUseCase.cs` | 29 | Partition owner |
| `XamlResourceOverride.cs` | 120 | Partition owner |
| **Total** | **26,999** | **120 complete files** |

# Round 2 App independent static review (source review complete; runtime validation pending)

Baseline: local `316f05ec1325384241083e903924cf22e7398bcf`, tree `248321174a405d823a7dcb2a0e40f231c02a8514`. Initial worktree clean. Root agent owns source fixes; this reviewer changes only this report.

This report is not a round-pass certification. Native Windows runtime, Narrator, multimonitor/DPI, real clipboard COM and installation behavior have not been exercised by this reviewer. CI is separately running. Known whole-song model semantics belong to `round1-full-song-semantic-review.md` and are not new findings here.


## Final static result

Eight actionable baseline issues were sent to the root reviewer immediately as they became certain. All eight now have corresponding source repairs independently reviewed in the working tree. No additional unaddressed confirmed issue remains in this scoped static pass. This does not establish that round 2 passed: new-head full App compilation/tests and the native integration/accessibility matrix remain pending, as does the separately tracked AI model semantic block.

Final review checkpoint: 2026-10-01 21:55 UTC. Root reported successful limited Clipboard and AI card/service projection builds (zero warnings/errors); those results do not cover all App/XAML and were not independently executed here. This report's own executed checks are only the offline inventory/XML/JSON/resource consistency checks described below.

## Finding A2-APP-01 (P2): saturated retired text readers block the entire capture worker

Baseline `ClipboardCaptureService.cs:1134` awaited the eight-slot text semaphore without a timeout or supersession lease. Eight obsolete reads retained by native completion/cancel/close can exhaust it. The ninth text signal then blocks the sole signal worker before registering a lease; further image/file notifications cannot supersede this wait. Existing `ClipboardTextSupersessionTests.cs:181–216` verifies native-slot ownership but not continued cross-format progress.

Root changed text admission to zero-wait `ClipboardProviderReadLifetime.TryReserveSlotAsync`, returning a rejected text-capacity snapshot. Both text and non-text paths use the same helper. Independent diff review confirms the fix preserves retirement ownership and avoids the worker stall. Added helper test verifies the ninth admission returns false immediately, independent other-format capacity remains usable, and text can resume after release. This is helper-level evidence, not a complete Windows event-flow test. Runtime test result remains pending.

## Actual full-file semantic reading completed

- `src/DropSpace.App/App.xaml.cs`
- `src/DropSpace.App/MainWindow.xaml.cs`
- `src/DropSpace.App/ViewModels/MainViewModel.cs`
- `src/DropSpace.App/ViewModels/NativeSettingsEditor.cs`
- `src/DropSpace.App/Services/MaintenanceShutdownService.cs`
- `src/DropSpace.App/Services/DispatcherQueueExtensions.cs`
- `src/DropSpace.App/Views/ContentDialogLifetime.cs`
- `src/DropSpace.App/Services/ClipboardCaptureService.cs`
- `src/DropSpace.App/Services/ClipboardProviderReadLifetime.cs`
- `src/DropSpace.App/Services/ClipboardAccessPolicy.cs`
- `src/DropSpace.App/Services/ShellIntakeActivationService.cs`
- `src/DropSpace.App/Services/ShareTargetActivationService.cs`
- `src/DropSpace.App/Services/SettingsApplicationCoordinator.cs`
- `src/DropSpace.App/Services/SettingsTransactionRollbackCoordinator.cs`
- `src/DropSpace.App/Services/NativeSubscriberNotification.cs`
- `src/DropSpace.App/Services/ItemProjectionService.cs`
- `src/DropSpace.App/Services/WorkspaceMutationUseCase.cs`
- `src/DropSpace.App/Services/PinItemsUseCase.cs`
- `src/DropSpace.App/Services/UndoCoordinator.cs`
- `src/DropSpace.App/Services/ClipboardNotificationService.cs`
- `src/DropSpace.App/Services/ClipboardCaptureDiagnostics.cs`
- `src/DropSpace.App/Services/Media/AiLyricsService.cs`
- `src/DropSpace.App/Services/Media/MediaExperienceService.cs`
- `tests/DropSpace.App.Tests/FullAuditClipboardShutdownRegressionTests.cs`
- `tests/DropSpace.App.Tests/ClipboardOverflowAccountingTests.cs`
- `tests/DropSpace.App.Tests/ClipboardTextSupersessionTests.cs`
- `tests/DropSpace.App.Tests/ClipboardProviderReadLifetimeTests.cs`
- `tests/DropSpace.App.Tests/ClipboardStateRaceTests.cs`
- `tests/DropSpace.App.Tests/ClipboardDeferredProviderNativeTests.cs`
- `tests/DropSpace.App.Tests/DropSpace.App.Tests.csproj`
- `tests/DropSpace.App.Tests/DispatcherCallbackShutdownRegressionTests.cs`
- `tests/DropSpace.App.Tests/ContentDialogLifetimeTests.cs`
- `tests/DropSpace.App.Tests/FullAuditUndoRegressionTests.cs`
- `tests/DropSpace.App.Tests/UndoLifecycleTests.cs`

- `src/DropSpace.App/Services/Media/WindowsMediaSessionService.cs`
- `src/DropSpace.App/ViewModels/MediaViewModel.cs`
- `src/DropSpace.App/Views/Music/MusicPage.cs`
- `src/DropSpace.App/Views/Music/AiLyricsSettingsCard.cs`
- `src/DropSpace.App/Views/Music/LyricsFontSizeControl.cs`
- `src/DropSpace.App/Views/Music/LyricsFontSizeEditSession.cs`
- `src/DropSpace.App/Views/Music/LyricsGlowModeControl.cs`
- `src/DropSpace.App/Services/Media/MediaArtworkService.cs`
- `src/DropSpace.App/Services/Media/MediaApplicationIconService.cs`
- `src/DropSpace.App/Services/Media/MediaProcessResolver.cs`
- `src/DropSpace.App/Services/Audio/ProcessLoopbackInterop.cs`
- `src/DropSpace.App/Services/Audio/WindowsProcessLoopbackService.cs`
- `src/DropSpace.App/Services/IslandGlowController.cs`
- `src/DropSpace.App/Services/IslandGlowRasterizer.cs`
- `src/DropSpace.App/Services/IslandGlowWindow.cs`
- `tests/DropSpace.App.Tests/IslandGlowRasterizerTests.cs`
- `tests/DropSpace.App.Tests/IslandGlowNativeWindowTests.cs`
- `tests/DropSpace.App.Tests/MediaRegressionTests.cs`
- `tests/DropSpace.App.Tests/ProcessLoopbackNativeSmokeTests.cs`

- `src/DropSpace.App/Services/NeteaseEnhancement/NeteaseEnhancementService.cs`
- `src/DropSpace.App/Services/NeteaseEnhancement/NeteaseRuntimeInstaller.cs`
- `src/DropSpace.App/Services/NeteaseEnhancement/InfLinkDeploymentService.IO.cs`
- `src/DropSpace.App/Services/NeteaseEnhancement/NeteaseInstallationProbe.cs`
- `src/DropSpace.App/Services/NeteaseEnhancement/BetterNcmProbe.cs`
- `src/DropSpace.App/Services/NeteaseEnhancement/NeteaseSmtcVerifier.cs`
- `src/DropSpace.App/Services/NeteaseEnhancement/InfLinkDeploymentService.cs`
- `src/DropSpace.App/Services/NeteaseEnhancement/InfLinkDeploymentService.Packages.cs`
- `src/DropSpace.App/ViewModels/NeteaseEnhancementViewModel.cs`
- `src/DropSpace.App/Views/Music/NeteaseEnhancementCard.cs`
- `tests/DropSpace.App.Tests/NeteaseSmtcVerifierTests.cs`
- `tests/DropSpace.App.Tests/NeteaseRuntimePolicyTests.cs`
- `tests/DropSpace.App.Tests/FullAuditNeteaseProbeRegressionTests.cs`
- `tests/DropSpace.App.Tests/NeteaseFileMutationTests.cs`
- `tests/DropSpace.App.Tests/NeteaseProcessRaceTests.cs`
- `tests/DropSpace.App.Tests/NeteaseDeploymentTests.cs`
- `tests/DropSpace.App.Tests/MediaSessionSelectionTests.cs`
- `src/DropSpace.App/Views/Island/MediaExpandedView.xaml.cs`
- `src/DropSpace.App/Views/Island/MediaCompactView.xaml.cs`
- `src/DropSpace.App/Services/Notifications/WindowsNotificationActivityService.cs`
- `src/DropSpace.App/Services/Volume/VolumeInterop.cs`
- `src/DropSpace.App/Services/Volume/WindowsVolumeActivityService.cs`
- `src/DropSpace.App/Services/SystemActivityExperienceService.cs`
- `src/DropSpace.App/ViewModels/SystemActivityViewModel.cs`

- `src/DropSpace.App/Services/CrashDiagnosticsService.cs`
- `src/DropSpace.App/Services/StartupRegistrationService.cs`
- `src/DropSpace.App/Services/ShellActionService.cs`
- `src/DropSpace.App/Services/ThumbnailService.cs`
- `src/DropSpace.App/Services/DragStorageItemService.cs`
- `src/DropSpace.App/Services/QuickPreviewService.cs`
- `src/DropSpace.App/Services/ImageDecoderPreflight.cs`
- `src/DropSpace.App/Services/ReleaseBuildInfo.cs`
- `src/DropSpace.App/Services/SettingsUpdateException.cs`
- `src/DropSpace.App/Services/DeviceHandoffService.cs`
- `src/DropSpace.App/Services/CrossDeviceClipboardService.cs`
- `src/DropSpace.App/Services/SecureInternetShareService.cs`
- `src/DropSpace.App/Services/ItemSharingService.cs`
- `src/DropSpace.App/Services/SharingUseCase.cs`
- `src/DropSpace.App/Services/DeviceHandoffUseCase.cs`
- `src/DropSpace.App/Services/WindowsShareIntegrationService.cs`
- `src/DropSpace.App/Services/DeploymentModeService.cs`
- `src/DropSpace.App/Services/InnoUpdateInstallerLauncher.cs`

- `src/DropSpace.App/Services/AuthenticodeTrustedUpdateVerifier.cs`
- `src/DropSpace.App/Services/TrustedPublisherIdentityPolicy.cs`
- `src/DropSpace.App/Services/NativeApplicationIcon.cs`
- `src/DropSpace.App/Services/AppLanguageService.cs`
- `src/DropSpace.App/Services/ResourceStringLocalizer.cs`
- `src/DropSpace.App/Services/XamlResourceOverride.cs`
- `src/DropSpace.App/Services/OverlayWindowService.cs`
- `src/DropSpace.App/ViewModels/OverlayViewModel.cs`
- `src/DropSpace.App/OverlayWindow.xaml.cs`
- `src/DropSpace.App/Services/OleDragDropService.cs`
- `src/DropSpace.App/Services/Ole/VirtualFileMaterializer.cs`
- `src/DropSpace.App/Services/Ole/OleDropTargetNative.cs`
- `src/DropSpace.App/Services/Ole/SmartDragRuntimePolicy.cs`
- `src/DropSpace.App/Services/Ole/SmartDragProbeOptions.cs`
- `src/DropSpace.App/Services/Ole/QueryOnlyDataObject.cs`
- `src/DropSpace.App/Services/Ole/EphemeralOleDragProbe.cs`
- `src/DropSpace.App/Services/Ole/OleFileDataClassifier.cs`
- `src/DropSpace.App/Services/DragSessionDetector.cs`
- `src/DropSpace.App/Services/OverlayNativeRegionController.cs`
- `src/DropSpace.App/Services/OverlayMotionOrchestrator.cs`
- `src/DropSpace.App/Services/OverlayCompositionAnimator.cs`
- `src/DropSpace.App/Services/OverlayMaterialController.cs`
- `src/DropSpace.App/Services/OverlayWindowInterop.cs`
- `src/DropSpace.App/Services/IslandAcrylicBackdrop.cs`
- `src/DropSpace.App/Services/DisplayIdentityService.cs`
- `src/DropSpace.App/Services/DisplayTopologyWatcher.cs`
- `src/DropSpace.App/Services/ForegroundWindowMonitor.cs`
- `src/DropSpace.App/Services/MonitorLayoutService.cs`
- `src/DropSpace.App/Services/SystemVisualPreferenceService.cs`
- `src/DropSpace.App/Services/WindowsCompatibilityService.cs`
- `src/DropSpace.App/Services/GlobalQuickPanelHotkeyService.cs`
- `src/DropSpace.App/Services/NativeTrayService.cs`
- `src/DropSpace.App/Services/WindowsImageTransformService.cs`
- `src/DropSpace.App/Services/WindowsImageCodecPreflight.cs`
- `src/DropSpace.App/Services/Widgets/NativeWidgetDataService.cs`
- `src/DropSpace.App/Services/NativeFolderPickerService.cs`
- `src/DropSpace.App/ViewModels/WidgetViewModel.cs`
- `src/DropSpace.App/ViewModels/QuickActionButtonViewModel.cs`
- `src/DropSpace.App/ViewModels/ClipboardIslandViewModel.cs`
- `src/DropSpace.App/ViewModels/ItemCardViewModel.cs`
- `src/DropSpace.App/Services/BackdropLifetimeSmoke.cs`
- `src/DropSpace.App/Services/QuickActionDialogService.cs`
- `src/DropSpace.App/Services/ClipboardIntegrationSmoke.cs`
- `src/DropSpace.App/Views/MainPage.xaml.cs`
- `src/DropSpace.App/Views/MainPage.Settings.cs`
- `src/DropSpace.App/Views/Settings/SettingsForm.cs`
- `src/DropSpace.App/Views/Settings/WidgetEditorView.cs`
- `src/DropSpace.App/Views/Island/ClipboardIslandView.xaml.cs`
- `src/DropSpace.App/Views/Island/WidgetsExpandedView.xaml.cs`
- `src/DropSpace.App/Converters/BoolToVisibilityConverter.cs`
- `src/DropSpace.App/Converters/NullToVisibilityConverter.cs`
- `src/DropSpace.App/App.xaml`
- `src/DropSpace.App/MainWindow.xaml`
- `src/DropSpace.App/Properties/AssemblyInfo.cs`
- `src/DropSpace.App/Properties/launchSettings.json`
- `src/DropSpace.App/app.manifest`
- `src/DropSpace.App/Package.appxmanifest`
- `src/DropSpace.App/DropSpace.rc`
- `src/DropSpace.App/DropSpace.App.csproj`
- `src/DropSpace.App/Services/ShareFolderEnumeration.cs`
- `tests/DropSpace.App.Tests/ShareFolderEnumerationTests.cs`
- `tests/DropSpace.App.Tests/PlacementObserverLifetimeTests.cs`
- `tests/DropSpace.App.Tests/QuickActionCancellationTests.cs`
- `tests/DropSpace.App.Tests/OleCallbackBoundaryTests.cs`

- `src/DropSpace.App/OverlayWindow.xaml`
- `src/DropSpace.App/Strings/en-US/Resources.resw`
- `src/DropSpace.App/Strings/zh-CN/Resources.resw`
- `src/DropSpace.App/Views/DesignTokens.xaml`
- `src/DropSpace.App/Views/Island/ClipboardIslandView.xaml`
- `src/DropSpace.App/Views/Island/MediaCompactView.xaml`
- `src/DropSpace.App/Views/Island/MediaExpandedView.xaml`
- `src/DropSpace.App/Views/Island/WidgetsExpandedView.xaml`
- `src/DropSpace.App/Views/MainPage.xaml`
- `src/DropSpace.App/packages.lock.json`
- `tests/DropSpace.App.Tests/ClipboardDiagnosticsTests.cs`
- `tests/DropSpace.App.Tests/CrossDevicePeerSettingsRegressionTests.cs`
- `tests/DropSpace.App.Tests/FullAuditBatchProjectionRegressionTests.cs`
- `tests/DropSpace.App.Tests/FullAuditNativeBoundaryRegressionTests.cs`
- `tests/DropSpace.App.Tests/FullAuditOverlayOwnershipRegressionTests.cs`
- `tests/DropSpace.App.Tests/FullAuditShareBitmapRegressionTests.cs`
- `tests/DropSpace.App.Tests/FullAuditVirtualDescriptorRegressionTests.cs`
- `tests/DropSpace.App.Tests/LyricsFontSizeEditSessionTests.cs`
- `tests/DropSpace.App.Tests/MediaSeekInteractionTests.cs`
- `tests/DropSpace.App.Tests/MediaSessionNativeSmokeTests.cs`
- `tests/DropSpace.App.Tests/MonitorTopologyRegressionTests.cs`
- `tests/DropSpace.App.Tests/NativeLifecycleRegressionTests.cs`
- `tests/DropSpace.App.Tests/NotificationNativeSmokeTests.cs`
- `tests/DropSpace.App.Tests/OleFileDataClassifierWindowsTests.cs`
- `tests/DropSpace.App.Tests/OverlayRegionNativeSmokeTests.cs`
- `tests/DropSpace.App.Tests/OverlayRetirementRegressionTests.cs`
- `tests/DropSpace.App.Tests/Preview16ImageDecoderTests.cs`
- `tests/DropSpace.App.Tests/Preview16LifecycleTests.cs`
- `tests/DropSpace.App.Tests/Preview16OleLifetimeTests.cs`
- `tests/DropSpace.App.Tests/ProbeReentrancyRegressionTests.cs`
- `tests/DropSpace.App.Tests/RemoteClipboardImageFormatTests.cs`
- `tests/DropSpace.App.Tests/SettingsLastCheckRegressionTests.cs`
- `tests/DropSpace.App.Tests/ShellAcknowledgementLifecycleRegressionTests.cs`
- `tests/DropSpace.App.Tests/SmartDragProbeOptionsTests.cs`
- `tests/DropSpace.App.Tests/TrayResourceBoundaryTests.cs`
- `tests/DropSpace.App.Tests/TrustedPublisherIdentityPolicyTests.cs`
- `tests/DropSpace.App.Tests/VirtualFileApartmentCleanupTests.cs`
- `tests/DropSpace.App.Tests/VolumeNativeSmokeTests.cs`
- `tests/DropSpace.App.Tests/WidgetDataNativeSmokeTests.cs`
- `tests/DropSpace.App.Tests/WindowsImageCodecPreflightTests.cs`
- `tests/DropSpace.App.Tests/packages.lock.json`

## Partial files (not counted as complete)

None currently

## Coverage and verification boundary

Full baseline reading is complete: all 143 current production text/config/resource files (126 C#) and 57 test text/config files (55 C#), plus 43 binary assets inventoried separately. Counts include the newly added share-folder helper and the three new test files for traversal, placement-stop lifetime and action cancellation. Generated `bin`/`obj` are excluded. The explicit ledger above records actual complete source reads, not a keyword-scan substitute. Both resource files were read in full as paired key/value entries (680 entries each); source XML wrappers were also inspected.

Offline checks actually executed: 14 production XML/XAML/RESW/manifest/project files parsed, both lock files parsed as JSON, the two resource key sets match with zero duplicates, and all composite-format placeholders match. Local `dotnet` was not available, so this reviewer ran no C# tests or native app. Root/CI results must be recorded separately.

Review also traced source/test call chains for startup privacy decisions, activation queueing, clipboard self-write/import/retirement, settings rollback, AI work/model ownership, SMTC selection, loopback/audio, NetEase transactional deployment, window/HRGN/backdrop retirement, OLE materialization, projection and UI handlers. Reading test source establishes intended coverage only; it is not a test-pass result.

## Finding A2-APP-02 (P2): enhancement consent does not retire with its UI owner

Baseline `Views/Music/NeteaseEnhancementCard.cs:84` passes CancellationToken.None to the installation/reinstallation dialog, and :62 only removes a subscription on unload. A retired page can retain a pending approval continuation which calls EnhanceAsync at :85 and refreshes detached UI at :88. Bind a per-loaded-generation token and recheck the generation after the result; cancel only unapproved UI ownership, preserve already-approved deployment ownership. Root patch reviewed: per-loaded-generation lifetime cancels pending confirmation; generation is rechecked before installation and UI publication. Already approved deployment intentionally survives navigation. Windows dialog/navigation execution remains unverified.

## Finding A2-APP-03 (P2): paused rejected seek has no driver for pending deadline

Baseline `Views/Island/MediaExpandedView.xaml.cs:181,190,193–198,265–272` keeps a two-second pending target and starts a command without observing success. The error notification can render before expiry, so the pending position remains. `Services/Media/MediaExperienceService.cs:299–303` stops periodic frames when paused; no acknowledgement-deadline timer triggers a later render. With a rejected seek and no further media events, the slider/labels can indefinitely show the rejected target. Add a single-shot pending timeout timer, canceled on unload/track change, or clear pending on an observed failed command. Root patch reviewed: single-shot two-second acknowledgement timer rejects the held target and renders even when paused, and is stopped on new interactions and cancellation/unload. Existing seek tests exercise interaction policy; actual dispatcher timer/paused-player behavior remains unverified.

## Finding A2-APP-04 (P2): unbounded synchronous empty-directory share enumeration

`Services/ItemSharingService.cs:121–147` limits files added to a share, but does not cap visited directories, entries, queue length or depth. Before the first nonempty file hash awaits, traversing an empty directory tree runs synchronously on the UI-originated async call chain. A large empty/wide tree can monopolize the dispatcher and grow the BFS queue without the 100-file limit applying. Bound enumeration and execute filesystem discovery away from the UI, retaining cancellation and symlink exclusion. Root patch reviewed: new ShareFolderEnumeration runs discovery on Task.Run, bounds entries at 4096, depth at 32 and files at 100, rejects a reparse-point root and skips reparse children, checks cancellation. Tests cover empty-tree entry limit, pre-cancellation and depth. No runtime C# execution was performed here.

## Finding A2-APP-05 (P2): old drop completion can retire a replacement gesture

Baseline native `Services/OleDragDropService.cs:1045,1187–1196` allows accepted data processing to continue after Drop returns. A newer DragEnter/Smart candidate can own the presentation before old persistence completes. `Services/OverlayWindowService.cs:1227–1231` unconditionally clears current owner and completes current Smart session; `ViewModels/OverlayViewModel.cs:175–178` completes the current state. Delayed virtual-file cancellation/failure also called DragLeft without checking its generation.

Root added local target, service-wide and view-model generation guards; accepted durable data is still committed, while old presentation completion and DragLeft are suppressed. Independent review found and reported the equivalent WinUI path at baseline `OverlayWindow.xaml.cs:1750–1822`: payload retrieval awaited before ownership capture and unconditional ResetVisualDrag. Root then captured the guard before provider awaits, reset the local active flag at Drop entry, used guarded file/text callbacks, and gated Finish. Those control flows have been independently reviewed. New held callback/failure test exercises the native guarded helper with an external replacement generation; it does not exercise Windows OLE or WinUI routed drag delivery, nor prove multi-monitor behavior.

## Finding A2-APP-06 (P2): Quick Action dialogs outlive their surface and semaphore owner

Baseline `Views/MainPage.xaml.cs:499–503,1101–1105,2305–2306` and `OverlayWindow.xaml.cs:1608–1620` omit owner tokens. MainPage unload cancellation cannot dismiss these dialogs, so an obsolete page can approve an export or hold the singleton dialog gate. `Services/QuickActionDialogService.cs:103` disposes the semaphore while an in-flight request can still release it at :50/:99, causing ObjectDisposedException during exit. MainWindow previously only drained its privacy/close-explanation dialogs, not these page dialogs.

Root patch independently reviewed: caller owner tokens, QuickAction service shutdown token and asynchronous gate drain without premature semaphore disposal, explicit MainPage retirement before service disposal, and whole-XamlRoot dialog cancellation for nested cards before Unloaded. The newly async ContentDialogLifetime wrapper was separately flagged and fixed to retain ConfigureAwait(false), because cancellation must not newly depend on a retiring UI synchronization context. Review also found that MainViewModel execution overwrote the context owner token with a default caller token, and a queued MainWindow language rebuild lacked an exit guard; both follow-through fixes have now been independently reviewed. MainViewModel links caller, context and VM lifetime tokens, checks cancellation before registry access and after execution, and avoids status mutation after disposal. QuickActionCancellationTests covers all three already-cancelled owners before registry access; it is not an in-flight native export test. The queued language rebuild checks _allowClose and retires the previous page before replacement. Native dialog/picker retirement and export cleanup need Windows integration coverage.

## Finding A2-APP-07 (P2): copied share links bypass clipboard self-write suppression

Baseline `Views/MainPage.xaml.cs:1274–1278` directly calls Clipboard.SetContent instead of the capture service's self-write-marked copy. With recording enabled this can create an unintended history record; configured automatic peer propagation can then send the full share link, including the secure-share decryption fragment. This is a self-write consistency defect under enabled clipboard features, not evidence of an unauthenticated attack. Root patch routes through MainViewModel.CopyTextAsync to ClipboardCaptureService.CopyTextAsync with page ownership token. Diff reviewed; complete capture/propagation integration not run.

## Finding A2-APP-08 (P2): placement editing races detector shutdown and restores stale wake mode

Baseline `Services/OverlayWindowService.cs:720–724` calls fire-and-forget SetMode(Disabled), immediately sets placement suppression, then requests Smart mode. `Services/DragSessionDetector.cs:454–474` can suspend shutdown; its eventual :528 clears that newly set suppression. The next Smart observer can therefore create candidates during placement editing. Independently, editing saves the old mode at :716; changing wake mode through Settings during the edit applies a new mode at ConfigureWakeMode :961, but RestorePlacementInputMode :805–809 later restores the stale entry snapshot. Runtime detector and persisted settings can disagree.

Root patch independently reviewed: placement suppression is caller-owned desired state and ordinary detector Stop no longer clears it; final detector disposal still clears it. While editing, configured changes preserve the temporary Smart observer and do not create Classic hosts; exit force-applies the current configured setting instead of the stale entry snapshot. The new PlacementObserverLifetimeTests holds the real StopCoreAsync at an injected processor task, arms suppression, releases stop and asserts suppression survives. It was fully read, not executed here. Editing with concurrent mode changes still requires UI/native integration coverage.

## Remaining runtime validation requirements

- Windows 10/11 real dispatcher, app startup/cancel/exit, first-run privacy choices, language reconstruction while asynchronous UI work is pending
- Saturated native clipboard readers followed by another format; actual delayed GetResults/Cancel/Close and resource retirement
- Paused seek rejection with no later player events; track replacement and unload while acknowledgement timer is active
- OLE and WinUI sequential overlapping drops, virtual-provider rollback, two monitors/DPI/topology retirement, no stale visual ownership
- Dialog child cards and export pickers during navigation/exit, ensuring no post-retirement approval or in-flight semaphore disposal
- Placement editing with delayed observer stop and wake-mode changes
- Real SMTC, audio endpoints/process loopback, notification permissions, NetEase restart/install/rollback, graphics/backdrop and long-run resource behavior
- Narrator and keyboard accessibility, actual rendering at all supported scale factors

The known whole-song semantic/model validation block remains separately owned; this static review does not clear it. No source fixes, publish, push or release action was performed by this reviewer.

## Binary asset inventory (metadata only)

No visual, executable or semantic assurance is inferred from this inventory. PNG dimensions are read from their headers; ICO is inventoried as bytes only.

| Path under src/DropSpace.App/Assets | Bytes | Dimensions | SHA-256 |
|---|---:|---|---|
| AppIcon.ico | 51668 | ICO | cd7a2a3fa2875f173a7e2880ac96ddbaafdde5ba28b44cff717d4ddef2a7405e |
| LockScreenLogo.scale-200.png | 1933 | 48 × 48 | fa1ecab08bb8e0cee727782ce1a15cecc8bb85a413818d5dd253423124969046 |
| SplashScreen.scale-100.png | 25845 | 620 × 300 | ba6c121314786bcd6971627ab4e625607d205c9d9224df29281aeaf5ce9de34f |
| SplashScreen.scale-125.png | 38181 | 775 × 375 | ad9ea9a4ca4525e59176fce3143c1fbb5977ae0eb1000f251ef88f2fc16d9168 |
| SplashScreen.scale-150.png | 52320 | 930 × 450 | ec396f543a168127bbe40e09de8e00a68859b2a27410e3d203af040f6b31154d |
| SplashScreen.scale-200.png | 86001 | 1240 × 600 | 29728fd7caf4c86a18643d4426b0fd4f60a8a5df7d7238f8aeeab6b720fe1d8b |
| SplashScreen.scale-400.png | 281972 | 2480 × 1200 | a9cb257f8d75a465ab672b400af23c5baf40fa50b5aa3d77afd8b38fbb5d86ad |
| Square150x150Logo.scale-100.png | 13005 | 150 × 150 | fdae8b2c2c37805fc359775249f4e00e122924f4fcd00152ab5dd4102c3bc67e |
| Square150x150Logo.scale-125.png | 19107 | 188 × 188 | c02d12a8c2a7e948b8c6ce36fadd43629112d00794a49a5ed0c871b444a4f976 |
| Square150x150Logo.scale-150.png | 25976 | 225 × 225 | ef1d2b8a59449fb7ff98f7164b66205eb6ad557a150bed4269b5631a0437ce80 |
| Square150x150Logo.scale-200.png | 42726 | 300 × 300 | 0bd3713085ec326b17ebbb8cd6da76e91a41a6a12302728517c15756c624f659 |
| Square150x150Logo.scale-400.png | 140068 | 600 × 600 | 44800784ad4161ce8d4628fa5e3a1c21d033a977600d10b70d92e4e0686e1d4e |
| Square44x44Logo.scale-100.png | 1697 | 44 × 44 | 7f4c6332d0ea3786ad0c1c5b04b9fbad81019d39b7f01afb433bd955601c849e |
| Square44x44Logo.scale-125.png | 2389 | 55 × 55 | e1d7da464d884e27b33895ef2f501bd0c5ddc5b039fdaa432c6798943bec9b27 |
| Square44x44Logo.scale-150.png | 3336 | 66 × 66 | 805f27348087bb658450f24d9f074777516de5d0bce1878c412c64bcdca57226 |
| Square44x44Logo.scale-200.png | 5387 | 88 × 88 | c73a12f4693a2e51414c9d517555fe8a9725f19cbf36420dddf8f2e80ed1d099 |
| Square44x44Logo.scale-400.png | 17131 | 176 × 176 | ff9fd2ad8b22e04bd80ff50338658e067524f818c3cacbe2039adcf24c69c746 |
| Square44x44Logo.targetsize-16_altform-unplated.png | 366 | 16 × 16 | 3207c8f144b818d6c7303291c9acba05c5b766e22042c30159054908efe4eedd |
| Square44x44Logo.targetsize-20_altform-unplated.png | 474 | 20 × 20 | 38cbc8ca0108efa5e318dd38965b63a716e2658b5bae8c03d3e4a81c7edaa2d4 |
| Square44x44Logo.targetsize-24_altform-unplated.png | 628 | 24 × 24 | bf98886f6975d61670619ebd2bcc62e361ea8f0dc1b63d986fe8478107e993c2 |
| Square44x44Logo.targetsize-256_altform-unplated.png | 32556 | 256 × 256 | 17eab182ebf1105221c6b3b04f580f7abb2158c5f1827bc0161991a0679b7050 |
| Square44x44Logo.targetsize-30_altform-unplated.png | 879 | 30 × 30 | 6b71233ecf5ee6c25fea7666d3c4a7ad0eb9266c64bbf47668e3dad77631f94a |
| Square44x44Logo.targetsize-32_altform-unplated.png | 1008 | 32 × 32 | b379ce55375d3f624792752eb091f33a435618ceca122eb7a18d118d34803840 |
| Square44x44Logo.targetsize-36_altform-unplated.png | 1175 | 36 × 36 | b1fcd840ebb2bcb0de6e9bc7339c89c148cb1f867157c226e560272577640002 |
| Square44x44Logo.targetsize-40_altform-unplated.png | 1398 | 40 × 40 | 9a40238c630d831de3d845ed9644ca810c48b6bba83156c6dbe375428ceee8d9 |
| Square44x44Logo.targetsize-44_altform-unplated.png | 1697 | 44 × 44 | 7f4c6332d0ea3786ad0c1c5b04b9fbad81019d39b7f01afb433bd955601c849e |
| Square44x44Logo.targetsize-48_altform-unplated.png | 1933 | 48 × 48 | fa1ecab08bb8e0cee727782ce1a15cecc8bb85a413818d5dd253423124969046 |
| Square44x44Logo.targetsize-60_altform-unplated.png | 2843 | 60 × 60 | 03cbe3f15b65a849f7df4ab2ee21f4e6b9fc910ca50b1e74bde8877897684a11 |
| Square44x44Logo.targetsize-64_altform-unplated.png | 3177 | 64 × 64 | 52b9a8a6891367a7d6234f2694f098becc082d2a325314c00c5c35586c60e995 |
| Square44x44Logo.targetsize-72_altform-unplated.png | 3930 | 72 × 72 | 6d46d128aaa08e24b358f6beb812abcefc75005e8d8c0ca723b2a9153f69a57a |
| Square44x44Logo.targetsize-80_altform-unplated.png | 4557 | 80 × 80 | 0b6fcbe1245e37ff77d02b1dafd6eaeadcc073ca82e792edc4cac08ef0dd6f61 |
| Square44x44Logo.targetsize-96_altform-unplated.png | 6188 | 96 × 96 | edbf8f18942614aced2d86ce57856887c8e4e838a67a4b98a237367ca13599b4 |
| StoreLogo.png | 2047 | 50 × 50 | b4662a91974adc956efe6205317f45ada3485ed700021c454f30354e7cd03474 |
| StoreLogo.scale-100.png | 2047 | 50 × 50 | b4662a91974adc956efe6205317f45ada3485ed700021c454f30354e7cd03474 |
| StoreLogo.scale-125.png | 3104 | 63 × 63 | 5f70331035a9191f6bebc1840738e992c77217c3126563c74af3645dfea3b243 |
| StoreLogo.scale-150.png | 4160 | 75 × 75 | 9a813923ce5129738115c6abf41aa5b8d2142189e4788e294e11d3064f90c234 |
| StoreLogo.scale-200.png | 6598 | 100 × 100 | 15c487e58e445df1ccfedc6da548be81ec02c76290cea41c745a020c2f79866e |
| StoreLogo.scale-400.png | 21306 | 200 × 200 | 873fa12550d1c4ba0cf39816e5bf439ba4711bef470d4f9a3ffce4c4f6a03aee |
| Wide310x150Logo.scale-100.png | 7865 | 310 × 150 | c265cef3523cd288a831b22b2e549e9d6d7e036ea03411b29cae8ab01f9418ca |
| Wide310x150Logo.scale-125.png | 11613 | 388 × 188 | d59a5d37163abe6296ec123fc4c9b38d39faf00590824b806d9fb135f240de55 |
| Wide310x150Logo.scale-150.png | 15786 | 465 × 225 | c9f50be080b6f2569db363dbe4ace9ba44ccd5171ed4ca12c6fc5893cbea91c6 |
| Wide310x150Logo.scale-200.png | 25845 | 620 × 300 | ba6c121314786bcd6971627ab4e625607d205c9d9224df29281aeaf5ce9de34f |
| Wide310x150Logo.scale-400.png | 86001 | 1240 × 600 | 29728fd7caf4c86a18643d4426b0fd4f60a8a5df7d7238f8aeeab6b720fe1d8b |

## Reviewed working-tree text fingerprint

200 listed text files, sorted path + NUL + raw SHA-256 digest: `9ee897f9558ef4148b1a1bbe4aaff06b5ba45624acd73860a097a867bdb217b1`. This identifies the source/test/config snapshot reviewed after root repairs; any later change needs its own follow-up review.

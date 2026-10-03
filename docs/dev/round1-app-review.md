# Round 1 App review

## Scope and evidence boundary

Original candidate: `a5b131c281cb32033a94083e0329a9ace16ae9e0`, tree `21114c46cfcc9af6fa9a3c87c7ca957c3a8e0c83`; corresponding remote PR 76 head `c7e9e60963aae56c603a616eb14b9f22c4550665`. Review began 2026-10-01. Root owns integration and the final issue register. This is the App partition of round 1, not a whole-repository completion or release approval. Website excluded as separately changing. Brand binaries and generated obj/bin excluded.

The findings below are established by source/control-flow review. Reproduction instructions are proposed checks unless execution is specifically stated. No actual Windows desktop, Narrator, real player, display topology, or two-device session was exercised by this reviewer. Original source was read before edits; subsequent reads use the original commit when files changed. Some test files were read after root added remediation tests.

## Confirmed findings

### R1-App-1: Clipboard live eviction skips previously paged history

- Severity: Medium
- Original location: `ViewModels/MainViewModel.cs:1505–1537, especially 1532`
- Reproduction / impact: Load more than 250 clipboard records, copy new text, and continue paging. Live insertion removes tail rows while the cursor still points beyond the previously loaded page; after the last page, HasMoreItems can remain false. Older records disappear from this projection until reload.
- Minimal remedy: Preserve explicitly paged rows or rebuild cursor/paging ownership whenever rows are evicted.
- Regression: Load 500 records, append a capture, page to the end, and assert every persisted ID remains reachable.
- Status: Root remediation in progress; this reviewer has not validated the final fix.

### R1-App-2: Concurrent dialogs can turn a recoverable presentation error into an unhandled UI exception

- Severity: High
- Original location: `Views/MainPage.xaml.cs:710–748, 2216–2255, 2276–2300`
- Reproduction / impact: Start two previews before asynchronous preparation completes, or receive a peer offer while another direct ContentDialog is open. Direct dialogs bypass the page gate. The second ShowAsync fails; RunAsync then tries another error dialog while the first is open, and that failure escapes the async-void handler.
- Minimal remedy: Serialize every ContentDialog at its XamlRoot, tie queued/presented dialogs to page/window lifetimes, and make error reporting nonthrowing.
- Regression: Concurrent preview/receive/close tests; a native dialog remains owned until actually closed after cancellation.
- Status: Root remediation in progress.

### R1-App-3: Paired peers and clipboard channels are not restored

- Severity: Medium
- Original location: `Views/MainPage.xaml.cs:567; Services/DeviceHandoffService.cs:150–174; Services/CrossDeviceClipboardService.cs:91–112`
- Reproduction / impact: Pair an outgoing device, restart or change display language, then try Send to device. MainPage only populates its dictionary after that page initiates pairing; persisted peers are never loaded into it. Incoming pairing does not populate it. Disabling clipboard sync clears channels and re-enabling does not restore them.
- Minimal remedy: Load trusted peer identities and reconcile verified discovery endpoints; persist/restore explicitly selected channels and modes.
- Regression: Two-device pairing from both ends, restart, language change, and disable/re-enable sequences.
- Status: Open.

### R1-App-4: Clipboard images cannot be dragged from the main item list

- Severity: Medium
- Original location: `ViewModels/MainViewModel.cs:1469–1475; Views/MainPage.xaml.cs:346–392`
- Reproduction / impact: Capture a PNG image and drag its card to Explorer. Thumbnail loading resolves DragStorageItem only for file references; the image payload branch in DragStorageItemService is never reached. The drag handler has neither a storage item nor text and cancels.
- Minimal remedy: Resolve readable available image payloads as well as file references.
- Regression: Image-card preparation and drag data package regression.
- Status: Root remediation in progress.

### R1-App-5: Copying the drag report can throw directly from a UI event

- Severity: Medium
- Original location: `ViewModels/MainViewModel.cs:593–600; Views/MainPage.xaml.cs:1530–1531`
- Reproduction / impact: Hold the Windows clipboard busy and click Copy Smart Drag compatibility report. SetContent/Flush exceptions propagate from an unguarded synchronous event.
- Minimal remedy: Use the existing bounded clipboard writer and guarded command/error feedback.
- Regression: Inject clipboard busy/access failure and assert no unhandled exception, useful feedback, and retry.
- Status: Root remediation in progress.

### R1-App-6: A crash inside the Undo window strands hidden rows

- Severity: Medium
- Original location: `Services/UndoCoordinator.cs:195–218; MainViewModel initialization`
- Reproduction / impact: Remove an item, terminate the process, and restart inside the eight-second deadline. Recovery only finalizes already-expired tokens, while no timer or Undo slot is rebuilt for future-expiry persisted removals. The row remains hidden after expiry until another restart. Core reviewer verified no other finalization path.
- Minimal remedy: Restore or explicitly finalize orphaned pending tokens according to recovery policy, or reconstruct timed ownership.
- Regression: Seed a future-expiry pending removal, restart/recover, advance time, and check visibility and owned payload cleanup.
- Status: Root remediation in progress.

### R1-App-7: Ctrl+F has no useful action on Music

- Severity: Low
- Original location: `Views/MainPage.xaml.cs:1754–1764, UpdateSectionChrome`
- Reproduction / impact: Open Music and press Ctrl+F. Only Settings navigates away before SearchBox.Focus, while Music also hides SearchBox.
- Minimal remedy: Navigate to a searchable collection from both Settings and Music.
- Regression: Keyboard test for Music → Ctrl+F → visible focused search.
- Status: Root remediation in progress.

### R1-App-8: Legacy settings lack programmatic labels

- Severity: Medium
- Original location: `Views/MainPage.xaml, legacy NumberBox/ComboBox/update toggle controls`
- Reproduction / impact: Inspect UI Automation names for image/file limits, retention, theme, update channel, and update toggles. They have adjacent visual labels but no Header/AutomationProperties.Name/LabeledBy association.
- Minimal remedy: Add localized automation names or label associations consistently.
- Regression: UIA assertions plus Narrator keyboard pass at normal and large text sizes.
- Status: Open; native accessibility execution not performed.

### R1-App-9: Deferred file/bitmap providers can stop all subsequent clipboard captures

- Severity: High
- Original location: `Services/ClipboardCaptureService.cs:1074–1130 (original snapshot)`
- Reproduction / impact: Publish a StorageItems or Bitmap provider that holds its response, then copy eager text. The original single-reader worker awaits native operations without a bounded/supersedable managed wait; subsequent notifications cannot progress.
- Minimal remedy: Keep bounded native ownership for the entire provider chain, release only the managed waiter on timeout/supersession, request Cancel off UI, and close after actual completion; retain streams/buffers until safe.
- Regression: Deferred StorageItems and Bitmap native providers followed by eager text; late stream, blocked Cancel, failed/retried Close, cancellation and bounded ownership tests.
- Status: Implemented in this working tree by this reviewer; 5 pure helper tests passed; real Windows projection compilation passed; native execution pending.

### R1-App-10: Transient native geometry failure permanently disables the island

- Severity: Medium
- Original location: `OverlayWindow.xaml.cs:644–653, 732–756, PositionFixedHost`
- Reproduction / impact: Inject a single failed Move/ResizeClient/client-size validation. HideForNativeFailure sets _nativeWindowSafeToShow=false permanently. Later show attempts return immediately; an unchanged topology broadcast does not rebuild windows.
- Minimal remedy: Separate immutable initial safety from recoverable frame failures, then revalidate checked native configuration and geometry before retrying reveal.
- Regression: Fail one positioning/region call, restore API success, assert the same window recovers without topology change.
- Status: Open.

### R1-App-11: Classic activation-host replacements remain retained for process lifetime

- Severity: Medium
- Original location: `Services/OleDragDropService.cs:64, Dispose; OverlayWindowService.ConfigureWakeMode`
- Reproduction / impact: Repeatedly toggle Classic/Smart or rebuild monitors. OverlayWindowService disposes old hosts, but OleDragDropService keeps every host in _registrations until process shutdown. Disposed registrations can also retain the current IDataObject.
- Minimal remedy: Use one owner or remove disposed registrations; clear drag data and callbacks/resources at retirement.
- Regression: Repeated replacement count and weak-reference convergence; disposing an active drag releases its data object.
- Status: Open.

### R1-App-12: Startup recovery claims writes stopped when capture may still run

- Severity: Medium
- Original location: `App.xaml.cs:299–320; Strings/*/Resources.resw StartupRecoveryContent`
- Reproduction / impact: Fail overlay creation after MainViewModel.InitializeAsync starts clipboard recording. The recovery dialog states that writing stopped, but the capture worker is neither canceled nor paused. Copying may still append history.
- Minimal remedy: Enter a genuine stopped/read-only recovery state before this message, or use accurate separate handling for optional presentation failures.
- Regression: Post-capture startup failure followed by clipboard change must match displayed recovery guarantees.
- Status: Corrected the bilingual recovery message to describe incomplete startup and possible running background services, and require exit before inspecting/restoring data. The general catch no longer falsely claims a database-specific failure or stopped writes. No runtime shutdown behavior changed; native failure-injection validation remains pending.

### R1-App-13: Remote JPEG/BMP payloads are saved with a PNG extension

- Severity: Medium
- Original location: `Services/ClipboardCaptureService.cs:389–402; Infrastructure/Storage/FilePayloadStore.cs:46–48; Services/WindowsImageCodecPreflight.cs:34–46`
- Reproduction / impact: Import a valid remote JPEG envelope. Its unchanged bytes are written through WriteAsync("images"), which defaults to .png. Signature/extension preflight subsequently rejects it, breaking image quick actions and giving drag files a misleading extension.
- Minimal remedy: Choose the extension from the validated Windows decoder and write through the existing owned-payload WriteFileAsync API.
- Regression: Remote JPEG/BMP/PNG import, exact byte preservation, detected extension, and CanDecode/action eligibility.
- Status: Implemented in this working tree by this reviewer: decoder-owned extension and MIME are stored with unchanged validated bytes via WriteFileAsync. Four native import cases (PNG/JPEG/BMP plus mislabeled JPEG) added and compiled against the real Windows projections; native execution pending.

## Verification actually run by this reviewer

- Read AGENTS.md and canonical dropspace-maintainer skill and App/UI guide; relevant architecture/product/feature/UX material
- Read production C# bodies and XAML trees/attributes listed below, including old features; resw entries read as paired English/Chinese key/value inventory
- Resource structural check: 654 entries in each locale, equal key sets, no numeric-format-placeholder mismatch
- Clipboard lifetime helper Linux-linked harness: 5 passed, 0 failed
- Linked Windows-target compile of ClipboardCaptureService, helper/dependencies, and four affected test classes against real Windows SDK/Dispatcher projections: 0 warnings, 0 errors
- Four capture-change C# files parsed: 0 syntax errors; scoped git diff check clean
- No native App test execution here; parent CI results must be reconciled separately

## Exact coverage inventory

### Read production files

- `src/DropSpace.App/packages.lock.json` (metadata review)
- `src/DropSpace.App/App.xaml`
- `src/DropSpace.App/App.xaml.cs`
- `src/DropSpace.App/Converters/BoolToVisibilityConverter.cs`
- `src/DropSpace.App/Converters/NullToVisibilityConverter.cs`
- `src/DropSpace.App/DropSpace.App.csproj`
- `src/DropSpace.App/DropSpace.rc`
- `src/DropSpace.App/MainWindow.xaml`
- `src/DropSpace.App/MainWindow.xaml.cs`
- `src/DropSpace.App/OverlayWindow.xaml`
- `src/DropSpace.App/OverlayWindow.xaml.cs`
- `src/DropSpace.App/Package.appxmanifest`
- `src/DropSpace.App/Properties/AssemblyInfo.cs`
- `src/DropSpace.App/Properties/launchSettings.json`
- `src/DropSpace.App/Services/AppLanguageService.cs`
- `src/DropSpace.App/Services/Audio/ProcessLoopbackInterop.cs`
- `src/DropSpace.App/Services/Audio/WindowsProcessLoopbackService.cs`
- `src/DropSpace.App/Services/AuthenticodeTrustedUpdateVerifier.cs`
- `src/DropSpace.App/Services/BackdropLifetimeSmoke.cs`
- `src/DropSpace.App/Services/ClipboardAccessPolicy.cs`
- `src/DropSpace.App/Services/ClipboardCaptureDiagnostics.cs`
- `src/DropSpace.App/Services/ClipboardCaptureService.cs`
- `src/DropSpace.App/Services/ClipboardIntegrationSmoke.cs`
- `src/DropSpace.App/Services/ClipboardNotificationService.cs`
- `src/DropSpace.App/Services/CrashDiagnosticsService.cs`
- `src/DropSpace.App/Services/CrossDeviceClipboardService.cs`
- `src/DropSpace.App/Services/DeploymentModeService.cs`
- `src/DropSpace.App/Services/DeviceHandoffService.cs`
- `src/DropSpace.App/Services/DeviceHandoffUseCase.cs`
- `src/DropSpace.App/Services/DispatcherQueueExtensions.cs`
- `src/DropSpace.App/Services/DisplayIdentityService.cs`
- `src/DropSpace.App/Services/DisplayTopologyWatcher.cs`
- `src/DropSpace.App/Services/DragSessionDetector.cs`
- `src/DropSpace.App/Services/DragStorageItemService.cs`
- `src/DropSpace.App/Services/ForegroundWindowMonitor.cs`
- `src/DropSpace.App/Services/GlobalQuickPanelHotkeyService.cs`
- `src/DropSpace.App/Services/ImageDecoderPreflight.cs`
- `src/DropSpace.App/Services/InnoUpdateInstallerLauncher.cs`
- `src/DropSpace.App/Services/IslandAcrylicBackdrop.cs`
- `src/DropSpace.App/Services/IslandGlowController.cs`
- `src/DropSpace.App/Services/IslandGlowRasterizer.cs`
- `src/DropSpace.App/Services/IslandGlowWindow.cs`
- `src/DropSpace.App/Services/ItemProjectionService.cs`
- `src/DropSpace.App/Services/ItemSharingService.cs`
- `src/DropSpace.App/Services/MaintenanceShutdownService.cs`
- `src/DropSpace.App/Services/Media/AiLyricsService.cs`
- `src/DropSpace.App/Services/Media/MediaApplicationIconService.cs`
- `src/DropSpace.App/Services/Media/MediaArtworkService.cs`
- `src/DropSpace.App/Services/Media/MediaExperienceService.cs`
- `src/DropSpace.App/Services/Media/MediaProcessResolver.cs`
- `src/DropSpace.App/Services/Media/WindowsMediaSessionService.cs`
- `src/DropSpace.App/Services/MonitorLayoutService.cs`
- `src/DropSpace.App/Services/NativeApplicationIcon.cs`
- `src/DropSpace.App/Services/NativeFolderPickerService.cs`
- `src/DropSpace.App/Services/NativeSubscriberNotification.cs`
- `src/DropSpace.App/Services/NativeTrayService.cs`
- `src/DropSpace.App/Services/NeteaseEnhancement/BetterNcmProbe.cs`
- `src/DropSpace.App/Services/NeteaseEnhancement/InfLinkDeploymentService.IO.cs`
- `src/DropSpace.App/Services/NeteaseEnhancement/InfLinkDeploymentService.Packages.cs`
- `src/DropSpace.App/Services/NeteaseEnhancement/InfLinkDeploymentService.cs`
- `src/DropSpace.App/Services/NeteaseEnhancement/NeteaseEnhancementService.cs`
- `src/DropSpace.App/Services/NeteaseEnhancement/NeteaseInstallationProbe.cs`
- `src/DropSpace.App/Services/NeteaseEnhancement/NeteaseRuntimeInstaller.cs`
- `src/DropSpace.App/Services/NeteaseEnhancement/NeteaseSmtcVerifier.cs`
- `src/DropSpace.App/Services/Notifications/WindowsNotificationActivityService.cs`
- `src/DropSpace.App/Services/Ole/EphemeralOleDragProbe.cs`
- `src/DropSpace.App/Services/Ole/OleDropTargetNative.cs`
- `src/DropSpace.App/Services/Ole/OleFileDataClassifier.cs`
- `src/DropSpace.App/Services/Ole/QueryOnlyDataObject.cs`
- `src/DropSpace.App/Services/Ole/SmartDragProbeOptions.cs`
- `src/DropSpace.App/Services/Ole/SmartDragRuntimePolicy.cs`
- `src/DropSpace.App/Services/Ole/VirtualFileMaterializer.cs`
- `src/DropSpace.App/Services/OleDragDropService.cs`
- `src/DropSpace.App/Services/OverlayCompositionAnimator.cs`
- `src/DropSpace.App/Services/OverlayMaterialController.cs`
- `src/DropSpace.App/Services/OverlayMotionOrchestrator.cs`
- `src/DropSpace.App/Services/OverlayNativeRegionController.cs`
- `src/DropSpace.App/Services/OverlayWindowInterop.cs`
- `src/DropSpace.App/Services/OverlayWindowService.cs`
- `src/DropSpace.App/Services/PinItemsUseCase.cs`
- `src/DropSpace.App/Services/QuickActionDialogService.cs`
- `src/DropSpace.App/Services/QuickPreviewService.cs`
- `src/DropSpace.App/Services/ReleaseBuildInfo.cs`
- `src/DropSpace.App/Services/ResourceStringLocalizer.cs`
- `src/DropSpace.App/Services/SecureInternetShareService.cs`
- `src/DropSpace.App/Services/SettingsApplicationCoordinator.cs`
- `src/DropSpace.App/Services/SettingsTransactionRollbackCoordinator.cs`
- `src/DropSpace.App/Services/SettingsUpdateException.cs`
- `src/DropSpace.App/Services/ShareTargetActivationService.cs`
- `src/DropSpace.App/Services/SharingUseCase.cs`
- `src/DropSpace.App/Services/ShellActionService.cs`
- `src/DropSpace.App/Services/ShellIntakeActivationService.cs`
- `src/DropSpace.App/Services/StartupRegistrationService.cs`
- `src/DropSpace.App/Services/SystemActivityExperienceService.cs`
- `src/DropSpace.App/Services/SystemVisualPreferenceService.cs`
- `src/DropSpace.App/Services/ThumbnailService.cs`
- `src/DropSpace.App/Services/TrustedPublisherIdentityPolicy.cs`
- `src/DropSpace.App/Services/UndoCoordinator.cs`
- `src/DropSpace.App/Services/Volume/VolumeInterop.cs`
- `src/DropSpace.App/Services/Volume/WindowsVolumeActivityService.cs`
- `src/DropSpace.App/Services/Widgets/NativeWidgetDataService.cs`
- `src/DropSpace.App/Services/WindowsCompatibilityService.cs`
- `src/DropSpace.App/Services/WindowsImageCodecPreflight.cs`
- `src/DropSpace.App/Services/WindowsImageTransformService.cs`
- `src/DropSpace.App/Services/WindowsShareIntegrationService.cs`
- `src/DropSpace.App/Services/WorkspaceMutationUseCase.cs`
- `src/DropSpace.App/Services/XamlResourceOverride.cs`
- `src/DropSpace.App/Strings/en-US/Resources.resw`
- `src/DropSpace.App/Strings/zh-CN/Resources.resw`
- `src/DropSpace.App/ViewModels/ClipboardIslandViewModel.cs`
- `src/DropSpace.App/ViewModels/ItemCardViewModel.cs`
- `src/DropSpace.App/ViewModels/MainViewModel.cs`
- `src/DropSpace.App/ViewModels/MediaViewModel.cs`
- `src/DropSpace.App/ViewModels/NativeSettingsEditor.cs`
- `src/DropSpace.App/ViewModels/NeteaseEnhancementViewModel.cs`
- `src/DropSpace.App/ViewModels/OverlayViewModel.cs`
- `src/DropSpace.App/ViewModels/QuickActionButtonViewModel.cs`
- `src/DropSpace.App/ViewModels/SystemActivityViewModel.cs`
- `src/DropSpace.App/ViewModels/WidgetViewModel.cs`
- `src/DropSpace.App/Views/ContentDialogLifetime.cs`
- `src/DropSpace.App/Views/DesignTokens.xaml`
- `src/DropSpace.App/Views/Island/ClipboardIslandView.xaml`
- `src/DropSpace.App/Views/Island/ClipboardIslandView.xaml.cs`
- `src/DropSpace.App/Views/Island/MediaCompactView.xaml`
- `src/DropSpace.App/Views/Island/MediaCompactView.xaml.cs`
- `src/DropSpace.App/Views/Island/MediaExpandedView.xaml`
- `src/DropSpace.App/Views/Island/MediaExpandedView.xaml.cs`
- `src/DropSpace.App/Views/Island/WidgetsExpandedView.xaml`
- `src/DropSpace.App/Views/Island/WidgetsExpandedView.xaml.cs`
- `src/DropSpace.App/Views/MainPage.Settings.cs`
- `src/DropSpace.App/Views/MainPage.xaml`
- `src/DropSpace.App/Views/MainPage.xaml.cs`
- `src/DropSpace.App/Views/Music/AiLyricsSettingsCard.cs`
- `src/DropSpace.App/Views/Music/LyricsGlowModeControl.cs`
- `src/DropSpace.App/Views/Music/MusicPage.cs`
- `src/DropSpace.App/Views/Music/NeteaseEnhancementCard.cs`
- `src/DropSpace.App/Views/Settings/SettingsForm.cs`
- `src/DropSpace.App/Views/Settings/WidgetEditorView.cs`
- `src/DropSpace.App/app.manifest`

### Read App test files

- `tests/DropSpace.App.Tests/ClipboardDeferredProviderNativeTests.cs`
- `tests/DropSpace.App.Tests/ClipboardDiagnosticsTests.cs`
- `tests/DropSpace.App.Tests/ClipboardOverflowAccountingTests.cs`
- `tests/DropSpace.App.Tests/ClipboardStateRaceTests.cs`
- `tests/DropSpace.App.Tests/ClipboardTextSupersessionTests.cs`
- `tests/DropSpace.App.Tests/ContentDialogLifetimeTests.cs`
- `tests/DropSpace.App.Tests/DispatcherCallbackShutdownRegressionTests.cs`
- `tests/DropSpace.App.Tests/DropSpace.App.Tests.csproj`
- `tests/DropSpace.App.Tests/FullAuditBatchProjectionRegressionTests.cs`
- `tests/DropSpace.App.Tests/FullAuditClipboardShutdownRegressionTests.cs`
- `tests/DropSpace.App.Tests/FullAuditNativeBoundaryRegressionTests.cs`
- `tests/DropSpace.App.Tests/FullAuditNeteaseProbeRegressionTests.cs`
- `tests/DropSpace.App.Tests/FullAuditOverlayOwnershipRegressionTests.cs`
- `tests/DropSpace.App.Tests/FullAuditShareBitmapRegressionTests.cs`
- `tests/DropSpace.App.Tests/FullAuditUndoRegressionTests.cs`
- `tests/DropSpace.App.Tests/FullAuditVirtualDescriptorRegressionTests.cs`
- `tests/DropSpace.App.Tests/IslandGlowNativeWindowTests.cs`
- `tests/DropSpace.App.Tests/IslandGlowRasterizerTests.cs`
- `tests/DropSpace.App.Tests/MediaRegressionTests.cs`
- `tests/DropSpace.App.Tests/MediaSeekInteractionTests.cs`
- `tests/DropSpace.App.Tests/MediaSessionNativeSmokeTests.cs`
- `tests/DropSpace.App.Tests/MediaSessionSelectionTests.cs`
- `tests/DropSpace.App.Tests/MonitorTopologyRegressionTests.cs`
- `tests/DropSpace.App.Tests/NativeLifecycleRegressionTests.cs`
- `tests/DropSpace.App.Tests/NeteaseDeploymentTests.cs`
- `tests/DropSpace.App.Tests/NeteaseFileMutationTests.cs`
- `tests/DropSpace.App.Tests/NeteaseProcessRaceTests.cs`
- `tests/DropSpace.App.Tests/NeteaseRuntimePolicyTests.cs`
- `tests/DropSpace.App.Tests/NeteaseSmtcVerifierTests.cs`
- `tests/DropSpace.App.Tests/NotificationNativeSmokeTests.cs`
- `tests/DropSpace.App.Tests/OleCallbackBoundaryTests.cs`
- `tests/DropSpace.App.Tests/OleFileDataClassifierWindowsTests.cs`
- `tests/DropSpace.App.Tests/OverlayRegionNativeSmokeTests.cs`
- `tests/DropSpace.App.Tests/OverlayRetirementRegressionTests.cs`
- `tests/DropSpace.App.Tests/Preview16ImageDecoderTests.cs`
- `tests/DropSpace.App.Tests/Preview16LifecycleTests.cs`
- `tests/DropSpace.App.Tests/Preview16OleLifetimeTests.cs`
- `tests/DropSpace.App.Tests/ProbeReentrancyRegressionTests.cs`
- `tests/DropSpace.App.Tests/ProcessLoopbackNativeSmokeTests.cs`
- `tests/DropSpace.App.Tests/SettingsLastCheckRegressionTests.cs`
- `tests/DropSpace.App.Tests/ShellAcknowledgementLifecycleRegressionTests.cs`
- `tests/DropSpace.App.Tests/SmartDragProbeOptionsTests.cs`
- `tests/DropSpace.App.Tests/TrayResourceBoundaryTests.cs`
- `tests/DropSpace.App.Tests/TrustedPublisherIdentityPolicyTests.cs`
- `tests/DropSpace.App.Tests/UndoLifecycleTests.cs`
- `tests/DropSpace.App.Tests/VirtualFileApartmentCleanupTests.cs`
- `tests/DropSpace.App.Tests/VolumeNativeSmokeTests.cs`
- `tests/DropSpace.App.Tests/WidgetDataNativeSmokeTests.cs`
- `tests/DropSpace.App.Tests/WindowsImageCodecPreflightTests.cs`
- `tests/DropSpace.App.Tests/packages.lock.json`
- `tests/DropSpace.App.Tests/ClipboardProviderReadLifetimeTests.cs` (new remediation tests)
- `tests/DropSpace.App.Tests/RemoteClipboardImageFormatTests.cs` (new remediation tests)

### Remaining source files

No unread original App/App.Tests owned source files remain in this partition. Dependency locks were inspected as package/version/dependency/checksum-presence metadata; third-party dependency source and vulnerability status were not audited.

### Explicitly unverified runtime areas

- First launch and restart in native Windows; desktop installation/portable/MSIX registration
- XAML layout, minimum width, high contrast, scaling and Narrator behavior
- Actual clipboard delayed providers, image decode/export, Explorer/OLE drag gestures and virtual file sources
- Music players, seek/control timing, process-loopback capture, real AI download/inference UI and page retirement
- Actual HWND material/glow hit testing, monitor/DPI disconnect/reconnect and native resource plateaus
- Device pairing/transfer/clipboard synchronization and network failures across two Windows machines
- Full native App test suite, even though the test source inventory has now been read; source review is not execution

## Remediation verification details

At 2026-10-01 19:18 UTC, capture/provider tests were recompiled after the remote image fix: linked Windows-target compilation passed with 0 warnings/errors. The pure provider ownership harness was rerun: 5 passed. `RemoteClipboardImageFormatTests` exercises actual `ImportRemoteAsync`, then validates bytes, MIME, extension, and the same codec preflight used by quick actions. Its 4 cases are compiled but not executed here. The 2 deferred file/bitmap NativeSmoke cases are also compiled but not executed. No commits or pushes were made by this reviewer.

## Source-review completion checkpoint

At 2026-10-01 19:20 UTC, the App partition source inventory was fully read: 139 original non-brand production/config/resource files and 50 original App.Tests source/config files. XAML was reviewed through complete element/attribute trees alongside code-behind; every bilingual resw entry was reviewed as paired key/value text. Native interface declarations, callback ownership, marshaling, cancellation, and test fixtures were included. This is static source coverage, not executed line/branch coverage. It does not establish that all features are correct, nor complete the other repository partitions, final-fix review, native acceptance, or the separate product-manager review.

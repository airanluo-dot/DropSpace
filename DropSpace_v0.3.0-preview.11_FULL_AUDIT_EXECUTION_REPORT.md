# DropSpace v0.3.0-preview.11 Full Audit Execution Report

Date: 2026-09-03  
Repository: airanluo-dot/DropSpace  
Pull request: #39 — Harden Preview.11 drag, sharing, lifecycle, and exports  
Pull request URL: https://github.com/airanluo-dot/DropSpace/pull/39

## 1. Final commit and delivery status

| Item | SHA / status |
|---|---|
| Audit base / previous main | 9ec6cc4d083ddb06731dd8dae593a46269e32b88 |
| Implementation branch final head | 8cb93e77fe8409eefc52287ea53b2f7d7660b205 |
| Squash-merged main and release source | cb3fe562cdb37f452852dcef8406471a02364d12 |
| Pull request | Merged |
| Preview release | v0.3.0-preview.11, public prerelease |
| Release conclusion | Published as CONDITIONAL Preview; unconditional full-audit sign-off remains BLOCKED by the deferred real-OS/DPI evidence matrix |

The implementation was merged to main before the Preview.11 release workflow ran. The follow-up execution report is documentation-only and does not change the shipped implementation commit.

## 2. Finding-by-finding disposition

### P0 / P1 audit findings

| ID | Status | Final disposition and evidence boundary |
|---|---|---|
| DS-AUD-001 | Fixed | Smart Drag now re-reads the live cursor at the last native verification boundary, re-resolves the monitor, and revalidates disposal, mode, session, and detector policy. Cursor-read failure fails closed. The required hands-on multi-DPI native matrix is recorded separately as deferred evidence. |
| DS-AUD-002 | Fixed | Ephemeral OLE probe watchdogs only request owner-thread cleanup. RevokeDragDrop and DestroyWindow remain on the probe owner thread, including queue-failure and timeout paths. A 1000-cycle native residue measurement was not available in hosted CI and remains a runtime evidence gap. |
| DS-AUD-003 | Fixed | DragSessionDetector shutdown is async-first and owns processor, hook, grace, and timeout task lifetimes. UI paths no longer synchronously Join/Wait the hook and processor. Repeated mode-switch and shutdown stress on physical systems remains part of the deferred runtime matrix. |
| DS-AUD-004 | Deferred | Static Win10 1809 contracts remain consistent and hosted Windows CI passed, but CI uses windows-latest and does not prove Windows 10 1809/22H2 or version-specific Windows 11 OLE, DWM, Clipboard, Tray, DNS-SD, AppWindow, or mixed-DPI behavior. A real OS/DPI/monitor/Explorer/provider evidence package is still required for unconditional completion. |
| DS-AUD-005 | Fixed | The reference Worker now uses ShareUsageCoordinator Durable Object state for concurrency-safe aggregate plaintext-byte and item accounting, including pending first chunks. It validates token claims, manifest/chunk contract, size limits, duplicate objects, expiry, and paginates R2 deletion during revoke. |
| DS-AUD-006 | Fixed | Revoke handles are persisted under the application data directory with current-user DPAPI protection, restored at startup, expiry-filtered, atomically written, and deleted after successful or already-completed revoke. Corrupt and expired records are retired. |
| DS-AUD-007 | Fixed | Image encoding now writes directly to the reserved destination file instead of materializing a second full-size managed byte array and in-memory encoded stream. Incomplete output is removed on failure and cancellation. A physical 32MP/64MP peak-memory stress run is not represented by hosted CI. |
| DS-AUD-008 | Fixed | ActionOutputPolicy now handles Windows reserved device names, control characters, trailing spaces/dots, bounded stems, fallback names, and collision-safe CreateNew reservation. |
| DS-AUD-009 | Fixed | Background work now has explicit ownership or observation for startup updates, close-to-tray explanation, search/thumbnail/undo refreshes, detector grace/timeout, incoming transfer notification, pairing expiry, projection workers, and related service tasks. Cancellation is separated from unexpected failure. |
| DS-AUD-010 | Fixed | DNS-SD, logger shutdown, detector shutdown, and application teardown use async-first disposal/await paths. App shutdown cancels and awaits startup work and disposes services in an explicit order without synchronously blocking the UI on long-running background work. |
| DS-AUD-011 | Fixed | Cleanup catches were narrowed or semantically separated from operation failures. Expected cancellation, IO, authorization, platform cleanup, and shutdown races remain best-effort where appropriate; unexpected failures are logged or propagated. Path redaction remains in the logging path. |
| DS-AUD-012 | Deferred | No large behavior-changing decomposition was performed after the P0/P1 fixes. Existing MainPage/MainViewModel/Smart Drag concentration was left stable to reduce regression risk. The controlled partial/service split remains a future maintenance slice, not a release blocker for this hardening Preview. |

### Controlled cleanup findings

| ID | Status | Final disposition |
|---|---|---|
| DS-CLN-001 | Fixed | Repeated export cleanup was centralized in ActionOutputPolicy.TryDeleteIncompleteOutput. The helper only receives the output created by the current operation and never broadens into source deletion. |
| DS-CLN-002 | Fixed | Repeated settings save/revert behavior was narrowed into shared setting-application/persistence helpers while retaining per-setting validation and UI behavior. |
| DS-CLN-003 | Verified Safe | Main Page and Dynamic Island continue to use the same Quick Action registry, capability checks, parameter model, execution path, and result/error mapping. No second action implementation was introduced. |
| DS-CLN-004 | Verified Safe | Native checks remain narrow and local to their semantics. No broad NativeUtils abstraction was added, and Win10/Win11 visual branches remain separate. |

## 3. Files changed in the implementation PR

### Release and repository policy

- .agents/skills/dropspace-maintainer/SKILL.md
- .github/release-notes/v0.3.0-preview.11.md
- .github/workflows/ci.yml
- README.md
- RELEASE_VERSION
- ROADMAP.md

### Encrypted share Worker

- share-worker/README.md
- share-worker/package.json
- share-worker/src/index.js
- share-worker/test/worker.test.mjs
- share-worker/wrangler.toml.example

### App lifecycle, Smart Drag, OLE, and UI

- src/DropSpace.App/App.xaml.cs
- src/DropSpace.App/MainWindow.xaml.cs
- src/DropSpace.App/Services/DeviceHandoffService.cs
- src/DropSpace.App/Services/DragSessionDetector.cs
- src/DropSpace.App/Services/Ole/EphemeralOleDragProbe.cs
- src/DropSpace.App/Services/OleDragDropService.cs
- src/DropSpace.App/Services/OverlayWindowService.cs
- src/DropSpace.App/Services/SecureInternetShareService.cs
- src/DropSpace.App/Services/UndoCoordinator.cs
- src/DropSpace.App/Services/WindowsImageTransformService.cs
- src/DropSpace.App/ViewModels/MainViewModel.cs
- src/DropSpace.App/Views/MainPage.xaml.cs

### Core and infrastructure

- src/DropSpace.Core/Collections/SerializedProjectionRefreshCoordinator.cs
- src/DropSpace.Infrastructure/Actions/ActionOutputPolicy.cs
- src/DropSpace.Infrastructure/Actions/HashActionService.cs
- src/DropSpace.Infrastructure/Actions/QrCodeActionService.cs
- src/DropSpace.Infrastructure/Actions/ZipActionService.cs
- src/DropSpace.Infrastructure/Logging/RedactingFileLoggerProvider.cs
- src/DropSpace.Infrastructure/Network/DropLinkHost.cs
- src/DropSpace.Infrastructure/Network/DropLinkPairingService.cs
- src/DropSpace.Infrastructure/Network/WindowsDnsSdDiscoveryService.cs
- src/DropSpace.Infrastructure/Sharing/InternetShareRevokeStore.cs
- src/DropSpace.Infrastructure/Sharing/ShareUploadCoordinator.cs

### Tests

- tests/DropSpace.Infrastructure.Tests/ActionOutputPolicyTests.cs
- tests/DropSpace.Infrastructure.Tests/InternetShareRevokeStoreTests.cs

## 4. What was intentionally consolidated

- Hash, QR, ZIP, image, and metadata export failure cleanup now shares the narrow ActionOutputPolicy incomplete-output deletion helper. This is behavior-equivalent because only the explicitly created destination is passed.
- Settings handlers share the narrow persistence/revert helper instead of repeating save, restore, and error-display mechanics.
- Image output no longer copies an encoded payload into a second managed byte array before writing the reserved destination.
- ZIP export now makes archive entry names unique before writing.
- Incoming DropLink HTTP offers return after the session is created and notification is queued; the host owns and cancels the notification task instead of holding the HTTP request open for UI approval.
- Detector, pairing, projection, logger, network-host, and app-shutdown background work now has explicit task ownership and cancellation.

## 5. Suspected duplicates intentionally retained

- The native smoke path still performs the intentional double Dispose check to preserve the idempotent-disposal contract.
- QueryOnlyDataObject continues to return COM E_NOTIMPL where that is the required query-only data-object contract; it is not an unfinished implementation.
- Source-safe cleanup catches remain around temporary staging and current-operation output cleanup so the original operation error is preserved and abandoned files do not remain.
- Win10 base visual and Win11 DWM-specific branches remain separate because the platform distinction is a compatibility boundary.
- Native helpers were not merged into a global utility class merely to reduce line count.

## 6. Tests and validation

### New or expanded regression coverage

- ActionOutputPolicyTests.WindowsReservedDeviceNamesAreMadeSafe
- ActionOutputPolicyTests.LongStemsAreBoundedForCollisionAndExtensionBudget
- InternetShareRevokeStoreTests.EncryptedRevokeHandleSurvivesRestartRoundTrip
  - DPAPI-protected persistence
  - no plaintext authorization in the persisted bytes
  - restart-style restore
  - revoke-handle deletion
- Worker coordinator test: concurrent plaintext-byte reservation is atomic
- Worker coordinator test: pending first chunks consume item quota
- Worker coordinator test: revoke closes the coordinator before object deletion completes
- Worker syntax check: node --check src/index.js

### Hosted CI and release validation

The following main-branch workflows completed successfully for the merged release source commit cb3fe562cdb37f452852dcef8406471a02364d12:

| Validation | Run |
|---|---|
| Windows CI: Core tests, Infrastructure tests, WinUI x64 build, portable EXE, installer, unsigned MSIX, identity package, installer lifecycle, en-US and zh-CN smoke, Worker syntax/tests | https://github.com/airanluo-dot/DropSpace/actions/runs/33769566001 |
| DropSpace Release: release bundle, checksums, update manifest, GitHub Release publication, release/API verification | https://github.com/airanluo-dot/DropSpace/actions/runs/33769565683 |
| Official website deployment and publication synchronization | https://github.com/airanluo-dot/DropSpace/actions/runs/33770614976 |

The Windows CI matrix reported success for both en-US and zh-CN plus the Worker job. The release workflow's final verifier reported successful verification of the GitHub Release, five public assets, manifest, checksums, release API, latest-change API, and live website.

### Not separately proven by hosted CI

- Physical Windows 10 1809 / build 17763
- Physical Windows 10 22H2
- Version-specific Windows 11 manual matrix
- Mixed-DPI multi-monitor placement, negative monitor coordinates, and monitor-edge behavior
- Explorer/Desktop/provider cursor feedback and real file-drag wake behavior
- Native 1000-cycle probe residue measurement
- Full Smart Drag false-reveal and rapid cross-monitor manual matrix
- Full Quick Action parameter/error matrix under real UI interaction
- Receiver-side streaming/File System Access large-file memory path
- Deployment-specific public Worker rate limiting and abuse controls

## 7. Windows OS, DPI, monitor, and native evidence

Hosted Windows CI proves the repository's current SDK/build/test/lifecycle smoke path on the GitHub Windows runner and verifies the static compatibility scripts. It does not substitute for the requested real-system evidence.

Accordingly, Preview.11 release notes and ROADMAP explicitly mark the release CONDITIONAL until the real-system evidence package covers:

- Windows 10 1809 and 22H2
- Windows 11 supported stable versions
- 100%–200% DPI
- mixed-DPI multi-monitor placement, including negative coordinates and monitor edges
- Compact and Expanded Dynamic Island border/activation/taskbar behavior
- Smart/Classic/Disabled drag wake and false-reveal behavior
- Explorer/Desktop files and folders
- Clipboard text/image/file/folder
- native probe cleanup and shutdown residue

## 8. Known limitations and follow-up work

1. The release remains a hardening Preview, not an unconditional stable-compatibility claim, until the real OS/DPI/native evidence is collected.
2. The reference Worker requires an explicitly configured Durable Object binding and migration; deployment operators must also keep R2 lifecycle expiry and deployment-level rate/abuse policy enabled.
3. Receiver-side large-file streaming/incremental hashing remains future work; the current release focuses on the server quota/revoke boundary and CSP nonce hardening.
4. Large-class decomposition of MainPage, MainViewModel, DragSessionDetector, OleDragDropService, and SqliteItemRepository was intentionally deferred to avoid mixing structural refactoring with the release-blocking fixes.
5. Hosted CI does not provide proof of every Explorer/provider and native visual interaction cell listed in the audit plan.

## 9. Release and synchronization result

GitHub Release v0.3.0-preview.11 is public, non-draft, and marked prerelease. It contains these five expected assets:

- DropSpace-x64.msix
- DropSpace.exe
- DropSpaceSetup.exe
- SHA256SUMS.txt
- update-manifest.json

The release workflow and official website deployment both succeeded, and the workflow verifier confirmed website/API synchronization for the Preview.11 tag.

Skill sync completed and verified: the personal dropspace-codex skill was updated and pushed at commit 5723ebb, and the repository copy at .agents/skills/dropspace-maintainer/SKILL.md contains the Preview.11 durable product facts.

## 10. Final audit judgment

**Preview.11 publication: CONDITIONAL PASS.**

The requested code hardening, server-side quota/revoke closure, output safety, task ownership, release metadata, CI, packaging, release assets, website deployment, and API synchronization are complete and verified.

**Unconditional full-audit completion: BLOCKED** until DS-AUD-004 and the explicitly listed real Windows OS/DPI/monitor/native interaction evidence are completed. This is an evidence limitation, not a failed build, failed test, failed release, or failed website/API synchronization.

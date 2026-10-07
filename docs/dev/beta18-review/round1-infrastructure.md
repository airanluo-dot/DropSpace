# Beta 18 round 1 — complete Infrastructure source review

Review source: immutable `/workspace/scratch/beta18-round1`. The repository `AGENTS.md`, canonical `.agents/skills/dropspace-maintainer/SKILL.md`, App/UI reference and relevant architecture guidance were read. All assigned source was reviewed before the parent authorized the narrow live-tree fixes documented below. No tests, builds, native processes, network requests or model inference were executed.

## Confirmed findings and scoped fixes

### R1-INF-01 — Lyrics quota publication waits for a complete cache scan and eviction

Priority: **P2**. Immutable evidence: `src/DropSpace.Infrastructure/Lyrics/LyricsCache.cs:55–63,80–85,139–148,176–184,272–284`; UI caller `src/DropSpace.App/Services/SettingsApplicationCoordinator.cs:33–37,165–167`, `src/DropSpace.App/ViewModels/NativeSettingsEditor.cs:116–124` and `src/DropSpace.App/Views/Music/MusicPage.cs:152–155`.

The synchronous policy publisher takes `PolicyGate`. Background quota maintenance and cache publication hold that same lock while enumerating temporary/owned files, reading metadata, sorting/summing the entire cache and deleting victims. The cache read path also updates access metadata under the lock. Although maintenance itself uses `Task.Run`, the native settings editor resumes on its UI context and calls the synchronous publisher after saving. A settings edit can therefore freeze the window while background filesystem work finishes. This also postpones disabling/clear generation publication until the scan finishes.

**Fixed in the live tree:** directory scans, eviction, reparse validation and optional access-time updates run outside `PolicyGate`, retaining the root's existing filesystem semaphore. `Trim` returns the retained byte count. A writer rechecks the latest quota and generation under the policy fence before publication; a quota shrink repeats eviction before committing. Maintenance similarly repeats if the latest quota is below the retained count. The atomic `File.Move` remains guarded so disable/clear cannot overtake an already-admitted publication. Failed deletion still prevents a new entry from increasing usage, temporary cleanup still runs in `finally`, and disable/clear preserve their original generation semantics.

Static proof: all `Trim` calls are outside the policy lock; write publication checks `generation`, current request, disabled state, entry size and complete post-replacement quota under that lock. A read checks generation after its optional timestamp write. The guarded atomic rename remains a possible short filesystem wait; the change removes the cache-size-dependent scan/deletion wait without weakening commit ordering.

### R1-INF-02 — Cancellation after a collision rename loses the new reservation's cleanup owner

Priority: **P2**. Immutable evidence: `src/DropSpace.Infrastructure/Downloads/OutputReservationService.cs:83–105,141–155`; owner and cleanup `src/DropSpace.Infrastructure/Downloads/DownloadManager.cs:204–215,312–318,379–395`.

Both collision branches release the old marker and reserve a replacement, but they call `beforeCommit` only in the next iteration. Cancellation at the next iteration's first check throws before publishing that replacement to `DownloadManager`. Its tracked reservation and durable output path remain the released old one. Cancel cleanup removes only that old marker and reports success, leaving the replacement marker behind. Its current-process PID prevents stale-marker reclamation until a later process lifetime. Retries choose another output name and can leave further markers.

**Fixed in the live tree:** each successful replacement reservation immediately invokes the tracking/checkpoint callback before the next cancellation point. A per-reservation flag prevents a second checkpoint before its rename. The callback publishes `Work.Reservation` and the output path before awaiting persistence, so a checkpoint failure also leaves the correct owner available for retry cleanup. The existing overwrite prohibition, release-failure retention and deferred committed-marker cleanup are unchanged.

Static proof: both replacement assignments are followed by the callback with no intervening token check; loop exhaustion also occurs only after the last replacement was tracked. No cancellation or filesystem failure was injected in this pass.

### R1-INF-03 — Recent-item overlay reads execute synchronous SQLite work on the UI thread

Priority: **P2**. Immutable evidence: `src/DropSpace.Infrastructure/Data/SqliteItemRepository.cs:318–343,346–352,442–445`; warm connection `src/DropSpace.Infrastructure/Data/SqliteDatabase.cs:156–166,209–232`; UI caller `src/DropSpace.App/ViewModels/MainViewModel.cs:1151–1166`.

`QueryAsync` directly invokes `QueryPageAsync`. Once initialization is complete, connection setup, SQLite command execution and row materialization can all finish their provider “async” calls synchronously. `ConfigureAwait(false)` does not offload those calls. The overlay invokes this path on startup (`App.xaml.cs:293` → `OverlayWindowService.cs:136` → `OverlayViewModel.cs:45–47,145`) and from UI projection notifications (`OverlayViewModel.cs:441–443`). `SerializedProjectionRefreshCoordinator.cs:65,140` invokes the loader inline. The main paged projection already offloads the same repository invocation in `ItemProjectionService.cs:19–22`, but recent-item reads bypass that boundary. Slow storage or SQLite lock contention can freeze the window/island.

**Fixed in the live tree:** `MainViewModel.GetRecentSourceItemsAsync` creates the repository query inside `Task.Run`, passing the existing cancellation token. Its await retains the caller context for card/view-model creation and thumbnail coordination. The repository contract and other callers were not broadly refactored.

Static proof: the lambda invokes `QueryAsync` on the worker, rather than offloading an already-created task; UI card construction occurs after the contextual await.

### R1-INF-04 — A failed reservation-marker write is retried as another owner's collision

Priority: **P2**. Immutable evidence: `src/DropSpace.Infrastructure/Downloads/OutputReservationService.cs:43–59,141–155`.

After `CreateNew` succeeds, a disk-full or other write/flush failure leaves the new marker path present. The `IOException` filter sees that path and treats the failure as a collision. Its partial contents fail stale-marker validation, so the loop skips to another output name and can repeat up to 100,000 times, creating unusable marker files while masking the original storage error.

**Fixed in the live tree:** a local ownership flag distinguishes a failed open against another marker from failures after successful marker creation. Only the former enters collision/reclaim logic. A failure after creation attempts cleanup of that owned partial and propagates the original error. A cleanup failure remains logged by the existing marker-deletion helper; no ability to guarantee deletion on a failing filesystem is claimed.

Static proof: the flag becomes true immediately after the successful `FileStream` construction, and both collision filters require it to remain false. The post-creation failure catch has no retry/continue branch.

### R1-INF-S1 — Abandoned staging recovery runs synchronous filesystem work on startup UI

Priority: **P2**. Immutable evidence: `src/DropSpace.Infrastructure/Storage/StagingLeaseStore.cs:175–227,339–380`; direct UI startup caller `src/DropSpace.App/App.xaml.cs:228–231`.

The uncontended semaphore await completes inline, followed by complete synchronous lease enumeration/deserialization and recursive cleanup on the visible startup window's UI continuation. Crash leftovers, slow AppData or antivirus contention can freeze interaction and postpone overlay/media initialization.

**Fixed in the live tree by the Sharing/Storage reviewer:** the public method invokes the complete existing recovery core through awaited `Task.Run`. The gate, cancellation checks, durable lease retention and returned count remain in the core. No detached work was introduced. Details and complete 14-file coverage are in [the Sharing/Storage report](round1-infrastructure-storage-sharing.md).

### R1-INF-S2 — Transient staging-record read failures are quarantined as malformed data

Priority: **P2**. Immutable evidence: `src/DropSpace.Infrastructure/Storage/StagingLeaseStore.cs:188–195,383–396,437–438`.

The recovery read catch uses `IsFileFailure`, which includes ordinary `IOException` and `UnauthorizedAccessException`, and moves the record through `QuarantineMalformedLease`. A valid lease whose read temporarily fails can be renamed out of the recovery directory if rename permissions remain available. Its staged root, potentially holding Internet Share plaintext, then loses automatic recovery tracking.

**Fixed in the live tree by the Sharing/Storage reviewer:** malformed JSON/schema/path errors still enter quarantine; a separate I/O/permission catch logs the failure and leaves the lease in its recovery directory for a later attempt. The malformed-path catch explicitly handles `PathTooLongException` before general `IOException`. Cancellation matches neither catch and still propagates. `InvalidDataException` is a `SystemException`, not an `IOException`; its explicit malformed-data classification is retained. Details and static proof are maintained in [the Sharing/Storage report](round1-infrastructure-storage-sharing.md).

## Coverage

Every file in the immutable [Infrastructure scope list](infrastructure-scope.txt) was physically read from first to last line: **130 files, 25,047 lines**. Scope SHA-256: `96982e6a85d309172e60308d4ce7efbe6846694ae5193ca54c5b8fb2ddf0cb3c`. The partition owner read 116 files / 21,470 lines; the Sharing/Storage reviewer read all remaining 14 files / 3,577 lines and lists each file in its report. Truncated outputs were reread in smaller chunks. This was a full-source audit, not a diff or search-only review, and no Infrastructure modules were deferred.

| Directory under `src/DropSpace.Infrastructure` | Files | Lines |
| --- | ---: | ---: |
| Actions | 5 | 511 |
| Content | 1 | 112 |
| Data | 3 | 2,586 |
| Downloads | 17 | 1,878 |
| Logging | 1 | 349 |
| Lyrics, including both manifests | 49 | 8,353 |
| Network | 17 | 4,449 |
| Preview | 9 | 900 |
| Properties | 1 | 3 |
| Settings | 2 | 446 |
| Sharing | 4 | 1,571 |
| Storage | 10 | 2,006 |
| Updates | 9 | 1,786 |
| Project/lock metadata | 2 | 97 |
| **Total** | **130** | **25,047** |

The full review covered repository migration/recovery, transaction/write gates, retention/pin/delete ownership, pending undo deletion and payload reconciliation; download queue/bandwidth/connection limits, resource validators, segmented assembly, reservations, persistence and shutdown; cache generations/quotas, provider selection/concurrency, request identity/admission, translation publication, bounded parsing, package delivery/integrity, resident workers, native execution gates, CPU/GPU fallback and process exit ownership; authenticated LAN pairing/replay/nonces, session admission/chunks/finalization/shutdown and DNS-SD parsing; preview budgets/cache cancellation; settings migrations/recovery; sharing encryption/revocation/staging; update replicas/manifests/state/integrity/installation and logging redaction/rotation. No additional sufficiently evidenced defect was confirmed in these reviewed paths beyond the findings/observations recorded here and in the supporting report.

## Validation and limits

The fixes above received source/diff inspection and scoped `git diff --check`; no test cases or builds were run. Runtime latency, throughput, Windows locking behavior, native process termination, GPU fallback, real-model output quality, network sharing and installer behavior remain unmeasured. No runtime success is implied by static reasoning.

The Sharing/Storage report additionally records synchronous deferred-delete journal draining during `FilePayloadStore` construction as a startup/performance observation, with exact DI callers; it was not silently changed as part of the staging recovery fix. `DeviceIdentityStore` imports PFX certificates with `PersistKeySet`; possible repeated Windows key-container retention was reported as an unconfirmed lifecycle candidate, and no cryptographic flags were changed without Windows compatibility evidence. The Authenticode verifier is synchronous, but its normal update path first awaits file hashing with `ConfigureAwait(false)`; a concrete shipping UI-blocking publisher-verification path was not established, so it was not promoted to a confirmed finding.

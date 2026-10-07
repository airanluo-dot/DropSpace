# Round 1 — complete Sharing/Storage partition review

Reviewed the immutable `/workspace/scratch/beta18-round1` snapshot without edits. Every line of all 14 assigned files was read, in bounded numbered chunks, including rereading the upload coordinator's initially truncated tail. Total: **3,577 source lines**, including comments/blank lines. This partition contributes to the parent review of the entire App; it is not a separate claim of full-App coverage. After the read-only findings were reported, the parent authorized two narrow live-tree fixes in `StagingLeaseStore`, recorded below. No tests were performed by this contributor.

| Full file read | Lines |
| --- | ---: |
| `src/DropSpace.Infrastructure/Sharing/InternetShareRevokeStore.cs` | 393 |
| `src/DropSpace.Infrastructure/Sharing/NearbyShareServer.cs` | 507 |
| `src/DropSpace.Infrastructure/Sharing/ShareCryptoService.cs` | 265 |
| `src/DropSpace.Infrastructure/Sharing/ShareUploadCoordinator.cs` | 406 |
| `src/DropSpace.Infrastructure/Storage/AppStoragePaths.cs` | 83 |
| `src/DropSpace.Infrastructure/Storage/FilePayloadStore.cs` | 414 |
| `src/DropSpace.Infrastructure/Storage/LocalFileReferenceService.cs` | 296 |
| `src/DropSpace.Infrastructure/Storage/LocalStorageMetrics.cs` | 40 |
| `src/DropSpace.Infrastructure/Storage/OwnedPayloadReconciler.cs` | 171 |
| `src/DropSpace.Infrastructure/Storage/PayloadCleanupCoordinator.cs` | 98 |
| `src/DropSpace.Infrastructure/Storage/ReparseSafeFileOpen.cs` | 106 |
| `src/DropSpace.Infrastructure/Storage/ReparseSafePathPolicy.cs` | 144 |
| `src/DropSpace.Infrastructure/Storage/StagedFileImportService.cs` | 195 |
| `src/DropSpace.Infrastructure/Storage/StagingLeaseStore.cs` | 459 |

Reviewed correctness, async cancellation, serialized ownership, network transition/shutdown, staged-source custody, authenticated revoke durability, bounded streaming, reparse confinement, failed cleanup retention and allocation/work frequency. Caller checks covered `App.xaml.cs` startup/service registration, `MainViewModel` constructor, `SecureInternetShareService` create/revoke flow and clipboard payload-write call sites; these checks do not claim full reads of those App files.

## Actionable findings

### S1. Storage recovery executes synchronous filesystem work on the startup UI thread

`StagingLeaseStore.cs:175–227` awaits its semaphore, then synchronously enumerates/deserializes every lease and recursively removes each abandoned tree (`339–380`). An uncontended `WaitAsync` completes inline; `ConfigureAwait(false)` does not transfer that synchronous continuation to a worker. `App.xaml.cs:231` directly awaits this call on its WinUI startup continuation, immediately after enabling window interaction (`228`). Many leftovers, antivirus contention or a slow AppData disk can therefore freeze the visible window and delay island/music initialization.

**Fixed in the live tree:** `RecoverAbandonedAsync` now enters through `Task.Run(() => RecoverAbandonedCoreAsync(cancellationToken), cancellationToken)`. The original recovery body, gate wait/release, per-lease failure retention, cancellation checks and returned recovery count remain in the awaited private core. This moves the whole synchronous enumeration/deserialization/deletion flow to a worker even when the semaphore is uncontended. The caller still observes completion/fault/cancellation; no detached cleanup was introduced. Source inspection and `git diff --check` confirm the scoped change. No Windows timing claim is made.

Related observation: payload-store construction drains an unbounded journal before the UI window is created

`FilePayloadStore.cs:23` immediately calls `TryDrainDeferredDeletes`. That method (`218–252`, `320–330`) loads/distincts the entire intentionally uncapped journal, performs synchronous delete attempts and may rewrite/fsync all remaining segments. `App.xaml.cs:520` registers this as the singleton `IPayloadStore`; `MainViewModel` requires that interface (`MainViewModel.cs:88`) and is resolved directly on the WinUI startup continuation (`App.xaml.cs:175`) before the window is constructed. A large locked-delete backlog can stall startup. The same full drain also occurs before each new payload write (`44`), so repeatedly locked files cause repeated whole-backlog work.

Suggested scoped fix for the root's startup review: keep construction free of recovery I/O and invoke worker-owned deferred-delete recovery from the existing awaited startup payload-cleanup route. Retain durable obligations and the existing deletion gate; do not cap or drop the journal. This contributor did not change `FilePayloadStore`, as it was outside the assigned fix. The constructor/UI relationship and full-backlog scan are confirmed from source; ordinary write call-site scheduling and runtime stall duration are not claimed.

### S2. Staging recovery treats transient storage failures as malformed records

`StagingLeaseStore.cs:188–195` quarantines any exception matched by `IsFileFailure`, whose set (`437–438`) includes ordinary `IOException` and `UnauthorizedAccessException`. A valid lease that temporarily cannot be read is therefore treated like malformed JSON. If its parent permits move/delete while the record denies read (or the read fails transiently while rename remains available), `QuarantineMalformedLease` (`383–396`) moves the only tracked lease away from the recovery directory. The valid root, potentially containing Internet Share plaintext, then has no automatic recovery record.

Minimal fix: quarantine only malformed schema/path/JSON failures (`InvalidDataException`, `JsonException`, malformed argument/path cases). Log and retain ordinary I/O/permission failures for a later recovery attempt. `PathTooLongException` is an `IOException`, so classify specific invalid-path exceptions before the general I/O catch. Cancellation must continue to propagate. This requires no new recovery mechanism or broad refactor.

**Fixed in the live tree:** the recovery read path now quarantines only invalid schema/JSON/path exceptions (`InvalidDataException`, `JsonException`, `ArgumentException`, `NotSupportedException`, `PathTooLongException`). A subsequent catch logs ordinary `IOException`/`UnauthorizedAccessException` and leaves the record in its active recovery directory for retry. Specific invalid-path exceptions precede the general I/O catch. Neither catch matches cancellation; all other recovery and durable cleanup rules remain unchanged. Source inspection and `git diff --check` confirm the change; no filesystem recovery scenario was executed.

## Reviewed areas without an additional validated finding

Nearby sharing validates unique item identifiers, rejects nonprivate receivers, uses constant-time token comparison, limits receivers/shares, serves bounded ranges and preserves server ownership through late shutdown. Internet sharing stages and verifies source size/hash before upload, encrypts chunk/manifest data, persists revoke capability before sending user data, and retains failed-revoke capability. The apparent revoke-store save/delete race was not escalated: the actual UI obtains a new share identifier only after final save completes, and startup restore is awaited before normal use. Reparse/path validation and staging import cleanup retain their existing custody/cancellation rules. Local remote metadata work retains its two-operation gate until the underlying uncancellable synchronous call ends.

Actual validation here: **0 executed test cases**. The findings are static source conclusions; no App startup, network sharing, drag/drop, recovery filesystem mutation or Windows smoothness was exercised.

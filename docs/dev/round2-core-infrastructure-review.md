# Round 2 — Core and Infrastructure review

Date: 2026-10-01 (UTC). Reviewer scope: Core, Infrastructure, and their two test projects. This is an independent source review, not a repetition of the prior finding list.

## Candidate and outcome

- Reviewed baseline: local commit `316f05ec1325384241083e903924cf22e7398bcf`, tree `248321174a405d823a7dcb2a0e40f231c02a8514`; task supplied its equivalence to PR 76 remote commit `4d8d0bb1a714ebfb9a91fd62f9e8b19f8f4086a8`.
- Inventory: **304 tracked files, 37,558 lines**: Core 93 / 7,730; Infrastructure 82 / 16,756; Core tests 61 / 4,737; Infrastructure tests 68 / 8,335. Generated `bin`, `obj`, and test-result artifacts are excluded.
- One independently reproduced network resource-admission defect (R2-CI-01, P2). One confirmed AI cache-path performance/availability issue requested for cross-layer evaluation (R2-CI-02, P2); its measured end-to-end latency remains unestablished.
- No application code was edited by this reviewer. Parent-authored fixes began after the baseline test run; their status is recorded separately below.
- The existing full-song semantic blocker remains open. `round1-full-song-semantic-review.md` is the existing evidence, not a novel finding from this pass. Neither structural JSON validation nor the passing pure tests establishes translation fidelity, native Windows acceptance, publication readiness, or completion of all release rounds.

## R2-CI-01 — Read/buffer of unauthenticated request bodies precedes authentication (P2)

**Baseline evidence:** `src/DropSpace.Infrastructure/Network/DropLinkAuthenticationMiddleware.cs:88–117` calls `EnableBuffering` (64 KiB in-memory threshold) and hashes the complete request body before even reading the mandatory hash/device/nonce/auth headers at lines 119–139. Secret/trusted-peer checks occur still later. `DropLinkHost.cs:126–130` sets the per-request body cap but no aggregate request/connection admission cap. `DropLinkProtocolContract.cs` allows the clipboard Base64 envelope for a 50 MiB image: 69,970,604 bytes including its 64 KiB allowance, approximately 66.7 MiB.

**Trigger / impact:** When the optional LAN host is enabled and reachable, an unpaired LAN client can POST missing-auth requests with non-seekable bodies. They are rejected, but only after hashing and (over 64 KiB) temporary-file buffering. Many requests can hold substantial temporary disk space, IO/CPU and server resources concurrently. No paired secret is needed. This is a resource-exhaustion issue, not an authentication bypass or file-disclosure claim. Actual victim disk exhaustion and hostile network load were deliberately not attempted.

**Offline reproduction actually executed:** A temporary net10 console harness references the baseline built Infrastructure/Core assemblies and `Microsoft.AspNetCore.App`. Construct `DefaultHttpContext`, set method POST and path `/v1/clipboard`, leave all auth headers absent, and use a non-seekable counting stream of 1,048,576 zero bytes. Invoke the middleware with null secret/repository dependencies (neither is reached) and a next delegate recording whether called. Result:

```text
missing_headers status=401 bytes_read=1048576 next=False
```

The harness used no listener, sockets, external providers, or user data. Its source and output during this review are `/tmp/round2-core-proof/Program.cs` and `/tmp/round2-proof.log` (ephemeral workspace evidence, not release artifacts).

**Minimal remedy:** Validate canonical, bounded auth headers, trusted peer state, and the HMAC over the declared body hash before admitting body reads; only then bounded-read/hash and compare actual bytes. Preserve endpoint prebinding authentication and nonce replay protection, including releasing a reserved nonce for a failed body validation/cancelled attempt. Add non-queued aggregate request admission and a bounded Kestrel connection/HTTP2-stream budget. Test missing/malformed auth with a non-seekable stream and assert zero reads; retain positive, tamper, replay, cancellation, alias and max-envelope tests. Windows DPAPI-dependent positive-path tests still need Windows.

**Parent worktree patch observed:** Header/trust/HMAC-before-body, an eight-slot non-queued middleware admission gate, 32 Kestrel connections, and eight HTTP/2 streams per connection were added while this report was being written. The diff was read independently. It retains bounded actual-body verification and removes reserved nonces when authentication fails after reservation. The gate covers the entire downstream request, so eight long-running approval/finalization requests can temporarily cause 429 for other callers; this is explicit finite backpressure. Baseline test counts below must not be relabeled as verification of that later patch.

## R2-CI-02 — Cached translation still requires model and runtime verification (P2)

**Evidence:** Cross-layer call trace in `src/DropSpace.App/Services/Media/AiLyricsService.cs:101–117`: after enabled/provider/model checks, `TryBegin` is evaluated before any cache access; then `GetInstalledPathAsync`, `EnsureExecutableAsync`, and `EnsureTokenizerAsync` complete before `LyricsTranslationCoordinator.TranslateBatchesAsync` reads the cache at its lines 37–43. `AiModelPackageService.cs:28–36,184–189` opens and SHA-256 hashes the entire installed model on every such call. Catalog sizes are 1,133,080,448 bytes standard and 876,311,552 bytes compact. Runtime components are likewise verified before the eventual cache hit.

**Impact and evidence limit:** A coordinator-only cache hit does not demonstrate an App-level 200 ms cache path: the full path performs O(model bytes) disk IO and hashing first. A missing/corrupt runtime, removed model, or paused circuit also prevents the otherwise-valid cached pure-data result from being returned. No App-level latency number was measured in this review, so a specific observed SLA violation is not claimed. Whether removing a model should intentionally disable retained results is a product choice; do not silently change that policy.

**Safe cache-first remedy:** Expose a coordinator cache-read operation using the same source/provider guard, requested line IDs, target normalization, model SHA and prompt-version cache key, and `TryApply` validation. Call it inside `AiLyricsWorkLifetime.RunAsync`, after explicit AI/settings and catalog checks but before inference circuit admission/model/runtime loading. Treat a valid cache hit as pure result retrieval, not a new inference success that resets/reopens the circuit. A miss must still verify model and embedded runtime integrity immediately before their execution; a timestamp-only file trust memo is not proposed. Keep cancellation checks before result publication and maintenance drain around reads so Clear cannot be followed by a stale write. Test cached retrieval with an unavailable runtime and paused circuit, miss verification failure, target/model changes, concurrent maintenance, and the agreed model-removal behavior. Measure the whole App service path on Windows, warm and cold, rather than only the coordinator.

## AI maintenance, cancellation and privacy checks

- `AiLyricsWorkLifetime` serializes maintenance, rejects new work while maintaining, cancels tracked scopes and waits for idle before deletion/clear. Its success/failure cleanup reopens admission; dispose cancels active scopes. Tests exercise drain/new-work rejection and failure recovery. No native shutdown/kill completion was inferred from this source-level review.
- `LyricsInferenceCircuit` uses a generation to prevent old completions undoing manual resume, pauses after three failures, and does not auto-reopen on later completion. Cancellation counting depends on the App wrapper; its cancellation branch was traced and retains provider lyrics.
- Model download requires explicit consent, is serialized against delete, retains bounded resumable partials, validates 206 ranges and fixed catalog size/SHA, checks free space, imposes headers/read/total deadlines and constrains HTTPS redirects to the catalog host family. This review made no real downloads or provider calls.
- Runtime extraction trusts only embedded manifest/resources, exact executable names/source commit, sizes and hashes, and rejects reparse paths. Windows process creation binds the child to a one-process kill-on-close memory-limited job before resume; inherited handles are explicitly restricted. This is a resource boundary, not a security/network sandbox.
- Prompt files are unique, bounded, noninteractive and removed on success/error/cancellation on a best-effort basis. Abrupt OS/process death cleanup is not proven by those `finally` blocks. Native Windows job/pipe behavior remains a Windows test gate.
- Translation batches are token-counted, split without dropping IDs, retried once on structurally invalid output, and returned all-or-original. The per-song budget includes tokenizer/inference work; optional cache size failure does not discard a valid larger song. Original text/timestamps/word lists remain source-owned.
- AI cache keys include prompt version, target, model hash, metadata and time axis. Entries and total storage are bounded; clear only removes owned 64-hex JSON entries. The maintenance owner is needed for concurrent clear/write consistency.
- Settings are serialized through a gate, written to a temporary file then renamed, validate before save, and recover malformed UI settings without deleting payload/database data. Privacy migration requires both explicitly persisted legacy `ClipboardPaused` and `StartWithWindows`; mere file existence or `{}` is not consent, and explicit `PrivacyChoicesCompleted=false` is preserved. Recovery sets it false. App startup ordering/actual registration was not independently executed here.

## Other source boundaries checked

- Data: shared database writer gate, initialization/connection cleanup, schema migrations/backups and validation, parameterized writes/search, keyset cursors, durable commit/reload tails, transaction rollback, pin/retention protection, pending removal recovery, relink ownership and transactional payload-delete outbox.
- Files/storage: containment and reparse rejection, owned-only deletion and reconciliation, segmented deferred-delete durability, staging custody/cleanup, bounded copying, source preservation, external metadata timeout capacity and cache bounds. Reparse checks do not constitute a filesystem-handle sandbox against a privileged local adversary racing ancestor replacement; no such guarantee is claimed.
- Networking/sharing: bilateral pairing and trust state, bounded pending/rate/replay collections, nonce/body binding, receive admission/approval/finalization/rollback/lifetime, chunk ledger and single-flight completion, source hash/length checks, encrypted-share staging and key/AAD/wire formats, revoke persistence/reservations, Nearby HTTP disclosure/ranges/receiver counts, DNS packet bounds and private-address selection. Only R2-CI-01 is a reproduced new finding here.
- Updates: source/asset allowlists and manifest metadata, bounded readers, resumable download/hash promotion, reparse-safe state recovery, publisher trust gates, shared-operation cancellation, shutdown ownership and manual-install versus unattended trust policy.
- Actions/preview/logging: CreateNew exports, ZIP hierarchy/collision/entry budget, QR payload size, controlled payload resolution, preview source/cache bounds and generation invalidation, registry capacity, logger bounded queue/retries/emergency diagnostics and redaction.
- Core presentation/policies: state-machine/session generations, drag intent versus payload verification, reliable/lossy signal lanes, spring finite-value/bounded-step projections, placement/DPI/material policy, media timing/identity, lyric matching/parsing (bounded XML/depth/regex and explicit ends), widget bounds, settings normalization, clipboard/transfer budgets and history semantics.

## Tests actually run

Toolchain: `/workspace/scratch/1f0730cd9ddb/dropspace-beta30/tools/dotnet/dotnet`; `DOTNET_CLI_HOME=/tmp/dotnet-glow-home`; `--no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false`.

1. Core baseline: **361 passed, 0 failed, 0 skipped**; `tests/DropSpace.Core.Tests/TestResults/round2-core.trx`, `/tmp/round2-core-test.log`.
2. Infrastructure baseline (`--filter 'TestCategory!=NativeSmoke'`): **315 passed, 8 failed, 11 skipped, 334 total**; `tests/DropSpace.Infrastructure.Tests/TestResults/round2-infra.trx`, `/tmp/round2-infra-test.log`. Native live-provider tests were excluded; no real provider/network actions were performed. In-process/local test-server fixtures are part of this suite.
3. Initial Infrastructure build was blocked by a cached NU1900 vulnerability-data warning becoming an error against a read-only NuGet cache. The subsequent no-restore run used `-p:NuGetAudit=false -p:WarningsNotAsErrors=NU1900`; it is not evidence of a clean current vulnerability audit.
4. The independent counting-stream reproduction described above ran successfully against baseline assemblies.

Infrastructure failures: four DPAPI authentication fixtures (`ValidBodyHashAndHmacReachEndpointAndRewindBody`, `BodyHashMismatchIsRejectedBeforeEndpoint`, `ReplayedNonceIsRejectedAfterFirstValidRequest`, `SecretWithoutTrustedPeerStateIsRejected`); DPAPI pairing admission (`PendingPairingsAreBoundedPerRemoteAddress`); two DPAPI revoke-store fixtures (`EncryptedRevokeHandleSurvivesRestartRoundTrip`, `SaveRejectsAtCapacityAndKeepsEveryLiveHandle`); host-composition `RemoteSenderCannotApproveItsOwnOfferAndLocalReceiverCan` (expected HTTP OK not obtained on Linux). These are failures, not passes; their Windows outcome is not established by this run. Skips include Windows process/job execution, native AI runtime, Windows sharing/reparse/UNC boundaries. CI for the exact candidate was ongoing when delegated; this reviewer did not independently fetch or certify it.

## Parent-authored fixes — independent follow-up verification

At approximately 21:38 UTC, this reviewer independently read the changed middleware/host/tests and the cache-first service/coordinator/test diff. No production edits were made by this reviewer.

- Focused **21 tests passed, 0 failed, 0 skipped** after recompilation: the two new unauthenticated-body/admission regressions; all translation-coordinator tests (including the new independent validated-cache path); AI lifetime, AI cache and first-run privacy fixtures. Command used the same no-restore single-worker flags plus `--filter 'FullyQualifiedName~MissingAuthenticationIsRejectedWithoutReadingTheBody|FullyQualifiedName~ConcurrentRequestAdmissionIsBoundedAndRecovers|FullyQualifiedName~LyricsTranslationCoordinatorTests|FullyQualifiedName~AiLyricsWorkLifetimeTests|FullyQualifiedName~FirstRunPrivacySettingsTests|FullyQualifiedName~AiLyricsCacheTests'`. Evidence: `tests/DropSpace.Infrastructure.Tests/TestResults/round2-infra-fixes.trx` and `/tmp/round2-infra-fixes.log`.
- The identical independent non-seekable counting-stream harness was rebuilt against the updated assemblies. It now reports `missing_headers status=401 bytes_read=0 next=False`; output `/tmp/round2-proof-fixed.log`. This closes the specific reproduced pre-auth read behavior in the current worktree, subject to Windows positive-path/CI validation.
- Cache-first now runs after AI/settings/provider/catalog guards and before circuit admission and model/runtime verification. The coordinator validates the same keyed pure JSON result without requiring runtime/model loading. Cache misses still reach full model/runtime hashes. A hit does not reset the inference circuit. Source diff and new coordinator regression support this change; App-level Windows timing, native model execution and semantic fidelity are still not established.
- These results apply to the observed uncommitted worktree patch, **not** the immutable candidate tree listed above or an unspecified future release commit. R2-CI-01 is locally fixed/reproduced; R2-CI-02 is structurally addressed with end-to-end Windows performance still pending. The known semantic blocker is unaffected.

## Coverage method and limits

The following is an actual per-file baseline inventory, not a claim based solely on build discovery. `P` = whole production source read; `C` = project/assembly/lock configuration inspected; `T` = test entry points, data rows, assertions and platform gates reviewed, with execution evidence above; `D` = additionally read complete fixture/helper bodies in this pass. Every in-scope file is represented. Most existing test helpers were not individually re-derived or fault-injected, so this does **not** claim exhaustive line-by-line review of all test fixture internals, exhaustive execution-path coverage, or a native Windows validation. This distinction is intentional. The SHA column is the first twelve characters of SHA-256 of the baseline file, not the later working-tree patch.


### src/DropSpace.Core

| File | Lines | SHA-256 prefix | Review |
|---|---:|---|---|
| `Abstractions/IAppStringLocalizer.cs` | 50 | `d1bf54b6ed03` | P |
| `Abstractions/IFileReferenceService.cs` | 12 | `26ccaf00dfea` | P |
| `Abstractions/IItemRepository.cs` | 136 | `b9b715d496ed` | P |
| `Abstractions/ILocalStorageMetrics.cs` | 8 | `ef62bcb80bc6` | P |
| `Abstractions/IPayloadCleanupCoordinator.cs` | 8 | `4c5063402478` | P |
| `Abstractions/IPayloadCleanupRepository.cs` | 27 | `658afb2dd32e` | P |
| `Abstractions/IPayloadStore.cs` | 30 | `694a253d17be` | P |
| `Abstractions/ISettingsService.cs` | 25 | `18487938967d` | P |
| `Abstractions/IStartupRegistrationService.cs` | 8 | `ec0af2e1f2be` | P |
| `Abstractions/IUpdateServices.cs` | 76 | `1011a9c2c82a` | P |
| `Actions/ActionModels.cs` | 138 | `9cc9a9c9ab61` | P |
| `Actions/ImageSizePresetPolicy.cs` | 16 | `58d1c304b10a` | P |
| `Actions/ItemSelectionResolver.cs` | 25 | `6a490c630c91` | P |
| `Actions/QuickActionPreferencePolicy.cs` | 174 | `78269f2d0b04` | P |
| `Collections/ProjectionCollection.cs` | 77 | `74b1b64f9447` | P |
| `Collections/SerializedProjectionRefreshCoordinator.cs` | 223 | `b3304a5a1cf7` | P |
| `Compatibility/WindowsCompatibility.cs` | 95 | `fbd1579b3785` | P |
| `Content/ItemContentModels.cs` | 42 | `3db81eee471e` | P |
| `Content/ItemContentPolicy.cs` | 84 | `0ca6e7bd5877` | P |
| `Diagnostics/OperationCorrelation.cs` | 6 | `cac927f1437d` | P |
| `Displays/DisplayIdentity.cs` | 38 | `07d3b8b7b341` | P |
| `DragDrop/DragEvidence.cs` | 58 | `99d89f576fd1` | P |
| `DragDrop/DragSessionPolicy.cs` | 314 | `035b6d913e42` | P |
| `DragDrop/DragSignalQueue.cs` | 64 | `23040dce40a5` | P |
| `DragDrop/OleFileDataKind.cs` | 45 | `3fb06e4bb24c` | P |
| `DropSpace.Core.csproj` | 6 | `0fa9f11859d4` | C |
| `Island/IslandExperienceCoordinator.cs` | 65 | `4411b3721587` | P |
| `Island/IslandGeometry.cs` | 22 | `cf1e4d74324e` | P |
| `Island/IslandPageTransition.cs` | 30 | `0efe1111eff6` | P |
| `Island/IslandPresencePolicy.cs` | 29 | `9742000b5f7e` | P |
| `Lyrics/AiLyricsModelCatalog.cs` | 24 | `babf3bf734de` | P |
| `Lyrics/LyricsDisplayPolicy.cs` | 49 | `5ac0f8cc3356` | P |
| `Lyrics/LyricsGlowEnvelope.cs` | 22 | `a043b84217a4` | P |
| `Lyrics/LyricsGlowPolicy.cs` | 35 | `a3c0c88075c0` | P |
| `Lyrics/LyricsInferenceCircuit.cs` | 29 | `70f61804bf94` | P |
| `Lyrics/LyricsMatcher.cs` | 235 | `60a433d88e47` | P |
| `Lyrics/LyricsModels.cs` | 110 | `60555a7ba29c` | P |
| `Lyrics/LyricsParser.cs` | 363 | `68103f582cf0` | P |
| `Lyrics/LyricsReloadPolicy.cs` | 17 | `8a7f12c5da78` | P |
| `Lyrics/LyricsTranslationOutput.cs` | 75 | `e188e5329cb6` | P |
| `Lyrics/LyricsTranslationPolicy.cs` | 59 | `c5d3c31aaee8` | P |
| `Lyrics/LyricsTranslationPrompt.cs` | 112 | `3c2348520930` | P |
| `Media/MediaModels.cs` | 99 | `e65434f3965f` | P |
| `Media/MediaPlaybackClock.cs` | 85 | `cd47362f3c65` | P |
| `Media/MediaProcessIdentityPolicy.cs` | 12 | `83c156f4eb0d` | P |
| `Media/NeteaseEnhancementModels.cs` | 39 | `e28aa3a99634` | P |
| `Media/SpectrumAnalyzer.cs` | 65 | `bceabe7bbb65` | P |
| `Models/AppSettings.cs` | 316 | `36b51dac151f` | P |
| `Models/Capabilities.cs` | 31 | `b12ce7058d61` | P |
| `Models/DomainModels.cs` | 211 | `7b07dd7a1556` | P |
| `Models/NativeIslandSettings.cs` | 61 | `45e86b8efbd1` | P |
| `Models/NativeIslandSettingsPolicy.cs` | 60 | `59d4f72db407` | P |
| `Models/OverlayPlacementEditSession.cs` | 111 | `425c41312120` | P |
| `Models/SettingsChangePolicy.cs` | 63 | `56ab62b6eee4` | P |
| `Models/SettingsMigration14.cs` | 13 | `16435df39628` | P |
| `Models/SettingsValidationPolicy.cs` | 89 | `f89a6d833341` | P |
| `Overlay/FullscreenWindowClassifier.cs` | 41 | `89acf3a35b7f` | P |
| `Overlay/OverlayContentPose.cs` | 13 | `f259185f35f0` | P |
| `Overlay/OverlayMotionController.cs` | 345 | `8e122706df1f` | P |
| `Overlay/OverlayMotionProfiles.cs` | 236 | `65c25147b0e2` | P |
| `Overlay/OverlayPlacementPolicy.cs` | 142 | `19607c35e967` | P |
| `Overlay/OverlayRegionSignature.cs` | 105 | `062df82fd9bc` | P |
| `Overlay/OverlayStateMachine.cs` | 249 | `e11d2875f908` | P |
| `Policies/AppLanguagePolicy.cs` | 40 | `b61f073a8654` | P |
| `Policies/ConsecutiveClipboardCaptureCoordinator.cs` | 78 | `642c7fd66891` | P |
| `Policies/ContentClassifier.cs` | 158 | `8fd71949ab61` | P |
| `Policies/FingerprintService.cs` | 17 | `ab612a15c14d` | P |
| `Policies/LogRedactor.cs` | 37 | `c936bcc81dff` | P |
| `Policies/PayloadPathPolicy.cs` | 34 | `3b20a0195e1f` | P |
| `Policies/RetentionPolicy.cs` | 26 | `7e93ae1a77f9` | P |
| `Policies/SearchNormalizer.cs` | 52 | `f4ecd6b44eac` | P |
| `Preview/PreviewModels.cs` | 165 | `77c841213acc` | P |
| `Shell/ShellIntakeModels.cs` | 139 | `5cd926524bde` | P |
| `SystemActivities/SystemActivityModels.cs` | 11 | `d36da9be93b1` | P |
| `Transfer/ClipboardImageBudgetPolicy.cs` | 126 | `243f4b3e1dbb` | P |
| `Transfer/ClipboardLoopGuard.cs` | 94 | `29ea3c98bb15` | P |
| `Transfer/ClipboardPausedException.cs` | 6 | `b12427abdb8e` | P |
| `Transfer/ClipboardPropagationQueue.cs` | 75 | `465bb872c49d` | P |
| `Transfer/HandoffMessagePolicy.cs` | 122 | `d14398310e92` | P |
| `Transfer/TransferModels.cs` | 286 | `e374b62de4b0` | P |
| `Transfer/TransferPolicies.cs` | 257 | `dc7323a74a22` | P |
| `Undo/UndoModels.cs` | 16 | `c9a46513fcd2` | P |
| `Updates/DeploymentModeResolver.cs` | 23 | `6b2fb6ccc185` | P |
| `Updates/ReleaseVersion.cs` | 101 | `e507b575d225` | P |
| `Updates/UpdateChannelJsonConverter.cs` | 27 | `459aefea95ba` | P |
| `Updates/UpdateInstallerArguments.cs` | 21 | `1e7d168f51ff` | P |
| `Updates/UpdateModels.cs` | 86 | `bde80f0c3936` | P |
| `Updates/UpdateReleaseSelector.cs` | 32 | `2290b5f01733` | P |
| `Widgets/WidgetCatalog.cs` | 25 | `b38f70086559` | P |
| `Widgets/WidgetCountdown.cs` | 23 | `bd9c5730674b` | P |
| `Widgets/WidgetDataSnapshot.cs` | 4 | `9df4431fc8bf` | P |
| `Widgets/WidgetModels.cs` | 196 | `e2e60e4fda6c` | P |
| `packages.lock.json` | 6 | `1624731bf151` | C |

### src/DropSpace.Infrastructure

| File | Lines | SHA-256 prefix | Review |
|---|---:|---|---|
| `Actions/ActionOutputPolicy.cs` | 132 | `f39089019bdd` | P |
| `Actions/HashActionService.cs` | 61 | `cbd56524847e` | P |
| `Actions/ItemActionRegistry.cs` | 84 | `4c5d8b3da285` | P |
| `Actions/QrCodeActionService.cs` | 67 | `eb26518cf7e2` | P |
| `Actions/ZipActionService.cs` | 167 | `17a2d7467f9c` | P |
| `Content/ItemContentResolver.cs` | 112 | `ef88db75f67f` | P |
| `Data/DatabaseExceptions.cs` | 12 | `e92778290acd` | P |
| `Data/SqliteDatabase.cs` | 794 | `6584cf87fba0` | P |
| `Data/SqliteItemRepository.cs` | 1700 | `9ec76e7d030e` | P |
| `DropSpace.Infrastructure.csproj` | 18 | `2dbf4e8e6aa3` | C |
| `Logging/RedactingFileLoggerProvider.cs` | 349 | `9c6eabb73f3f` | P |
| `Lyrics/AiLyricsCache.cs` | 95 | `bac37cf9e2c9` | P |
| `Lyrics/AiLyricsRuntimePackage.cs` | 120 | `c3f969bb6a79` | P |
| `Lyrics/AiLyricsWorkLifetime.cs` | 77 | `3907603261a4` | P |
| `Lyrics/AiModelPackageService.cs` | 199 | `70826fc30aa3` | P |
| `Lyrics/AmllLyricsProvider.cs` | 66 | `9d7c91484ca1` | P |
| `Lyrics/KugouLyricsProvider.cs` | 62 | `fd42892920b1` | P |
| `Lyrics/LlamaCompletionRunner.cs` | 243 | `05488d659dad` | P |
| `Lyrics/LocalInferenceProcess.cs` | 47 | `ba4cf0b4bd09` | P |
| `Lyrics/LocalLrcLyricsProvider.cs` | 103 | `b862e5c91bca` | P |
| `Lyrics/LrclibLyricsProvider.cs` | 56 | `4bcf7c2ef6f2` | P |
| `Lyrics/LyricsCache.cs` | 41 | `73651632de38` | P |
| `Lyrics/LyricsHttpClient.cs` | 94 | `a7ee8fd08633` | P |
| `Lyrics/LyricsProviderRegistry.cs` | 18 | `1654cfa502bc` | P |
| `Lyrics/LyricsService.cs` | 134 | `495c2fcfa786` | P |
| `Lyrics/LyricsTranslationCoordinator.cs` | 97 | `9b78ba8a5f6a` | P |
| `Lyrics/NetEaseLyricsProvider.cs` | 107 | `17d273431ef5` | P |
| `Lyrics/QqMusicLyricsProvider.cs` | 38 | `7a6719fb0c36` | P |
| `Lyrics/WindowsInferenceProcess.cs` | 282 | `ea18b94c7721` | P |
| `Network/DeviceIdentityStore.cs` | 195 | `6a5499247030` | P |
| `Network/DeviceSecretStore.cs` | 113 | `34b59f3d7fea` | P |
| `Network/DropLinkAuthenticationMiddleware.cs` | 264 | `1fb65d1c88d3` | P |
| `Network/DropLinkChunkLedger.cs` | 48 | `cd960820e58c` | P |
| `Network/DropLinkClient.cs` | 516 | `01e00a7cc27f` | P |
| `Network/DropLinkDtos.cs` | 43 | `5b6fc61ebba0` | P |
| `Network/DropLinkHost.cs` | 1480 | `a00edcfdf379` | P |
| `Network/DropLinkNonceCache.cs` | 74 | `d77d26f30ed5` | P |
| `Network/DropLinkPairingService.cs` | 545 | `683fa8eed8fb` | P |
| `Network/DropLinkProtocolContract.cs` | 160 | `d711044d190a` | P |
| `Network/DropLinkReplayCache.cs` | 77 | `2bbe38c11055` | P |
| `Network/DropLinkSingleFlight.cs` | 28 | `894386d041b0` | P |
| `Network/FirewallCapabilityService.cs` | 34 | `6f4e98200838` | P |
| `Network/LocalNetworkInterfaceResolver.cs` | 40 | `ece4e9875a81` | P |
| `Network/ReparseSafeDirectoryEnumerator.cs` | 91 | `7b5f8a496c2a` | P |
| `Network/TransferRepository.cs` | 250 | `1fe2de3ef4cc` | P |
| `Network/WindowsDnsSdDiscoveryService.cs` | 546 | `fc7b424d9966` | P |
| `Preview/FilePreviewCache.cs` | 184 | `012e68cd119a` | P |
| `Preview/FilePreviewProviderBase.cs` | 94 | `7bd547c49d5c` | P |
| `Preview/ImagePreviewProvider.cs` | 186 | `933bdc68dc04` | P |
| `Preview/MediaPreviewProvider.cs` | 59 | `44b65e0cb01c` | P |
| `Preview/PdfPreviewProvider.cs` | 48 | `7e97d25a27ce` | P |
| `Preview/PreviewProviderRegistry.cs` | 150 | `c20c72011720` | P |
| `Preview/TextPreviewProvider.cs` | 97 | `12d593b10df3` | P |
| `Preview/UnknownPreviewProvider.cs` | 41 | `11fea71ea8df` | P |
| `Preview/UrlPreviewProvider.cs` | 41 | `acc89df72c48` | P |
| `Properties/AssemblyInfo.cs` | 3 | `49326c9c258a` | C |
| `Settings/JsonSettingsService.cs` | 307 | `f6af175594da` | P |
| `Settings/SettingsIoPolicy.cs` | 123 | `f20395394d5c` | P |
| `Sharing/InternetShareRevokeStore.cs` | 393 | `fe9be61bbe71` | P |
| `Sharing/NearbyShareServer.cs` | 465 | `504a13db0abc` | P |
| `Sharing/ShareCryptoService.cs` | 265 | `ead6a7df5423` | P |
| `Sharing/ShareUploadCoordinator.cs` | 406 | `527ce2fb1f34` | P |
| `Storage/AppStoragePaths.cs` | 79 | `0071ee2b28d0` | P |
| `Storage/FilePayloadStore.cs` | 414 | `729705a0e951` | P |
| `Storage/LocalFileReferenceService.cs` | 296 | `ae892178f0c1` | P |
| `Storage/LocalStorageMetrics.cs` | 40 | `5d7466d797d7` | P |
| `Storage/OwnedPayloadReconciler.cs` | 171 | `889b7d7039a9` | P |
| `Storage/PayloadCleanupCoordinator.cs` | 98 | `6bfd09490658` | P |
| `Storage/ReparseSafeFileOpen.cs` | 96 | `19b6ef4d4647` | P |
| `Storage/ReparseSafePathPolicy.cs` | 144 | `bb7df02f73bf` | P |
| `Storage/StagedFileImportService.cs` | 195 | `df5abb84d399` | P |
| `Storage/StagingLeaseStore.cs` | 459 | `9a5ad463d39f` | P |
| `Updates/GitHubReleaseUpdateSource.cs` | 132 | `23da99d48f6b` | P |
| `Updates/HttpUpdateDownloader.cs` | 292 | `6450b8a1eda9` | P |
| `Updates/OfficialWebsiteReleaseUpdateSource.cs` | 176 | `e73181b48403` | P |
| `Updates/ResilientUpdateSource.cs` | 123 | `e1c3df9acf3d` | P |
| `Updates/UpdateFileVerifier.cs` | 43 | `7b034e146310` | P |
| `Updates/UpdateManifestParser.cs` | 174 | `a3afdb2819a1` | P |
| `Updates/UpdateMetadataReader.cs` | 32 | `ca2ef194d5f0` | P |
| `Updates/UpdateService.cs` | 728 | `08c65d8f70db` | P |
| `Updates/UpdateStateStore.cs` | 254 | `5e94fd8891b9` | P |
| `packages.lock.json` | 69 | `8fa447458430` | C |

### tests/DropSpace.Core.Tests

| File | Lines | SHA-256 prefix | Review |
|---|---:|---|---|
| `AiLyricsSettingsTests.cs` | 72 | `811f72c26f7b` | T |
| `Beta31UpgradeRegressionTests.cs` | 41 | `5df734aaa7b5` | T |
| `BetaMigrationTests.cs` | 42 | `fb439f5e357c` | T |
| `ClipboardCaptureCoordinatorShutdownTests.cs` | 64 | `4024993dd05f` | T |
| `ClipboardImageBudgetPolicyTests.cs` | 53 | `1cd63a23aa78` | T |
| `ClipboardPeerSettingsTests.cs` | 26 | `b52524c089a5` | T |
| `ClipboardPropagationQueueTests.cs` | 79 | `a38f3d7a5226` | T |
| `ConsecutiveClipboardCaptureCoordinatorTests.cs` | 186 | `01786f40e3e8` | T |
| `ContentAndDragPolicyTests.cs` | 70 | `806368a2337b` | T |
| `DisplayIdentityTests.cs` | 26 | `b655ab495fdf` | T |
| `DragSessionPolicyTests.cs` | 355 | `864026914b1b` | T |
| `DragSignalQueueTests.cs` | 76 | `bb42c56f14d7` | T |
| `DropSpace.Core.Tests.csproj` | 17 | `e5a5f3ba66eb` | C |
| `FullscreenWindowClassifierTests.cs` | 58 | `3057c8c7d240` | T |
| `HandoffMessagePolicyTests.cs` | 37 | `1fc53dbc9cb7` | T |
| `IslandPageTransitionTests.cs` | 32 | `f5fb133cba17` | T |
| `IslandPresencePolicyTests.cs` | 118 | `ad1e2d0fba92` | D |
| `LogRedactorAuthorizationRegressionTests.cs` | 34 | `7fb03ad4d363` | T |
| `LogUserInfoRegressionTests.cs` | 15 | `ad5eb0941561` | T |
| `LyricsArtistMatchingTests.cs` | 169 | `a88afa65a735` | T |
| `LyricsDisplayPolicyTests.cs` | 91 | `314faee6504c` | T |
| `LyricsGlowEnvelopeTests.cs` | 50 | `b77eb083d267` | T |
| `LyricsGlowPolicyTests.cs` | 44 | `9dc19a08f457` | T |
| `LyricsInferenceCircuitTests.cs` | 41 | `f21ee851c96d` | T |
| `LyricsParserTests.cs` | 226 | `dc479dd48044` | T |
| `LyricsRelativeEndRegressionTests.cs` | 27 | `23f502494e32` | T |
| `LyricsReloadPolicyTests.cs` | 26 | `111cf875fb32` | T |
| `LyricsRepeatedLineRegressionTests.cs` | 18 | `27782b8096f2` | T |
| `LyricsTranslationOutputTests.cs` | 55 | `9c32cf8805d9` | T |
| `LyricsTranslationPolicyTests.cs` | 46 | `38c8d90acefe` | T |
| `LyricsTranslationPromptTests.cs` | 65 | `9a1aeb158edd` | T |
| `MalformedTransferManifestTests.cs` | 29 | `8e0725351d31` | T |
| `MediaPlaybackClockTests.cs` | 190 | `7df818964a93` | T |
| `MediaProcessIdentityPolicyTests.cs` | 16 | `1fca67963b8e` | T |
| `NeteaseEnhancementPolicyTests.cs` | 31 | `3a4881271383` | T |
| `OverlayContentPoseTests.cs` | 31 | `500703ae33f9` | T |
| `OverlayMaterialCapabilityTests.cs` | 82 | `6ff19caecf17` | T |
| `OverlayMotionCatchUpTests.cs` | 45 | `a79c65e20892` | T |
| `OverlayMotionControllerTests.cs` | 162 | `7379cea077a8` | T |
| `OverlayMotionProfileTests.cs` | 112 | `2d5b4a887390` | T |
| `OverlayPlacementEditSessionTests.cs` | 104 | `3f0f9ab8a0e2` | T |
| `OverlayPlacementPolicyTests.cs` | 191 | `9e9566b30108` | T |
| `OverlayRegionSignatureTests.cs` | 57 | `1f5d8f60d265` | T |
| `OverlayStateDedupeTests.cs` | 46 | `e8f26b7774cb` | T |
| `OverlayStateMachineTests.cs` | 177 | `621c695619f5` | T |
| `OverlayTransitionTests.cs` | 37 | `a63a00e25af6` | T |
| `PolicyTests.cs` | 219 | `be763592eda3` | T |
| `QuickActionPreferencePolicyTests.cs` | 92 | `59bc91663e53` | T |
| `RepeatedCaptureRetryAuditTests.cs` | 45 | `98d6bf8f520f` | T |
| `SerializedProjectionRefreshCoordinatorTests.cs` | 137 | `841d351b934a` | T |
| `SettingsAndImageActionPolicyTests.cs` | 69 | `358791847c13` | T |
| `SettingsUpdateCheckMergeRegressionTests.cs` | 36 | `54c2fae5ed1e` | T |
| `SettingsValidationPolicyTests.cs` | 36 | `277843fb007c` | T |
| `ShellIntakeTests.cs` | 90 | `d907a714377f` | T |
| `TransferPolicyTests.cs` | 84 | `da2628851359` | T |
| `UpdateVersionTests.cs` | 83 | `f23c6be45231` | T |
| `WidgetCatalogTests.cs` | 58 | `a754fbd59284` | T |
| `WidgetExpansionTests.cs` | 40 | `1050c18d493e` | T |
| `WidgetMovementTests.cs` | 40 | `30958f7ca614` | T |
| `WindowsCompatibilityPolicyTests.cs` | 30 | `c3b443ce9871` | T |
| `packages.lock.json` | 109 | `378b30830aa4` | C |

### tests/DropSpace.Infrastructure.Tests

| File | Lines | SHA-256 prefix | Review |
|---|---:|---|---|
| `ActionOutputPolicyTests.cs` | 100 | `ac5b8f4b47e6` | T |
| `AiLyricsCacheTests.cs` | 46 | `032b69db3d21` | D |
| `AiLyricsNativeRuntimeSmokeTests.cs` | 82 | `7e85851c4c73` | D |
| `AiLyricsRuntimePackageTests.cs` | 170 | `b4579d206a0c` | D |
| `AiLyricsWorkLifetimeTests.cs` | 37 | `366aee0173bb` | D |
| `AiModelPackageServiceTests.cs` | 167 | `a3666739dc87` | D |
| `AppStoragePathsTests.cs` | 20 | `37272a02c885` | T |
| `AuditHardeningTests.cs` | 106 | `7e68908c982e` | T |
| `ClipboardWireBudgetTests.cs` | 36 | `a219001702cb` | T |
| `DatabaseWriteBoundaryTests.cs` | 60 | `8ea71bea001f` | T |
| `DropLinkAuthenticationMiddlewareTests.cs` | 231 | `fd66d40025ac` | D |
| `DropLinkChunkLedgerTests.cs` | 28 | `768ca878abc7` | T |
| `DropLinkClientCancellationNativeSmokeTests.cs` | 135 | `8d373cf8a276` | T |
| `DropLinkNonceCacheTests.cs` | 60 | `67fca8e67cc4` | T |
| `DropLinkPairingAdmissionTests.cs` | 87 | `375ca05d3a06` | T |
| `DropLinkPairingProtocolTests.cs` | 63 | `866e3453d4ba` | T |
| `DropLinkReplayCacheTests.cs` | 33 | `4b5f0247a232` | T |
| `DropLinkSingleFlightTests.cs` | 41 | `7bcf28abe147` | T |
| `DropSpace.Infrastructure.Tests.csproj` | 18 | `a333c6ce58e6` | C |
| `FirstRunPrivacySettingsTests.cs` | 50 | `b959963db69a` | D |
| `HashActionServiceTests.cs` | 43 | `3696617c083c` | T |
| `HostCompositionRegressionTests.cs` | 144 | `2a5540872fb0` | T |
| `InternetShareRevokeStoreTests.cs` | 152 | `493f0b4cb008` | T |
| `ItemActionRegistryTests.cs` | 82 | `5d78af283a0c` | T |
| `ItemContentResolverTests.cs` | 150 | `6d63c86bc2df` | T |
| `LlamaCompletionRunnerTests.cs` | 33 | `6e0a7d4022e4` | D |
| `LocalStorageMetricsTests.cs` | 43 | `1dac3783dfe8` | T |
| `LoggingDiagnosticsTests.cs` | 99 | `74abb6d48072` | T |
| `LyricsCacheIdentityRegressionTests.cs` | 45 | `f90efb4f4c18` | T |
| `LyricsFallbackTests.cs` | 226 | `7ae27fafac8e` | T |
| `LyricsProviderStrategyNativeSmokeTests.cs` | 81 | `28207e414ee3` | T |
| `LyricsProviderTransportTests.cs` | 261 | `2ac8e8665d5c` | T |
| `LyricsTranslationCoordinatorTests.cs` | 199 | `66ce4a7403e2` | D |
| `NearbyShareRangeTests.cs` | 37 | `332e0b774ec5` | T |
| `NetworkRoundTwoRegressionTests.cs` | 282 | `07c172daf009` | T |
| `OfficialWebsiteReleaseUpdateSourceTests.cs` | 151 | `3a099fb625b3` | T |
| `OwnedPayloadReconcilerTests.cs` | 88 | `bebf5b39436a` | T |
| `PairingRoundTripNativeTests.cs` | 81 | `2235a9381a7c` | T |
| `PairingShutdownRegressionTests.cs` | 61 | `c01ebfa808dd` | T |
| `PayloadCleanupOutboxTests.cs` | 179 | `0a0026d6b4d6` | T |
| `PeerTrustLifecycleTests.cs` | 66 | `dacc08773b89` | T |
| `Preview16NetworkPolicyTests.cs` | 32 | `89930444db26` | T |
| `Preview24SettingsMigrationTests.cs` | 95 | `07a91dc6838a` | T |
| `PreviewCacheTests.cs` | 163 | `e241a9d829df` | T |
| `PreviewEdgeCaseAuditTests.cs` | 134 | `2efbae23cdde` | T |
| `PreviewRegistryConcurrencyTests.cs` | 102 | `3f79fcb1b0f0` | T |
| `QueryPagingTests.cs` | 99 | `a5b310bcd56e` | T |
| `RemoteMetadataCacheTests.cs` | 43 | `9aeedcc917a6` | T |
| `RemoteMetadataGateTests.cs` | 49 | `98df119a8f0b` | T |
| `ReparseSafeDirectoryEnumeratorTests.cs` | 80 | `613c9b9ea7ec` | T |
| `ReparseSafeFileOpenTests.cs` | 44 | `fa82d65a31da` | T |
| `RepositoryRelinkAuditTests.cs` | 262 | `736a3b5569b8` | T |
| `SettingsConcurrencyTests.cs` | 45 | `2054c8e51e59` | T |
| `ShareCapacityRegressionTests.cs` | 52 | `6b8cbdee6b30` | T |
| `ShareCleanupRegressionTests.cs` | 139 | `5c1ced2228df` | T |
| `ShareCryptoTests.cs` | 170 | `e08eb2ff997d` | T |
| `StagedFileImportTests.cs` | 135 | `8647ea62a0ff` | T |
| `StagingLeaseStoreTests.cs` | 109 | `1ed9bbf0c087` | T |
| `StorageAndRepositoryTests.cs` | 779 | `cfa38b58a563` | T |
| `UndoRepositoryTests.cs` | 246 | `2ed24c93c315` | T |
| `UpdateCoordinatorTests.cs` | 538 | `7fc47dcfb64c` | T |
| `UpdateDownloadTests.cs` | 312 | `d2cfe54bd213` | T |
| `UpdateManifestTests.cs` | 149 | `6bdde986478e` | T |
| `UpdateMetadataNullRegressionTests.cs` | 62 | `5cb3b5ad608f` | T |
| `WindowsDnsSdDiscoveryTests.cs` | 82 | `c495d1b5ad1c` | T |
| `WindowsInferenceProcessTests.cs` | 83 | `561c2a4cd34c` | D |
| `ZipActionServiceTests.cs` | 80 | `23484f256236` | T |
| `packages.lock.json` | 178 | `345fa0bdbbfc` | C |

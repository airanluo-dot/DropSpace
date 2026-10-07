# Beta18 round 3: Infrastructure source review

Source commit: `f22bcc4bdd5c2d477a7880fe02c87f6035392ab9`.
Immutable source: `/workspace/scratch/beta18-round3`.
Scope: `round3-infrastructure-scope.txt`, identical to the immutable snapshot's `scope/infrastructure-scope.txt`.

This is a new whole-source read for round 3. Prior round reports and diffs were not used as physical-read coverage or recycled as new findings. The canonical repository `AGENTS.md` and `.agents/skills/dropspace-maintainer/SKILL.md` were read. Changed and unchanged files were read first through last, using bounded consecutive chunks for long files. Every truncated output was re-read in smaller chunks before claiming coverage.

The Infrastructure owner completed 117 files / 21,745 physical lines. The root reviewer completed the separate 13 Sharing/Updates files / 3,357 physical lines, recorded in `round3-infrastructure-root.md` and incorporated below. The complete partition is 130 files / 25,102 physical lines. All 130 immutable-file SHA-256 values match `round3-source-manifest.json`, whose source commit is the value above; mismatch count is zero. Counts use byte `splitlines()` and include blank physical lines.

No production edits were made during physical review. After the root confirmed all four partitions (425 files / 85,220 lines) were fully read and explicitly authorized the two confirmed Infrastructure fixes, the changes below were implemented. No tests, fixtures, probes, builds, native execution, inference, remote mutation, or commits were performed. Actual test cases executed: **0**, against the authorized hard cap of **21** (original suite 2,124). Hash/count reconciliation is static metadata verification, not a test execution.

## New confirmed findings

### R3-INF-1 — P2: Unpaired DropLink responses have no protocol-sized memory budget

`Network/DropLinkClient.cs:43` uses `HttpClient.PostAsync` with its default `ResponseContentRead` behavior, so the entire remote response is buffered before `ReadFromJsonAsync<PairingOffer>` and before the SAS confirmation callback at line 71. `CreateClient` at lines 397–419 sets certificate fingerprint validation and a ten-minute timeout, but no `MaxResponseContentBufferSize`. The same eager response behavior is used by device discovery, pairing confirmation, authenticated JSON replies, and chunk acknowledgements at lines 27, 288, 370, and 380.

Shipping reachability was freshly corroborated by the App Services reviewer and a supplementary caller read: `DropSpace.App/Services/DeviceHandoffService.cs:103–114` passes a discovered Windows descriptor's Endpoint/Fingerprint into `client.PairAsync` after creating the pending peer row, with no response-byte budget. `WindowsDnsSdDiscoveryService.ParseAnnouncement` constructs a descriptor from LAN announcement data; its fingerprint pins the selected peer's certificate but does not establish SAS trust. A user choosing to pair with a discovered peer can therefore have that still-unpaired peer send an oversized successful HTTP body. The client allocates far beyond the 64 KiB pairing protocol budget before rejecting malformed JSON or presenting SAS, potentially exhausting application memory. The default HTTP buffer ceiling is approximately 2 GiB, not the protocol budget; the timeout does not bound bytes delivered quickly.

Minimal fix: introduce a protocol-owned response limit and apply it in `CreateClient` so all existing eager request methods fail while buffering at a small bounded size. The limit must accommodate shipping transfer status/complete responses, which include chunk indices and completed relative paths; a pairing-specific 64 KiB limit can be applied separately if changing the request helper. Keep byte enforcement before deserialization, including unknown/chunked content length and unsuccessful response bodies. No test or network execution was used to establish this finding.

### R3-INF-2 — P2: Failed receive finalization can delete a user's replacement output

`Network/DropLinkHost.cs:964–970` assembles and publishes each manifest item in sequence. `CommitItemAsync` at lines 1174–1178 moves the verified temporary file to the public `Downloads/DropSpace` destination, releases its file handle, and records only its relative path. Later items can still be assembled and hashed, and the final durable completion checkpoint can still fail. The catch at lines 991–998 calls `RollbackCompletedItems`; lines 1194–1196 re-resolve that path and delete whichever ordinary file currently occupies it. Reparse containment proves location, not the identity or contents of the published file.

Concrete shipping consequence: a first output becomes visible during a large multi-file receive. A user edits it or moves it aside and creates/replaces a file at the same name while later items are being verified. A later hash mismatch, destination collision, I/O/cancellation failure, or durable checkpoint failure then causes rollback to delete the user's updated/replacement file. The transfer held neither exclusive custody nor an identity record during this interval, so this loses data unrelated to its original temporary output.

Minimal safe fix: retain custody of the exact published files until the whole transfer commits and make rollback delete only those exact owned objects, or preserve already-published partial outputs and return their paths in the failed completion result. A path-only existence check, reparse revalidation, or a pre-delete hash check with a separate delete is insufficient to close the replacement race. Keep owned temporary assembly cleanup confined; do not broaden deletion of public destination files.

## Freshly reviewed behavior and exclusions

- Database/schema and repository operations: examined whole schema initialization, shared write admission, transactions, duplicate handling, tombstones, pin/retention guards, restore/undo, payload outbox ownership, and durable tails. No additional confirmed round-3 defect was found in these files.
- Download pipeline: examined reservation/collision ownership, redirects and request identity, range restart/fallback, bounded connection/transfer admission, idle deadlines, retries, cancellation drain, terminal cleanup, restored completion hash verification, and progress persistence coalescing. No additional confirmed round-3 defect was found.
- Lyrics/provider/cache/AI: examined all 49 files, including both manifests, every provider/parser/session/cache, progressive candidate selection, presentation versus retained work ownership, clear/preference generation fences, private package admission, optional CUDA runtime admission, CPU fallback/cooldown, model/native integrity, process/Job teardown, and whole-track language selection. These are source conclusions only; no native code or inference was executed. Experimental or obsolete adapters were read but were not promoted to shipping defects without a composition/caller chain.
- GPU selection concern excluded: the root reviewer freshly read the maintained native worker header, adaptation, and request loop. Native `choose_gpu` respects `CUDA<N>`, excludes integrated or compute-capability-below-7.5 devices, and chooses a qualified device by free memory for automatic selection; explicit devices are passed into llama parameters with split disabled. The managed any-qualified-device admission therefore does not imply a GPU-0 mismatch.
- Preview/cache/settings/storage: examined cache bounds and clear generation, provider admission/fallback, bounded source reads, UI/non-UI preference migrations and recovery, owned/reference boundaries, confined staging admission, cancelled batch cleanup, durable leases, deferred delete journal/outbox, and orphan reconciliation. TIFF/WebP formats whose quick Infrastructure probe does not know dimensions are admitted by the shipping App `ImageDecoderPreflight` before `BitmapImage` decoding, as freshly confirmed by the App UI reviewer; not a new pixel-budget finding.
- A proposed timeout-versus-finalization overwrite was not promoted: finalization has linked lifetime/deadline ownership, cancel rejects verifying state, and durable completion deliberately uses uncancelled database work. A source-only boundary race observation without a demonstrated material shipping failure is excluded.
- FastText's synchronous disposal gate wait was observed. Shutdown/disposal order and native work retirement were not established to create a material UI freeze, so this is excluded rather than counted as a finding.
- DNS browse results are retained for a caller-bounded browsing interval. No new material LAN-flood finding was established from the dictionary alone.

## Root Sharing/Updates supplement

The root freshly read all nine Updates files and all four Sharing files, 13 files / 3,357 physical lines, and confirmed 13/13 manifest SHA-256 matches in [round3-infrastructure-root.md](round3-infrastructure-root.md). No additional confirmed defect was found. Coverage included update single-flight waiter ownership, cancellation/disposal, manifest/version/hash validation, recovery/install state; nearby-server rebind, bounded late cleanup and ranged streams; secure-share staging, durable revoke capacity, crypto and upload recovery. These files are listed separately in the coverage table and are not included in the Infrastructure owner's 117-file physical-read claim. Combined coverage is complete at 130 files / 25,102 lines, with no duplicate counting.

## Implemented fixes and static verification

- **R3-INF-1 fixed:** `DropLinkProtocolContract` owns two response budgets. Device lookup and both pairing requests use a 64 KiB `MaxResponseContentBufferSize`; authenticated clients use 16 MiB + 64 KiB to accommodate bounded shipping transfer status/path replies. All existing Get/Post/Send requests retain eager buffering, so the HTTP client applies its limit before JSON deserialization and before returning even an unused acknowledgement or non-success response, including chunked responses without `Content-Length`. Responses exceeding the budget fail rather than being buffered to the default approximately 2 GiB ceiling. Source-only compatibility check: shipping transfer defaults are 8 GiB / 100 items; even the negotiated minimum 64 KiB chunk size yields about 131,072 chunk indices, comfortably below the authenticated response limit. Oversized responses under custom extreme transfer configurations will also be rejected by this protocol budget.
- **R3-INF-2 fixed:** failed finalization preserves already-published partial outputs and their `CompletedRelativePaths`; the transfer still returns `Failed` with its error. The path-only rollback helper was removed. Publication now records the path immediately after successful `File.Move`, before post-move revalidation can fail, so that failure also reports the published item. Owned unpublished assembly temporaries and confined staging retain their existing cleanup. Successfully published items stay in `Downloads/DropSpace`; a retry retains the existing no-overwrite collision behavior, so the user can inspect or move/remove partial outputs before retrying. Public outputs are not transactionally all-or-none, and no claim of filesystem atomicity is made.

The existing `FailedCompletionPersistenceRollsBackPublishedFilesAndReportsFailure` regression case was retained and updated to the safe preserved-output contract (same single case, no additional cases). It remains unexecuted under the focused budget.

An independent fresh App Services source check corroborated the rollback failure and the path-recording order, then reviewed and accepted the narrow implementation diff and both response-limit call paths. Static verification reviewed the full narrow production diff, every `CreateClient` call, response-buffer assignment, failure/snapshot state paths, publication ordering, staging retirement, and shipping sender failure handling. `git diff --check` passed. The three touched production files retained CRLF line endings. No tests, builds, native execution, network probes, or commits were run; actual test cases remain **0**.

## Limits

This review establishes source coverage and source-backed findings for the immutable commit. It does not establish Windows runtime behavior, provider availability, real file system/SMB timing, network throughput, native ABI execution, GPU compatibility under load, model quality, build correctness, packaging/signing, or deployment health. Supplementary App caller reads are evidence for reachability and guards, not additional files in the Infrastructure coverage count. The two fixes below were authorized after all four round-3 source partitions physically completed. Runtime behavior and compilation were not executed in this source-only review phase; final focused build/validation is recorded separately.

## Per-file physical coverage

`1–N` means all physical lines in the immutable file, including blank lines. Both readers have explicitly confirmed completion; SHA-256 matching is complete for every immutable row.

| File under `src/DropSpace.Infrastructure/` | Lines | Read range | Fresh reader | SHA-256 |
| --- | ---: | --- | --- | --- |
| `Actions/ActionOutputPolicy.cs` | 132 | 1–132 | Infrastructure owner | matches R3 manifest |
| `Actions/HashActionService.cs` | 61 | 1–61 | Infrastructure owner | matches R3 manifest |
| `Actions/ItemActionRegistry.cs` | 84 | 1–84 | Infrastructure owner | matches R3 manifest |
| `Actions/QrCodeActionService.cs` | 67 | 1–67 | Infrastructure owner | matches R3 manifest |
| `Actions/ZipActionService.cs` | 167 | 1–167 | Infrastructure owner | matches R3 manifest |
| `Content/ItemContentResolver.cs` | 112 | 1–112 | Infrastructure owner | matches R3 manifest |
| `Data/DatabaseExceptions.cs` | 12 | 1–12 | Infrastructure owner | matches R3 manifest |
| `Data/SqliteDatabase.cs` | 807 | 1–807 | Infrastructure owner | matches R3 manifest |
| `Data/SqliteItemRepository.cs` | 1767 | 1–1767 | Infrastructure owner | matches R3 manifest |
| `Downloads/AdaptiveDownloadScheduler.cs` | 47 | 1–47 | Infrastructure owner | matches R3 manifest |
| `Downloads/DirectFileRequestFactory.cs` | 24 | 1–24 | Infrastructure owner | matches R3 manifest |
| `Downloads/DownloadBandwidthLimiter.cs` | 61 | 1–61 | Infrastructure owner | matches R3 manifest |
| `Downloads/DownloadConnectionBudget.cs` | 54 | 1–54 | Infrastructure owner | matches R3 manifest |
| `Downloads/DownloadManager.cs` | 542 | 1–542 | Infrastructure owner | matches R3 manifest |
| `Downloads/DownloadPersistenceQueue.cs` | 221 | 1–221 | Infrastructure owner | matches R3 manifest |
| `Downloads/DownloadRequestPolicy.cs` | 37 | 1–37 | Infrastructure owner | matches R3 manifest |
| `Downloads/DownloadStorage.cs` | 65 | 1–65 | Infrastructure owner | matches R3 manifest |
| `Downloads/DownloadTaskRepository.cs` | 53 | 1–53 | Infrastructure owner | matches R3 manifest |
| `Downloads/DownloadTransferBudget.cs` | 20 | 1–20 | Infrastructure owner | matches R3 manifest |
| `Downloads/FileNameSanitizer.cs` | 43 | 1–43 | Infrastructure owner | matches R3 manifest |
| `Downloads/HttpByteRangePlanner.cs` | 29 | 1–29 | Infrastructure owner | matches R3 manifest |
| `Downloads/HttpRangeDownloader.cs` | 180 | 1–180 | Infrastructure owner | matches R3 manifest |
| `Downloads/HttpTransferDeadline.cs` | 20 | 1–20 | Infrastructure owner | matches R3 manifest |
| `Downloads/OutputReservationService.cs` | 223 | 1–223 | Infrastructure owner | matches R3 manifest |
| `Downloads/ParallelHttpFileDownloader.cs` | 214 | 1–214 | Infrastructure owner | matches R3 manifest |
| `Downloads/RetryExecutor.cs` | 62 | 1–62 | Infrastructure owner | matches R3 manifest |
| `DropSpace.Infrastructure.csproj` | 22 | 1–22 | Infrastructure owner | matches R3 manifest |
| `Logging/RedactingFileLoggerProvider.cs` | 349 | 1–349 | Infrastructure owner | matches R3 manifest |
| `Lyrics/AiLyricsBackend.cs` | 79 | 1–79 | Infrastructure owner | matches R3 manifest |
| `Lyrics/AiLyricsCache.cs` | 57 | 1–57 | Infrastructure owner | matches R3 manifest |
| `Lyrics/AiLyricsRuntimePackage.cs` | 180 | 1–180 | Infrastructure owner | matches R3 manifest |
| `Lyrics/AiLyricsWorkLifetime.cs` | 113 | 1–113 | Infrastructure owner | matches R3 manifest |
| `Lyrics/AiModelDeliveryManifest.cs` | 51 | 1–51 | Infrastructure owner | matches R3 manifest |
| `Lyrics/AiModelPackageService.cs` | 299 | 1–299 | Infrastructure owner | matches R3 manifest |
| `Lyrics/AmllLyricsProvider.cs` | 163 | 1–163 | Infrastructure owner | matches R3 manifest |
| `Lyrics/CpuInferenceMemoryPolicy.cs` | 64 | 1–64 | Infrastructure owner | matches R3 manifest |
| `Lyrics/Ct2HelperAdapter.cs` | 275 | 1–275 | Infrastructure owner | matches R3 manifest |
| `Lyrics/Ct2LyricsPipeline.cs` | 235 | 1–235 | Infrastructure owner | matches R3 manifest |
| `Lyrics/Ct2PackageInstaller.cs` | 250 | 1–250 | Infrastructure owner | matches R3 manifest |
| `Lyrics/Ct2PrivatePackage.cs` | 201 | 1–201 | Infrastructure owner | matches R3 manifest |
| `Lyrics/CudaDriverAvailability.cs` | 41 | 1–41 | Infrastructure owner | matches R3 manifest |
| `Lyrics/CudaLyricsRuntimePackage.cs` | 480 | 1–480 | Infrastructure owner | matches R3 manifest |
| `Lyrics/CudaPlainHyLyricsBackend.cs` | 76 | 1–76 | Infrastructure owner | matches R3 manifest |
| `Lyrics/FastTextLanguageIdentifier.cs` | 274 | 1–274 | Infrastructure owner | matches R3 manifest |
| `Lyrics/ILyricsSelectionRuntime.cs` | 14 | 1–14 | Infrastructure owner | matches R3 manifest |
| `Lyrics/InferenceResourcesUnavailableException.cs` | 8 | 1–8 | Infrastructure owner | matches R3 manifest |
| `Lyrics/KugouKrcParser.cs` | 159 | 1–159 | Infrastructure owner | matches R3 manifest |
| `Lyrics/KugouLyricsProvider.cs` | 175 | 1–175 | Infrastructure owner | matches R3 manifest |
| `Lyrics/KugouResponseStatus.cs` | 12 | 1–12 | Infrastructure owner | matches R3 manifest |
| `Lyrics/LlamaCompletionRunner.cs` | 369 | 1–369 | Infrastructure owner | matches R3 manifest |
| `Lyrics/LocalInferenceExecutionException.cs` | 8 | 1–8 | Infrastructure owner | matches R3 manifest |
| `Lyrics/LocalInferenceProcess.cs` | 115 | 1–115 | Infrastructure owner | matches R3 manifest |
| `Lyrics/LocalLrcLyricsProvider.cs` | 103 | 1–103 | Infrastructure owner | matches R3 manifest |
| `Lyrics/LrclibLyricsProvider.cs` | 135 | 1–135 | Infrastructure owner | matches R3 manifest |
| `Lyrics/LyricsCache.cs` | 335 | 1–335 | Infrastructure owner | matches R3 manifest |
| `Lyrics/LyricsCandidateRequests.cs` | 43 | 1–43 | Infrastructure owner | matches R3 manifest |
| `Lyrics/LyricsCandidateSelector.cs` | 107 | 1–107 | Infrastructure owner | matches R3 manifest |
| `Lyrics/LyricsDiagnostic.cs` | 32 | 1–32 | Infrastructure owner | matches R3 manifest |
| `Lyrics/LyricsHttpClient.cs` | 244 | 1–244 | Infrastructure owner | matches R3 manifest |
| `Lyrics/LyricsProviderRegistry.cs` | 22 | 1–22 | Infrastructure owner | matches R3 manifest |
| `Lyrics/LyricsRequestTrace.cs` | 98 | 1–98 | Infrastructure owner | matches R3 manifest |
| `Lyrics/LyricsService.cs` | 710 | 1–710 | Infrastructure owner | matches R3 manifest |
| `Lyrics/LyricsTranslationCoordinator.cs` | 167 | 1–167 | Infrastructure owner | matches R3 manifest |
| `Lyrics/LyricsTranslationProgress.cs` | 45 | 1–45 | Infrastructure owner | matches R3 manifest |
| `Lyrics/Manifests/fasttext-lid176-bin-v1.json` | 25 | 1–25 | Infrastructure owner | matches R3 manifest |
| `Lyrics/Manifests/models-hy-mt2-q8-v1.json` | 115 | 1–115 | Infrastructure owner | matches R3 manifest |
| `Lyrics/NetEaseLyricsProvider.cs` | 197 | 1–197 | Infrastructure owner | matches R3 manifest |
| `Lyrics/NetEaseResponseCache.cs` | 213 | 1–213 | Infrastructure owner | matches R3 manifest |
| `Lyrics/PersistentPlainLyricsRunner.cs` | 765 | 1–765 | Infrastructure owner | matches R3 manifest |
| `Lyrics/PlainHyLyricsBackend.cs` | 383 | 1–383 | Infrastructure owner | matches R3 manifest |
| `Lyrics/PlainLyricsExecutionStatus.cs` | 18 | 1–18 | Infrastructure owner | matches R3 manifest |
| `Lyrics/PlainLyricsMetrics.cs` | 49 | 1–49 | Infrastructure owner | matches R3 manifest |
| `Lyrics/PlainLyricsSegmentMemo.cs` | 38 | 1–38 | Infrastructure owner | matches R3 manifest |
| `Lyrics/QqMusicLyricsProvider.cs` | 248 | 1–248 | Infrastructure owner | matches R3 manifest |
| `Lyrics/QqMusicSession.cs` | 236 | 1–236 | Infrastructure owner | matches R3 manifest |
| `Lyrics/ResidentInferenceMemoryPolicy.cs` | 16 | 1–16 | Infrastructure owner | matches R3 manifest |
| `Lyrics/WindowsInferenceProcess.cs` | 326 | 1–326 | Infrastructure owner | matches R3 manifest |
| `Network/DeviceIdentityStore.cs` | 195 | 1–195 | Infrastructure owner | matches R3 manifest |
| `Network/DeviceSecretStore.cs` | 119 | 1–119 | Infrastructure owner | matches R3 manifest |
| `Network/DropLinkAuthenticationMiddleware.cs` | 207 | 1–207 | Infrastructure owner | matches R3 manifest |
| `Network/DropLinkChunkLedger.cs` | 48 | 1–48 | Infrastructure owner | matches R3 manifest |
| `Network/DropLinkClient.cs` | 519 | 1–519 | Infrastructure owner | matches R3 manifest |
| `Network/DropLinkDtos.cs` | 43 | 1–43 | Infrastructure owner | matches R3 manifest |
| `Network/DropLinkHost.cs` | 1482 | 1–1482 | Infrastructure owner | matches R3 manifest |
| `Network/DropLinkNonceCache.cs` | 74 | 1–74 | Infrastructure owner | matches R3 manifest |
| `Network/DropLinkPairingService.cs` | 545 | 1–545 | Infrastructure owner | matches R3 manifest |
| `Network/DropLinkProtocolContract.cs` | 160 | 1–160 | Infrastructure owner | matches R3 manifest |
| `Network/DropLinkReplayCache.cs` | 77 | 1–77 | Infrastructure owner | matches R3 manifest |
| `Network/DropLinkSingleFlight.cs` | 28 | 1–28 | Infrastructure owner | matches R3 manifest |
| `Network/FirewallCapabilityService.cs` | 34 | 1–34 | Infrastructure owner | matches R3 manifest |
| `Network/LocalNetworkInterfaceResolver.cs` | 40 | 1–40 | Infrastructure owner | matches R3 manifest |
| `Network/ReparseSafeDirectoryEnumerator.cs` | 91 | 1–91 | Infrastructure owner | matches R3 manifest |
| `Network/TransferRepository.cs` | 250 | 1–250 | Infrastructure owner | matches R3 manifest |
| `Network/WindowsDnsSdDiscoveryService.cs` | 546 | 1–546 | Infrastructure owner | matches R3 manifest |
| `Preview/FilePreviewCache.cs` | 184 | 1–184 | Infrastructure owner | matches R3 manifest |
| `Preview/FilePreviewProviderBase.cs` | 94 | 1–94 | Infrastructure owner | matches R3 manifest |
| `Preview/ImagePreviewProvider.cs` | 186 | 1–186 | Infrastructure owner | matches R3 manifest |
| `Preview/MediaPreviewProvider.cs` | 59 | 1–59 | Infrastructure owner | matches R3 manifest |
| `Preview/PdfPreviewProvider.cs` | 48 | 1–48 | Infrastructure owner | matches R3 manifest |
| `Preview/PreviewProviderRegistry.cs` | 150 | 1–150 | Infrastructure owner | matches R3 manifest |
| `Preview/TextPreviewProvider.cs` | 97 | 1–97 | Infrastructure owner | matches R3 manifest |
| `Preview/UnknownPreviewProvider.cs` | 41 | 1–41 | Infrastructure owner | matches R3 manifest |
| `Preview/UrlPreviewProvider.cs` | 41 | 1–41 | Infrastructure owner | matches R3 manifest |
| `Properties/AssemblyInfo.cs` | 3 | 1–3 | Infrastructure owner | matches R3 manifest |
| `Settings/JsonSettingsService.cs` | 325 | 1–325 | Infrastructure owner | matches R3 manifest |
| `Settings/SettingsIoPolicy.cs` | 123 | 1–123 | Infrastructure owner | matches R3 manifest |
| `Sharing/InternetShareRevokeStore.cs` | 393 | 1–393 | root reviewer | matches R3 manifest |
| `Sharing/NearbyShareServer.cs` | 507 | 1–507 | root reviewer | matches R3 manifest |
| `Sharing/ShareCryptoService.cs` | 265 | 1–265 | root reviewer | matches R3 manifest |
| `Sharing/ShareUploadCoordinator.cs` | 406 | 1–406 | root reviewer | matches R3 manifest |
| `Storage/AppStoragePaths.cs` | 83 | 1–83 | Infrastructure owner | matches R3 manifest |
| `Storage/FilePayloadStore.cs` | 414 | 1–414 | Infrastructure owner | matches R3 manifest |
| `Storage/LocalFileReferenceService.cs` | 296 | 1–296 | Infrastructure owner | matches R3 manifest |
| `Storage/LocalStorageMetrics.cs` | 40 | 1–40 | Infrastructure owner | matches R3 manifest |
| `Storage/OwnedPayloadReconciler.cs` | 171 | 1–171 | Infrastructure owner | matches R3 manifest |
| `Storage/PayloadCleanupCoordinator.cs` | 98 | 1–98 | Infrastructure owner | matches R3 manifest |
| `Storage/ReparseSafeFileOpen.cs` | 106 | 1–106 | Infrastructure owner | matches R3 manifest |
| `Storage/ReparseSafePathPolicy.cs` | 144 | 1–144 | Infrastructure owner | matches R3 manifest |
| `Storage/StagedFileImportService.cs` | 195 | 1–195 | Infrastructure owner | matches R3 manifest |
| `Storage/StagingLeaseStore.cs` | 471 | 1–471 | Infrastructure owner | matches R3 manifest |
| `Updates/GitHubReleaseUpdateSource.cs` | 132 | 1–132 | root reviewer | matches R3 manifest |
| `Updates/HttpUpdateDownloader.cs` | 121 | 1–121 | root reviewer | matches R3 manifest |
| `Updates/OfficialWebsiteReleaseUpdateSource.cs` | 176 | 1–176 | root reviewer | matches R3 manifest |
| `Updates/ResilientUpdateSource.cs` | 123 | 1–123 | root reviewer | matches R3 manifest |
| `Updates/UpdateFileVerifier.cs` | 43 | 1–43 | root reviewer | matches R3 manifest |
| `Updates/UpdateManifestParser.cs` | 174 | 1–174 | root reviewer | matches R3 manifest |
| `Updates/UpdateMetadataReader.cs` | 32 | 1–32 | root reviewer | matches R3 manifest |
| `Updates/UpdateService.cs` | 731 | 1–731 | root reviewer | matches R3 manifest |
| `Updates/UpdateStateStore.cs` | 254 | 1–254 | root reviewer | matches R3 manifest |
| `packages.lock.json` | 75 | 1–75 | Infrastructure owner | matches R3 manifest |

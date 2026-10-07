# Beta 18 round 2 — complete Infrastructure source review

Review source: immutable `/workspace/scratch/beta18-round2`, source commit `729d5b179f34ab2f852e7975f129431f77e8dc7a`. This is a fresh full-source pass after the round-1 fixes and requested integration changes. Repository `AGENTS.md`, canonical `.agents/skills/dropspace-maintainer/SKILL.md` and the App/UI reference were read. No product files, tests or fixtures were changed; no build, runtime probe, native process, model inference or network operation was executed.

## New confirmed finding

### R2-INF-01 — Declining pairing can retain the operation for the ten-minute transfer timeout

Priority: **P2**. Immutable evidence: `src/DropSpace.Infrastructure/Network/DropLinkClient.cs:70–73,81–92,215–226,275–290,394–416`; shipping caller `src/DropSpace.App/Views/MainPage.xaml.cs:614–623,629–641,2354–2363`; forwarding boundaries `src/DropSpace.App/Services/DeviceHandoffUseCase.cs:25–30` and `src/DropSpace.App/Services/DeviceHandoffService.cs:103–114`.

The user can click Cancel on the outgoing SAS confirmation dialog after the peer has returned a valid offer. That returns `false` to `DropLinkClient.PairAsync`. Before completing the local decline, the client awaits a rejection POST with `CancellationToken.None`. The same HTTP client has the ten-minute timeout used for file transfers. If the peer stops responding after the offer, declining pairing can therefore retain the pairing task, its client, handshake and derived-secret lifetime until that timeout. The final timeout is an `OperationCanceledException`, which the page's `RunAsync` silently consumes. This is an asynchronous operation-lifetime defect; the caller does not establish a UI-thread freeze or a global busy gate.

The explicit user-cancellation handlers for pairing confirmation and file transfer use the same unbounded-with-respect-to-local-cancellation notification pattern. They also exclude `OperationCanceledException` from their best-effort catches, so a notification timeout replaces the original cancellation exception. The current file-send UI does not pass a cancellation token; the concrete shipping trigger above is declining the pairing dialog. The SAS-mismatch and missing-confirmation rejection branches also use the same ten-minute notification lifetime.

**Minimal fix:** give rejection/cancellation notifications a short independent deadline, such as five seconds, while retaining the existing normal transfer timeout. A shared best-effort notification helper should observe transport failures and the notification deadline's expected cancellation, then preserve the local rejection/cancellation result. Use the helper for all three pairing rejection branches and both cancellation handlers. Await the bounded notification so its request/client ownership is still explicit. No change to bilateral-confirmation admission, certificate pinning, authentication or transfer resource ownership is needed.

Static proof: the shipping dialog's close action produces `false`; that branch passes no token to the awaited POST; `CreateClient` supplies only the ten-minute timeout. No simulated peer or timing test was run. This finding was not present in the round-1 findings or observations.

## Round-1 regression inspection

The affected modules were reread in full alongside every unchanged module. No new regression was confirmed in these fixes:

- Lyrics cache scans, eviction, reparse validation and optional access-time updates remain outside `PolicyGate`. The root filesystem gate still serializes cache I/O. Publication rechecks generation, enabled state, request fence, entry size and complete retained-byte quota under the policy fence, retrying eviction if the quota shrinks. The atomic rename retains disable/clear ordering.
- Both output-reservation collision branches hand the replacement to `beforeCommit` before another cancellation point; the manager publishes its reservation before the persistence await. Marker collision filters require that this attempt has not created the marker, so a partial marker write is cleaned up and propagated instead of becoming another naming retry.
- Staging recovery invokes the entire recovery core on an awaited worker. Malformed schema/path records remain distinct from ordinary I/O/access failures, which retain their durable lease for retry.
- The recent-item caller still creates the repository query inside `Task.Run` and resumes card construction after its contextual await. `JsonSettingsService` applies `IslandAppearanceMigration` on both normal load and recognized-version migration paths. These caller/migration paths were inspected as supporting context; this report's complete-coverage claim is limited to the Infrastructure partition.

The App/Services reviewer separately owns the new device-secret reconciliation finding. Infrastructure `DeviceSecretStore` was read in full and confirms that ordinary read/permission failures are not evidence of corrupt secret material; it does not provide a caller-side deletion safeguard. The Core reviewer separately owns mixed-language target-completeness scoring; its TTML representation and AMLL/provider/service callers were cross-checked here without duplicating the finding.

## Coverage

Every file in [the round-2 Infrastructure scope list](round2-infrastructure-scope.txt) was physically read from first to last line: **130 files, 25,093 physical lines**, by the partition reviewer. No Infrastructure source subread was delegated. Large files were read in consecutive chunks, and truncated output ranges were reread in smaller chunks. This was a complete source audit, including unchanged modules and project/lock metadata; no assigned module was deferred.

Scope SHA-256: `96982e6a85d309172e60308d4ce7efbe6846694ae5193ca54c5b8fb2ddf0cb3c`. Physical lines use Python `splitlines()`; newline counting yields 25,092 because `packages.lock.json` has an unterminated final line. All 130 source hashes match [the round-2 source manifest](round2-source-manifest.json), whose SHA-256 is `f3be1f8a232db46d8eff7d557661d3316a3da658948f4520703abe15ff137ac8`.

| Directory under `src/DropSpace.Infrastructure` | Files | Physical lines |
| --- | ---: | ---: |
| Actions | 5 | 511 |
| Content | 1 | 112 |
| Data | 3 | 2,586 |
| Downloads | 17 | 1,895 |
| Logging | 1 | 349 |
| Lyrics, including both manifests | 49 | 8,368 |
| Network | 17 | 4,449 |
| Preview | 9 | 900 |
| Properties | 1 | 3 |
| Settings | 2 | 448 |
| Sharing | 4 | 1,571 |
| Storage | 10 | 2,018 |
| Updates | 9 | 1,786 |
| Project/lock metadata | 2 | 97 |
| **Total** | **130** | **25,093** |

The complete pass covered SQLite migration/recovery, transaction and writer ownership, paged queries, retention/pin/delete behavior, undo and payload cleanup; action output/staging ownership; download queue/bandwidth/connection budgets, HTTP resource identity, range assembly, retries, reservations, persistence and exit; lyrics source/cache generations and quotas, provider concurrency and retirement, candidate selection, language/admission/publication fences, bounded parsing, package integrity and delivery, CT2 candidate isolation, resident workers, CPU/GPU fallback, native memory budgets and confirmed-exit resource ownership; LAN certificate identity, pairing secrets, authentication/replay/nonces, session/chunk/finalization ownership and DNS-SD lifetime; preview budgets/cache cancellation; settings migrations/recovery; sharing encryption/revocation/staging; update metadata replicas, version/state/integrity/installer boundaries; logging redaction/rotation; project dependencies and both model manifests.

## Validation and limits

Evidence is source inspection and immutable-snapshot hash/count verification only. This review did not modify product source or run tests, probes, builds, inference, network requests or remote actions. The proposed fix above has not been implemented or runtime-validated in this report. Windows filesystem behavior, actual process termination, GPU behavior, real-model quality, network latency, sharing throughput and installer behavior remain unmeasured.

Round-1 observations about synchronous deferred-delete journal draining in `FilePayloadStore`, possible `PersistKeySet` key-container retention and the unestablished Authenticode UI path were not relabeled as new findings. No additional sufficiently evidenced Infrastructure defect was confirmed beyond the new finding above and the separately owned cross-partition findings.

## Fix disposition

R2-INF-01 is fixed in the integrated round-2 working tree. `DropLinkClient`
now sends all three rejection notices and both cancellation notices through
`NotifyBestEffortAsync`, which owns a separate five-second cancellation deadline.
The awaited request retires before client/secret disposal. Expected transport,
malformed-response and deadline failures preserve the original local decision;
the ordinary ten-minute file-transfer timeout is unchanged. This was checked by
source and diff inspection only, not by a timing/network test. Round 3 will reread
the complete corrected Infrastructure source.

# Local development — 2026-10-13

Baseline: `461be3d`; the working tree was clean. All work is local. No version
change, remote push/PR, release, installer or real user-data mutation is authorized
or performed. This record continues independently of historical release ledgers.

## 1. NetEase discovery and controls

### Evidence and scope

The host deploys official BetterNCM/InfLink assets; this repository contains no
InfLink frontend/backend implementation. Runtime communication is exclusively
Windows GSMTC. A plugin-installed indication is an offline file/receipt check,
not evidence of a live session or successful transport controls.

There are no current application logs or NetEase processes in this Linux workspace.
The historical raw `artifacts/netease-enhancement/live-smtc*.jsonl` files are also
absent. The checked-in `netease-enhancement.md` records earlier actual
`0x8001010E` failures in metadata and pause calls, followed by successful controls
after preserving one apartment. These results are evidence about that older run,
not acceptance of this change.

Code/history establish these defects:

- `649d344` moved media-service native operations to independent thread-pool work;
  `e4d6b1b` subsequently removed the verifier's dispatcher ownership. Calls after
  asynchronous metadata reads and subscription retirement could also change
  apartments. This regresses a previously necessary, empirically validated
  constraint, but the user's present HRESULT is unavailable.
- Initial manager subscription is followed by a complete discovery request, so a
  missing initial manager event alone does not explain the report. However, an
  enumerated session whose source is temporarily empty/unreadable was discarded
  without subscribing to its own readiness events. A later session-only event
  could never wake discovery. The separate verifier had the same blind spot.
- A rejected/thrown control skipped `RequestRefresh`, leaving a disconnected
  canonical object selected. Selection could also commit a new control object
  before revision-fenced metadata publication; a command then consulted the
  previous object's playback flags and timeline.
- Ordinary control failures were Debug-only, below the shipping Information log
  threshold. The app had no command/session/HRESULT evidence for a failed skip.

There is no evidence here that InfLink misses its internal startup event, retains
its own stale callbacks, or receives and then drops a command. Those possibilities
require the real player/upstream diagnostics; they are not claimed as root causes.

### Lifecycle and implementation

1. File deployment/rollback stays in the existing managed transaction. Offline
   installation inspection remains separate from media readiness.
2. Each real media owner lazily creates one dedicated STA dispatcher. Manager
   creation, discovery, metadata/controls, artwork, event attachment/removal and
   native cancellation keep that apartment across awaits. UI waits remain bounded.
3. Subscribe before the initial full read. Keep a bounded set of discovered but
   unidentifiable sessions observed, and re-read after attachment to cover readiness
   between discovery and subscription. No added polling or fixed startup delay.
4. Candidate preparation retains the healthy primary lease until readable metadata
   is ready. Commands capture that session's snapshot under the session gate and
   reject a mismatched publication rather than using another object's state.
5. Every control completion/failure requests discovery. Definitively disconnected
   COM objects are retired and excluded while still enumerated. A failed/timed-out
   skip is never automatically replayed because it may already have executed.
6. Stop fences callbacks by generation before canceling work, retires leases and
   clears current state. Old metadata cannot publish after its lifetime ends.
   Bounded pools retain late operations and native cancellations until completion;
   only then is the apartment shut down. Repeated stop/dispose is safe. A
   permanently hung native call retains one bounded owner; it does not spawn an
   unbounded sequence of replacement threads.

Information-level diagnostics record only command sequence, operation, ephemeral
session ID, acceptance/observed state and HRESULT, plus connection/selection/
retirement milestones. They contain no song, artist, path or listening content.
`accepted` is explicitly not `StateChanged`. There is no invented plugin-side
receive/execute trace: that boundary is outside the host's public SMTC access.

### Minimal validation

Three unique focused source-linked scenarios passed using controlled Windows/WinUI
boundaries and actual service/ownership code: startup/readiness and manager events;
disable/re-enable with actual fake-publisher pause/play/previous/next effects;
stale-object/late-result recovery and native ownership (grouped as described in the
[probe README](../../scripts/media-lifecycle-probe/README.md)). There were five
scenario attempts: four passes and one fixture assertion that read the display
before the already-executed Next command's metadata publication. That assertion
was corrected to await the appropriate event, not by changing production behavior.
The native scenario was necessarily rerun after adding explicit retry of a failed
dispatcher factory; startup/recovery were not rerun without new changes.

An initial probe shim namespace error was corrected before execution. Three later
probe compilations were scoped to those fixes/new checks. One production C# subset
compile against actual Windows SDK/WinUI references passed with zero warnings or
errors. This is not an App/XAML build. Verifier late-source readiness received
compile/static review, not its own runtime test. No MSTest/full suite, Windows,
NetEase or InfLink execution occurred. See the exact
[validation record](evidence/local-iterations-2026-10-13/media-validation.md) and
[source/reference hashes](evidence/local-iterations-2026-10-13/media-validation-inputs.json).

Required real-machine follow-up remains: start DropSpace then NetEase (and reverse
order); confirm recognition without toggling; disable/re-enable the enhancement
inside NetEase; confirm actual pause/play/previous/next and advancing state. For
one failed skip, correlate the command and session IDs in
`%LOCALAPPDATA%\DropSpace\logs\dropspace.log`; distinguish HRESULT/rejection,
accepted-without-observation and observed state. Queue contents must permit skips.

## 2. Consistent local component inventory

Code review found Settings → DLC treated committed receipts plus file presence as
installed, even when Music's existing hash inspection rejected altered component
bytes. `NeteaseComponentsDlcProvider` now reuses managed-integrity verification,
reports the existing localized rollback/conflict reasons for pending or damaged
installs, and bounds inventory hashing by the existing 32 MiB per-asset limit.
Independent installations retain presence-only inspection. This does not change
which files can be removed, plugin preferences or deployment behavior.

Validation: final source review only; the native subset compile does not include
this provider. Existing
`PassiveInspectionUsesOnlyCommittedLocalBytes` already covers the underlying
pending/intact/modified-byte policy; its historical result is not a new execution.

## 3. Explicit refresh of enhancement installation state

`InspectAsync` previously cached successful inspection for the entire app lifetime,
including a negative result. Music's existing Refresh action now reconnects the
media service and explicitly rereads local enhancement inventory. Concurrent
inspection/deployment remains coalesced. No timer, download or automatic reinstall
was added.

Review caught a UI ownership consequence before verification: moving the enhancement
card between the top/bottom raises Loaded again, so simply removing the service
cache would cause repeated inspection/reordering. The card now starts inspection
once per instance; the explicit Refresh action owns subsequent inspection. Existing
localized status/errors and cancellation/lifetime guards are retained. This small
UI/inventory change received source review; WinUI behavior/compilation is unverified.

## 4. File reference and presentation lifetimes

Source review confirmed that an availability check for A could complete after Locate
replaced it with B, then write A's missing status/reason into B. Thumbnail and drag
preparation had the same late-publication problem. Main and Island hold distinct
cards, so protecting only one card instance cannot protect the stored record.

The repository now offers conditional status and Locate writes bound to the observed
revision, normalized path and entry kind. A rejected write changes neither the item
nor reference details. Entering either pending-removal path increments revision:
otherwise Remove followed by Undo restored the same identity/revision and allowed
an obsolete operation to mutate the restored record. Existing unconditional APIs,
transactional payload cleanup, Undo identity and source-file ownership are retained.

Cards fence asynchronous availability/thumbnail/drag results by presentation
generation and accept only non-older records. Reference changes clear obsolete
assets immediately; pin/last-used updates preserve valid same-content work. Optional
failed loads cannot erase usable same-content assets. Main and Island merge current
records independently. Missing records retire their cards; stale Open/Locate work
cannot act on a different replacement or revive its retired caller. Rejected Locate
requests use existing localized feedback and are not automatically replayed.

Validation: one real temporary-SQLite availability case and one production-card
source-linked delayed-publication scenario passed once each. Independent review then
found the Locate/Remove/Undo boundary above; one additional exact-name SQLite case
passed once after its repair, covering both removal entry points and valid replacement.
The unchanged card probe was not rerun. No broad suite ran. Complete Main/Overlay
WinUI compilation and real Locate/drag/drop/Undo interaction remain unverified;
caller integration received source review. Raw evidence and source boundaries are in
[file-race validation](evidence/local-iterations-2026-10-13/file-race-validation.md) and
[Locate/Undo validation](evidence/local-iterations-2026-10-13/file-relocate-validation.md).

## 5. Module inventory recovery

Current source confirms dependency validation ran against only the records already
enumerated. A valid dependent enumerated before its dependency became Faulted.
An interrupted uninstall could also remove its candidate/current package then retain
that version as installed. Startup management operations could create a slot before
recovery replaced that slot, losing its worker owner.

The implementation recovers all journals and verifies package inventory
before dependency validation/activation. It distinguishes uninstall cleanup from
retired-version cleanup and shares initialization with management operations.
Disable records startup retirement intent and immediately fences an existing owner;
queued activation cannot adopt its replacement generation. Uninstall waits for the
complete dependency check before deciding whether retirement is permitted.

Planned minimal verification, recorded before execution: one new managed-inventory
case in the existing feature-module probe, one necessary compile/run. Temporary
fixtures bind dependencies to actual enumeration order and exercise interrupted
uninstall plus initialization/management ownership. The fixture declares minimum
Windows build 0; no worker, native UI, external download or Windows compatibility
is exercised. Historical worker cases are not included in this run.

Result: the offline locked restore and one necessary probe build/run passed; the
single case completed ten assertions in approximately 173 ms. All five fixtures
were disabled, so early Disable exercised owner/barrier behavior, not a running
worker or enabled-startup suppression. Those paths received independent source
review. Physical stop remains behind initialization/slot serialization even though
Disable immediately withdraws contributions and cancels requests. See the exact
[module evidence](evidence/local-iterations-2026-10-13/module-inventory-validation.md).

## 6. Cross-device clipboard acknowledgement

The HTTP host snapshots receiver delegates before awaiting them. Disable could win
the service lifecycle gate, after which the copied receiver returned normally without
importing. The host then returned Accepted=true. A copied delegate could also become
active again after re-enable. Each subscription now captures its generation and
rejects retired calls with the existing receiver-unavailable protocol category.
Accepted event identities remain in the existing bounded TTL guard across toggles;
failed imports release their reservation so a retry can still succeed.
The matching outbound race used a copied capture delegate that read the next
session's queue. The callback now captures its own queue; exact unsubscription and
writer completion prevent an obsolete callback from entering a later queue.

Planned minimal verification before execution: one source-linked receiver lifecycle
scenario with real temporary settings and SQLite imports; only native clipboard and
peer-discovery boundaries are substituted. A cached ephemeral identity avoids DPAPI;
no server, network, protected identity persistence or system clipboard will run.
The HTTP exception mapping receives compilation/source review, not live HTTP proof.

Result: one offline restore and one Release build/run of the single receiver case
passed on the first attempt. Copied callbacks are rejected while disabled and after
re-enable; current and cross-toggle retries do not duplicate accepted events; a
precommit failure remains retryable. The real temporary SQLite database contained
exactly two accepted text rows. The host remained stopped and no protected identity
file was written. Default analyzers remain enabled, with only the standalone
fixture's documented Windows-boundary warning suppressed. HTTP acknowledgement
mapping and outbound queue ownership received source review/compilation, not live
HTTP or outbound transfer validation. See the
[receiver evidence](evidence/local-iterations-2026-10-13/clipboard-receiver-validation.md).

## 7. Honest ZIP results for selected directory links

The intake and content resolver accept directory links as readable folders. ZIP
execution then returned early for an explicitly selected linked root, yet the
outer action reported success. A single selection produced an empty ZIP; a mixed
selection silently omitted that selected item. Existing tests covered ordinary
hierarchy, names and budgets, not this path.

The selected-root path now fails through the existing ActionInvalidData feedback
and incomplete-output cleanup. Descendant links retain the existing skip policy.
Planned check: one new exact-name SQLite-independent Infrastructure test using
temporary directory links and the real action registry. It covers single/mixed
selection rejection, no residual archive, unchanged source content and ordinary
directory success with an internal link skipped. Reuse the existing restore and
do not rerun the large entry-budget case. Linux symbolic links do not establish
Windows junction behavior or atomic protection from concurrent directory swapping.

Result: the one new exact-name case passed, with no failure or skip (71 ms). It used
the real action registry and real Linux symbolic links. The existing restore was
reused, and no other ZIP case or broad suite ran. See the
[ZIP evidence](evidence/local-iterations-2026-10-13/zip-selection-validation.md).

## 8. Preview requests queued across cache clear

Adding bounded preview admission moved cache-generation capture from the public
LoadAsync entry into the eventual worker. An old text snapshot queued before Clear
could therefore acquire the post-clear generation and recreate cached old content.
Existing clear and concurrency tests exercised those concerns separately.

The narrow repair captures generation before admission and passes it through the
existing worker path. The slot count, cancellation/late-owner lifetime and provider
fallback behavior remain in scope for review. Planned check: one controlled
Infrastructure case holds both provider slots, queues an old request, clears the
real temporary cache, then confirms no old request repopulates it while a fresh
request can cache normally. No timing delay or broad preview suite is planned.

Result: the one new exact-name test passed once (94 ms runner summary; no failure
or skip), using the real text provider and file cache. Normal compilation reused
existing restore assets. The existing global Clear contract invalidates persistence;
it does not revoke a preview already requested by a caller. That return behavior
is retained, and Windows rendering/UI interaction remain unverified. See the
[preview evidence](evidence/local-iterations-2026-10-13/preview-generation-validation.md).

## Continuation

The integrated media changes are saved in local commit `0e01f77`, and file lifetime
changes in `845649f`, module recovery in `d9ba41c`, and clipboard ownership in
`0e32575`. ZIP result correctness is saved in `907ff86`. Preview cache-generation
ownership is saved in `2d89b32`. Investigation continues into remote
clipboard import cancellation and resource ownership.
Previously fixed outbox batch rotation
was confirmed in current source and existing tests, so it was not changed or retested.
The original user failures remain unverified on the target platform until the
Windows/NetEase observations above are available. Do not infer success from C#
compilation or controlled publishers. No background execution is implied after
an active session ends.

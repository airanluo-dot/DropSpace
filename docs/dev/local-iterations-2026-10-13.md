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
compile against actual Windows SDK and Windows App SDK dispatcher references
passed with zero warnings or errors. It did not use the Microsoft.WinUI XAML
reference assembly and is not an App/XAML build. Verifier late-source readiness received
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

## 9. Canceled remote image preparation

Normal remote-import exceptions already release both the capture commit gate and
bridge lifecycle gate. The uncovered boundary is native completion: dimension
preflight correctly keeps its stream until `NativeAsyncLifetime` observes actual
completion and any in-flight Cancel call. Awaiting that full owner directly inside
ImportRemote also kept both gates after the HTTP caller canceled. This follows
from the existing cancellation contract; no real codec hang has been observed here.

Dimension preparation now has one retained owner and a separate bounded/cancelable
caller wait. Persistence remains in the foreground import, with fresh cancellation
and shutdown checks, so late dimensions cannot create payloads or database rows.
Capacity and deadline failures use the existing rejection path.

The one planned, controlled ownership scenario passed on its only run, reusing
restore assets for one necessary probe build. It confirmed cancellation returns
while the native input and sole slot remain owned through actual completion and a
blocked Cancel; excess reads never start; late results stay canceled; fresh work
returns with its slot already released. The existing probe analyzer configuration
was retained. ImportRemote/bridge gate release, persistence exclusion and deadline
mapping received independent source review, not dynamic coverage. Full WinUI/image
codec and HTTP interaction remain unavailable here. See the exact
[image lifetime evidence](evidence/local-iterations-2026-10-13/remote-image-lifetime-validation.md).

## 10. Pin persistence and removal Undo ordering

Pinning previously finalized the old Undo, awaited a separate repository write,
and only then registered its own Undo. SQLite deliberately runs this write on a
worker. Main and Island button handlers do not serialize that three-step operation,
so a later Remove could create its Undo while Pin was suspended. The earlier Pin
continuation then finalized that newer removal, prematurely deleting its history
row/owned payload and replacing the expected removal Undo. Original user files
remain outside this deletion path.

The shared Undo gate now covers pin finalization, persistence and immediate Undo
registration. Both Main/Island single-item and batch pin paths use this boundary.
Once the repository reports a successful commit, caller cancellation cannot
interrupt registration of the actual previous states. Failed or unchanged writes
do not create a pin Undo. The existing behavior of finalizing a prior operation
before attempting a new one remains unchanged.

Planned minimal check: one source-linked production Undo/Pin scenario with actual
temporary SQLite, gating the return from an already-committed pin. Queue Remove,
cancel the pin caller after commit, release the pin result, and confirm the later
removal owns the visible Undo and can still restore its row. No native UI or broad
Undo suite will run. Independent source review passed.

Result: the one combined scenario passed on its only run, with one offline-cache
restore and one normal build/run retaining default analyzers. Actual SQLite A was
already pinned at the controlled return barrier; Remove had not entered its
repository call. Postcommit cancellation still published PinChange then RemoveItem.
The newer removal token remained current, Undo restored B, and A remained pinned.
The final temporary database contained both rows. Main/Island clicking and full
WinUI/XAML compilation remain unverified. See the exact
[pin/Undo evidence](evidence/local-iterations-2026-10-13/pin-undo-validation.md).

## 11. Download startup recovery preserves admitted work

App startup intentionally runs download-history restoration in the background so
slow directories cannot block the window. The download panel can enqueue work
before that restoration acquires the manager's action gate. Enqueue persists the
new identity and starts its run; subsequent restoration then reads that same
identity from the journal. It previously replaced the live Work with a snapshot-only
Work, losing the original run, cancellation source and reservation owner. Later
Cancel could clean files for the replacement without canceling the actual run.
The action gate serialized calls but did not prevent this valid Enqueue-before-
Restore ordering.

Restoration now keeps every already-owned identity, including invisible pending
cleanup, and only registers unseen IDs. Cold history and cleanup recovery retain
their previous logic. The fix is specific to normal-file DownloadManager; optional
components share the underlying downloader/budgets but have separate operation
owners. Existing UI progress ordering guards were confirmed and left unchanged.

Planned check: one exact Infrastructure test holds the downloader's transfer slot,
admits a live task, restores its journal, and cancels the original run while still
holding that slot. It also covers a separate cold history entry. There will be no
external HTTP transfer or broad download suite; actual native download-panel
interaction remains a Windows verification gap.

Result: that exact case passed once (336 ms runner summary; no failure or skip),
reusing restore assets for one normal targeted compilation. The same Work, Run,
cancellation source and reservation survived recovery. Cancel settled that run
and removed its marker; the separate cold record remained Paused. No listener or
HTTP connection started. Hidden cleanup ownership received source review rather
than an additional case. See the
[download recovery evidence](evidence/local-iterations-2026-10-13/download-restore-validation.md).

## 12. Image exports retain their reserved output handle

Hash, QR and ZIP keep their CreateNew output stream through completion. The native
image transform instead immediately closed that reservation and awaited two
path-based WinRT open operations. The output path could identify a replacement
file before encoding began. This is a demonstrated ownership gap in code, not an
observed real-world replacement incident or a claim about malicious filesystem
mutation.

The narrow change keeps the original exclusive read/write CreateNew stream and
passes its random-access adapter directly to the encoder. Existing callers retain
their write-only default; encoding formats, dimensions, metadata behavior, memory
budgets and cancellation semantics are unchanged. This removes the asynchronous
reopen window, not every possible external mutation around cleanup.

Planned verification: independent source review and one necessary C# subset compile
against the actual pinned Windows SDK reference assemblies. No mock codec scenario
or repeated action suite is planned. Actual WinRT encoding, output-format acceptance
and Windows filesystem sharing behavior cannot execute in this Linux workspace.

Result: one offline-cache restore and the single actual-SDK subset compilation
succeeded, with zero warnings/errors in 9.45 seconds. It compiled the real image
service and preflight helpers against SDK 10.0.20348.57, with default analyzers and
warnings-as-errors, confirming the real random-access adapter/encoder API types.
Zero functional scenarios ran. Full App/XAML compilation and native image encoding
remain unverified. See the
[image output compilation evidence](evidence/local-iterations-2026-10-13/image-output-native-validation.md).

## 13. Honest pause feedback after settings failures

The Main pause toggle changes visually before persistence. Capture correctly keeps
recording if Pause cannot save, but the ordinary IO failure path only showed an
error and did not reset that toggle. It could therefore display Paused while
recording remained active. The settings service also performs a real follow-up
read after a successful transition, so blindly restoring the previous checkbox
would be wrong when Resume committed but that read failed.

The narrow repair is in MainViewModel's pause failure path. While still holding the
existing settings publication gate, it reads capture's actual paused state on the
UI dispatcher, changes only that setting field and notifies the existing Settings
subscribers. If the field is unchanged it explicitly notifies anyway, resetting the
initiating control. Changed fields use the normal setter without duplicate events.
The original error still reaches existing feedback; successful operations are
unchanged. Main, tray, widget and Island consumers use the existing notification.
The control-sync flag prevents the visual correction from starting another save.

Verification: independent control-flow/consumer review and whitespace/diff check
only. Zero builds or functional scenarios ran; a substitute helper test would not
exercise this native UI path. Real WinUI interaction under a refused settings write
and a postcommit read failure remains unverified. Ordinary settings-field merging,
late permission-response guards and timestamp/pause publication locking were
already present, so they were not changed or retested. Shutdown ownership and
postcommit read optimization remain observations, not additional claimed defects.

## 14. Withdraw assets belonging to discarded Main cards

A Main page can contain 200 cards. Its background asset requests previously used
only the ViewModel lifetime token; a successful search replacement, navigation to
Settings/Music, or live-history eviction did not cancel the old cards' work.
Discarded cards therefore continued through file checks, drag preparation and
thumbnail queues. Finite delayed IO is enough to retain unnecessary waiters and
delay current requests. No measured latency or permanent leak is claimed.

The repair gives each Main card's asset request an independent cancellation owner.
Only an accepted replacement withdraws visible work; a failed/stale query leaves
the current cards intact. External Island/recent-item requests and explicit batch
drag keep their own lifetimes. Existing content-generation and repository guards
remain in force. Drag storage resolution also needs actual-native ownership:
its old cancellable projection could release the eight-slot gate before native
completion. Routine navigation cancellation must not activate that latent flaw.

Planned verification is independent integration review and one controlled scenario
using the real asset-token owner, drag service and native-lifetime helper. It fills
the native slots, withdraws old active/queued callers, and verifies that a new
request waits for actual completion and then recovers. Main's UI wiring is covered
by source review; actual WinUI navigation/drag remains a Windows verification gap.

Result: the single opt-in `drag-native` scenario passed once, with one normal
source-linked compilation using existing restore assets. Nine old callers retired;
the queued ninth lookup never started, all eight native slots remained occupied
until actual completion, and fresh work then recovered with all slots returned.
No previous media/image scenarios were rerun. Main's integration received two
independent source reviews; no Main/App/XAML compile or native navigation ran.
See the [asset cancellation evidence](evidence/local-iterations-2026-10-13/asset-drag-native-validation.md).

## 15. Keyboard focus after opening Quick Panel by hotkey

The product describes Quick Panel as keyboard-first, but the global hotkey only
expanded it. Its normal show path uses `SW_SHOWNOACTIVATE`; the sole explicit
`Activate()` was in the mouse-open path. Removing the no-activate style permits
activation but does not request it. The hotkey therefore lacked the step needed
to transfer keyboard input from the previously focused application.

The hotkey now requests activation from the actual active, visible and safe
expanded surface after synchronous monitor routing. It focuses the current page's
first available control. A first show may await Loaded and at most one subsequent
layout notification; these callbacks never reactivate the window. Page/hide
generation checks and retirement on deactivation, hide, native failure, placement
editing and shutdown prevent stale focus requests. Fullscreen activation policy
still applies, and passive presentation/Smart Drag do not invoke this entry point.

Verification: independent source review of routing, focus ownership and the
existing Escape handler, plus diff checks. Zero builds or functional tests ran.
The cache lacks the actual Microsoft.WinUI reference assembly, so no actual WinUI
metadata/type verification is claimed. A large substitute window harness would
not establish native foreground or keyboard behavior. First-show focus, Tab/Escape,
multiple displays and fullscreen policy still need Windows interaction; the source
repair is not represented as target-platform acceptance.

## 16. Bound DropLink source traversal before sending

DropLink counted regular files and their bytes, but directories entered its work
stack and visited set without a traversal budget. Many empty directories could
therefore consume preparation work without approaching either existing limit.
The separate Nearby/Internet sharing enumerator already has its own entry/depth
limits and is unchanged. No measured UI stall or guaranteed immediate cancellation
of synchronous filesystem IO is claimed.

The selected repair adds one shared node budget across the entire selection:
`MaxItems + 4096`, including roots, directories, files and skipped links. This is
a new local preparation policy, not an existing DropLink requirement or a strict
process-memory limit. It preserves the configurable 10000-file ceiling. Ordinary
directories, including empty ones, use the configured manifest-relative path limit
before enqueueing; no new fixed depth limit is introduced. File path normalization
also needs that configuration, which the old enumerator omitted. Exceeding either
new preparation check aborts the whole selection before hashing or offering it.

One typed error maps to a translated corrective message in all ten UI languages.
Independent wording review also found transportation-specific Korean/Russian
translations in the reused failure title; only those two existing title entries
are included in this round's explicit translation review.

Planned verification is one real-filesystem composite in the existing enumerator
test class, focused on shared selection ownership, empty-directory paths and
valid deep/configured-length paths, plus one scoped localization integrity check.
No network transfer, existing enumeration suite or Windows GUI run is planned.
Outbound transfer cancellation, progress and disable/shutdown ownership remain a
separate, confirmed follow-up; this node budget does not solve those lifetimes.

Result: the single Infrastructure composite passed once (56 ms runner summary),
with one normal targeted compilation and no restore or repeated scenarios. The
small shared budget, empty-directory path rejection, real 40-level path accepted
at 2048/rejected at the default 1024, and pre-canceled admission all executed.
The nine explicit translation-review commands and one App integrity check also
succeeded (10 languages, 887 IDs); these are not functional test cases. Windows
long paths, MainPage's real dialog and network transmission remain unexecuted.
See the [handoff preparation evidence](evidence/local-iterations-2026-10-13/handoff-enumeration-validation.md).

## 17. Retire explicit handoff sends across service switches

File and text sends previously forwarded straight from DeviceHandoffService to
the shared client. Disabling or disposing the service stopped discovery and the
receiving host, but did not cancel those outgoing requests. Main's calls supply
no cancellation token, and repeated actions had no shared capacity limit. The
clipboard bridge also uses the client independently, so imposing the handoff
switch on the entire client would break a separate feature.

The repair is scoped to the service's two explicit send methods. A successful
enable owns a new admission identity; disable, rollback and disposal withdraw it.
At most two actual sends may remain active or retiring across all identities, with
immediate feedback when capacity is occupied. Caller cancellation and native/IO
completion remain separate: abandoned work must retain its slot and cancellation
resources through actual completion. Old service results and progress reports are
fenced. A third-party Progress<T> consumer's already-posted UI callbacks cannot be
retracted by this service; a future progress UI must fence its own consumption.
Cancellation also cannot undo a remote commit that already completed.

Busy feedback has ten offline translations; the reused Russian success title is
corrected to describe data transmission. Planned validation is one source-linked
owner scenario with blocked operation/cancellation boundaries, plus independent
service/UI review. The App localization integrity check ran once after explicit
review of the changed IDs (10 languages, 888 IDs); its log is retained. No actual
network send is planned for these checks, and Windows interaction is unavailable.
The visible progress/cancel workflow is a separate next iteration, not claimed
as implemented by this service-lifetime repair.

Final review also traced a concrete feedback gap: the client's ten-minute HTTP
timeout can throw TaskCanceledException, while MainPage treats any cancellation
as a quiet exit. Only a provider cancellation observed while this send's own
tokens and admission identity remain current is now wrapped in IOException,
preserving the original exception. Intentional caller/service cancellation stays
cancellation; the code does not label every provider cancellation a timeout.

Result: the single controlled ownership composite passed on its first run after
one offline-cache restore and normal compilation with default analyzers. It
exercised retained slots through actual work and blocked cancellation callbacks,
disable/re-enable, stale progress, fresh recovery, original failure propagation,
and provider cancellation versus intentional cancellation. Five actual owners
retired with none left. This did not run a real client, peer, Windows UI or full
App/XAML compilation. Independent service and UI reviews passed. See the
[send lifetime evidence](evidence/local-iterations-2026-10-13/handoff-send-lifetime-validation.md).
The feature catalogue's stale claim that historical Beta 28 is the current
release was also replaced with a pointer to RELEASE_VERSION; no version changed.

## 18. Give file sends a persistent progress and cancellation surface

The existing file action waits for the entire transfer and then opens a result
dialog. It exposes neither progress nor cancellation, despite the client already
reporting acknowledged chunk bytes. MainPage is replaced when the interface
language changes, so keeping this operation in page/dialog lifetime would lose
observation or cancel the transfer for a presentation change.

The selected implementation keeps bounded in-session file-send snapshots in the
existing singleton use-case boundary, with a native nonmodal Flyout in Main's
navigation footer. Preparation, approval, sending and finalization come from
actual client boundaries. Only a Completed response means success. A narrow
actual-retirement notification joins caller outcome with real work/cancellation
completion; cancellation stays visible until both facts are known. Finalization
stops accepting the cancel action, and cancellation after network admission must
state that the remote result can be uncertain. No persisted history, automatic
retry, new protocol or new setting is planned.

Source review also confirmed that the offer POST sits outside the client's
existing cancellation cleanup block. If the receiver registers an offer before
the sender observes its canceled response, no cleanup request is attempted. The
manifest session ID is already known locally; moving this request inside the
existing block allows one bounded best-effort cancel without waiting for its
response to reveal that ID. Cross-request ordering can still let cancellation
arrive first, so this cannot guarantee remote rollback.

Cancellation and finalization also require synchronous arbitration. The tracker
marks its own cancellation token while deciding the phase under one lock; the
send owner propagates that decision when a progress callback returns, including
the branch that suppresses an obsolete scope's report. The client rechecks its
work token after the finalization report and before the commit request. This
closes a window in which queued cancellation callbacks had not marked the actual
work token yet. Already-retired progress callbacks stay ignored.

Result: the new tracker/lifetime composite passed on its first actual execution.
There were two compilation attempts: the first stopped on a probe lambda
parameter named `scoped`, which conflicts with C# syntax; only that parameter was
renamed, and the necessary retry compiled and ran successfully. No production
code or assertions were weakened. Existing restore assets and default analyzers
were used; the unchanged round-17 scenario did not rerun.

Fourteen actual controlled sends and one disabled admission exercised the two
outcome/retirement orders, exactly-once retirement notifications, phase snapshots,
observer reattachment, both cancel/finalization decisions, late progress, the
two-active/ten-finished bounds and clearing history without losing active work.
This is a controlled sender boundary, not actual HTTP submission. Independent
production/UI review passed; nine explicit reviews of the twenty new resource
IDs and one App integrity check passed (ten languages, 908 IDs). See the
[tracked-send evidence](evidence/local-iterations-2026-10-13/handoff-tracked-validation.md).
Actual Windows UI, keyboard/focus, language replacement and two-device transfer
remain target-platform verification gaps. No full App/XAML compilation ran.

## 19. Respect cancellation and checkpoint failures before download publication

OutputReservationService checks cancellation before committing, but its initial
beforeCommit callback then awaits DownloadManager's durable journal checkpoint.
The old path moved the staging file immediately after that await, even when the
user canceled during the checkpoint. The manager subsequently observed Completed
and correctly declined to delete the delivered file; the missing boundary check
was before delivery, not a reason to roll completed output back.

The initial callback also sat inside the IOException collision handler. If a
different process created the destination while the checkpoint failed, that
storage error could be mistaken for a name collision and replaced by a retry.
The selected repair moves checkpoint execution outside that handler and checks
cancellation immediately before File.Move. Only actual publication IO remains in
the collision-recovery block. Existing marker handoff, cancellation cleanup,
pause retention and post-publication success semantics remain intact.

One real-filesystem composite is being prepared: cancel during the callback,
preserve staging and its reservation; surface an original checkpoint error even
when an external destination appears; then successfully retry by reserving another
name without changing the external file. Manager control/cleanup integration is
reviewed statically, with no network, WinUI, or unchanged test suite planned.

Result: the one Infrastructure composite passed once (54 ms runner summary,
30.7312 ms in TRX), using existing restore assets and one normal targeted build.
No failed or repeated execution occurred. It verified intact staging/marker bytes
after cancellation and checkpoint failure, propagation of the original exception,
and a later collision rename that preserves the external file and removes owned
markers. The successful rename exercised the existing PathExists branch, not a
forced race inside File.Move. Linux temporary-filesystem behavior is verified;
Windows sharing/locking and the actual DownloadManager UI/control path remain
outside runtime coverage. See the
[publication evidence](evidence/local-iterations-2026-10-13/output-checkpoint-validation.md).

## Continuation

The integrated media changes are saved in local commit `0e01f77`, and file lifetime
changes in `845649f`, module recovery in `d9ba41c`, and clipboard ownership in
`0e32575`. ZIP result correctness is saved in `907ff86`. Preview cache-generation
ownership is saved in `2d89b32` and remote image ownership in `8e3901a`.
The pin/removal Undo ordering fix is saved in `d510192`, and download-startup
ownership in `783fec7`, and image-export handles in `d03c59d`. Pause failure
feedback is saved in `c12d283`, and discarded-card asset cancellation in `796f90b`.
Quick Panel keyboard focus is saved in `5216d4e`. DropLink directory preparation
budgets and their localized feedback are saved in `b51e307`. Explicit device
handoff sends retain their service lifetime and bounded capacity across
disable/re-enable in `8553eac`. A native progress and cancellation surface for
file sends, with state owned independently of a MainPage instance, is saved in
`8734660`. The active repair tightens cancellation and checkpoint errors at the
download publication boundary. A later product candidate is a persistent
in-session Nearby-link management surface; the server already supports revoke,
but the App has neither a revoke call nor a recoverable list of active links.
Further local workflow investigation continues; target-platform gaps remain explicit.
Previously fixed outbox batch rotation
was confirmed in current source and existing tests, so it was not changed or retested.
The original user failures remain unverified on the target platform until the
Windows/NetEase observations above are available. Do not infer success from C#
compilation or controlled publishers. No background execution is implied after
an active session ends.

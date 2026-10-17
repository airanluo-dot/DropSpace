# Local development after v0.3.2-beta.3 — 2026-10-17

## Baseline and scope

The workspace initially contained clean Beta 2 commit `523d0da`. Read-only GitHub
Release and remote ref checks established that `v0.3.2-beta.3`, published at
2026-10-17 17:14:33 UTC, was the latest public App Beta. Both its tag and main
pointed to `1e858011b493c1bba10bb918192834fe29257ce5`. The clean local branch was
fast-forwarded to that commit before implementation began.

The owner requested autonomous ongoing App development without requiring new
releases. The first checkpoint kept source changes and focused verification local.
The continuation below adds an isolated development branch for Windows compilation;
it does not change `RELEASE_VERSION`, publish a release or claim that development
continues after the execution session ends. Existing data,
default timer durations, module contracts, updater channels and model/CUDA
components are preserved.

Work was split between item interaction, download recovery, timer implementation,
ten-language resources and an independent combined review. An additional language
review checked the translated timer strings. This is the common execution record
for those activities; earlier release ledgers remain unchanged.

## Timer controls

Focus timer and Timer now offer 1, 5, 10, 15, 25, 45 and 60 minute durations from
the time readout's native menu (also accessible through its context menu). A new
duration pauses and resets only that timer. Reset uses the chosen duration;
reaching zero then pressing Start begins another session. Choices remain in
memory for the App session; initial durations remain 25 and 5 minutes.

`WidgetCountdown` uses monotonic timestamps and preserves fractional elapsed time
through pause/resume. Display seconds round upward: starting a five-minute timer
keeps `05:00` until a complete second elapses, and `00:00` cannot appear before
the deadline. Timers and the stopwatch render even before the first native system
sample. Hidden surfaces do not pause a running timer or introduce a new timer loop.

The time readout and a 32-DIP Fluent play/pause button occupy constrained Grid
columns. Independent review identified the previous two text buttons as a width
risk in a 2-by-1 tile; the final layout avoids expanding the row for translated
action labels. At very large text scales, the readout can ellipsize while its full
value remains in the tooltip and accessible name. Menus close on hiding,
unloading, rebuilding and view-model replacement; menu callbacks reject a retired
view. Accessible names distinguish the two timers and their current actions.

Five resource IDs were reviewed in all nine translated languages against English:
`WidgetFocusTimer.Text`, `WidgetCountdown.Text`, `WidgetTimerDuration`,
`WidgetTimerMinutesFormat`, `WidgetTimerDurationHelp`. Exactly those five review
fingerprints changed per language. Narrow policy exceptions cover German `Timer`
and the standard `{0} min` abbreviation in French, Spanish and Brazilian Portuguese.

## Hidden batch selections

The old template hid only a row's inner Grid. Its ListViewItem and selection
could survive folding a batch, allowing invisible members into item actions.
The container now follows batch visibility and clears its old binding on recycling.
An event after complete batch projection changes prunes hidden selections for
folding, pagination and live clipboard header replacement; page reload also prunes.
Subscriptions retire with the page.

Keyboard actions, double click, item dragging and Quick Actions reject hidden or
removed cards. Multi-item Quick Actions filter the current visible projection.
The existing explicit operation of dragging a batch representative as the whole
batch remains. Projection and selection methods live in
`MainViewModel.ItemSelection.cs`, allowing the portable probe to link actual
production algorithms instead of copying them.

## Download recovery

`RestoreAsync` previously marked restoration complete before reading the journal.
If cancellation interrupted the read or traversal, a subsequent call skipped the
remaining records. Success is now recorded only after the traversal finishes.
Cancellation while checking a committed file withdraws only that provisional
recovery item so it can be checked again. Already settled rows and admitted live
downloads keep their existing owners. Recovering history does not start transfers.

The existing IOException path already cleared the restored flag; its regression
case confirms that behavior was preserved. App startup currently does not pass a
user cancellation token, so this is a recovery/lifecycle correction, not a new
user-facing retry button.

## Executed checks

Functional checks were deliberately scoped to changed behavior, with no full
suites, model inference, browser matrix or native stress runs.

| Area | Actual execution | Result and boundary |
| --- | --- | --- |
| Timer | `dotnet test tests/DropSpace.Core.Tests/DropSpace.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~WidgetCountdownTests\|FullyQualifiedName~WidgetExpansionTests.TimerSupportsStartPauseAndReset\|FullyQualifiedName~WidgetExpansionTests.ZeroDurationNeverReturnsNegativeTime'` | 6/6 passed once; deadline rounding, late reads/restart, fractional pause, duration isolation/reset, invalid/zero/large duration, existing compatibility. |
| Download initial attempt | Focused Infrastructure MSTest with `--no-restore` | Build failed with cached NU1301/proxy 403; zero cases ran and no TRX was produced. Locked dependency restore subsequently succeeded. |
| Download before/after probe | Actual download sources plus a small assertion host, three recovery scenarios | Before fix: 1 passed, 2 failed, reproducing cancelled-read and partial-recovery defects. After fix: 3/3 passed. These were portable source-linked executions, not MSTest or live HTTP. |
| Download formal regression | Three `DownloadManagerRecoveryTests` plus `LateRestorePreservesAdmittedRunOwnershipAndStillRecoversNewTasks` | 4/4 MSTest cases passed once after restore, zero skipped. Actual TRX counters were checked. Cancellation during hashing was reviewed, not deterministically injected. |
| Batch selection | `dotnet run --project scripts/batch-selection-probe/BatchSelectionProbe.csproj --no-launch-profile`, then `--no-restore` after adding final visibility-event assertions | 3 distinct scenarios executed twice: 6 passes, zero failures. Second run was required by the added unified notification behavior. Links production projection, item cards and Core selection logic; does not emulate WinUI. |

Non-functional integrity and compilation checks:

- Core and Infrastructure test projects restored using locked dependencies and
  compiled as part of the focused executions above.
- Widget C# subset compiled twice with real official WinUI and Windows SDK
  reference assemblies, nullable checks and warnings-as-errors: both builds had
  zero warnings/errors. The second build covered the final icon/Grid layout.
  The inputs were actual `WidgetViewModel.cs`, `WidgetsExpandedView.xaml.cs` and
  `NativeWidgetDataService.cs`; only unrelated App collaborators and generated
  XAML fields were supplied by a compilation fixture. This is not an App/XAML build.
- Resource extraction reported exactly five pending IDs per translated language;
  explicit-ID review succeeded for all nine languages. App localization integrity
  passed for ten languages and 923 IDs, then passed again after the final timer
  icon/Grid/tooltip source changes. Resource counts are not functional cases.
- MainPage XAML parsed as XML; changed files passed whitespace checks. Independent
  combined source review found no remaining definite blocking defect. Its timer
  width concern led to the final constrained icon layout.

Local raw evidence is under `artifacts/local-iterations-2026-10-17/`: timer TRX,
download before/after logs and formal TRX, and widget compilation fixtures,
reference/source hashes and both build logs. The checked-in evidence summary
([validation-summary.json](evidence/local-iterations-2026-10-17/validation-summary.json))
records the first checkpoint source hashes and actual TRX counters. The continuation
summary records subsequent source changes without rewriting that historical snapshot.

## Remaining validation and next work

At the first checkpoint, no complete Windows App/XAML build or Windows runtime observation occurred.
The new `BatchSelectionSafetyTests` App test class was added but not executed.
The batch probe does not establish ListView virtualization, keyboard focus,
screen-reader behavior, native drag/drop or main-window integration. The widget
compile does not establish menu placement inside the no-activate Island, actual
layout at different DPI/text scales, or visual theme behavior.

The next Windows pass should exercise folding with multiple selected members,
paged/live batch-header replacement, keyboard and Quick Actions after folding,
timer preset/reset/menu retirement, and narrow/large-text timer presentation.
Continue with evidence-backed improvements after that checkpoint; this record
is not a product completion or publication claim.

## Continuation: positions, keyboard navigation and motion

The owner explicitly asked to continue from the existing modifications. A fresh
Release/main read confirmed Beta 3 and `1e85801` were still current. Earlier tests
were retained as checkpoint evidence rather than rerun for unchanged behavior.

- Widget Settings now exposes the existing localized Column/Row controls for the
  selected widget. Values are one-based, bounded by its current size, and apply
  directly through the existing serialized settings transaction. Updating one axis
  reads the other from the latest layout, preserving rapid consecutive edits.
  Rejected positions restore the real coordinates. Pending coordinate input keeps
  its display until the queued saves finish; changing selection targets the correct
  widget. No new resource IDs were needed.
- Review identified an Undo race: an Undo clicked during a pending second move
  could capture the preceding step and undo both moves. Layout changes now share
  a commit-aware helper. Undo is disabled while layout operations are pending;
  only successful changes replace its snapshot, and an old Undo cannot clear a
  later operation's snapshot. No-op/failed edits retain the prior undo step.
  A revision guard also prevents an older coordinate failure from leaving an
  obsolete error after a newer successful input.
- Ctrl+L now joins Ctrl+F as the documented search shortcut. From a module page,
  search restores a visible built-in collection. Escape from an empty search
  falls back to its navigation item when an empty/hidden list cannot take focus.
  Navigation revisions and the originating page lifetime prevent older requests
  from stealing focus after a newer navigation or page retirement.
- Pointer exit, cancellation and capture loss clear the Island's pressed scale.
  Switching to Reduced Motion clears an existing press immediately. A disposed
  composition animator ignores late press/hover callbacks. Existing right-hold,
  placement and hover behavior remains.

Continuation checks executed before Windows compilation:

- Widget editor C# compiled against actual Windows SDK/WinUI references twice,
  with zero warnings/errors: once for the numeric controls, again after the Undo
  and error-ordering fixes. The settings collaborator was a compilation-only stub;
  no native coordinate or Undo interaction was executed. Independent review
  confirmed the concrete await/undo and stale-error issues were addressed.
- Search XAML parsed successfully; seven shortcut combinations had no duplicates
  and referenced existing handlers. Source/whitespace and independent focus-lifetime
  review passed. No functional search scenario was executed on Windows.
- Actual animator/orchestrator source ran in four portable boundary scenarios.
  Before fixes, two passed and two failed (Reduced Motion retained pressed scale;
  disposed animator still wrote the visual). After fixes, 4/4 passed. Total: eight
  scenario executions, six passes and two expected baseline failures. Real WinUI
  reference compilation passed without warnings/errors. Visual properties in the
  runtime probe were shims; routed Windows pointer events remain untested.

### Separate Windows compilation route

The existing candidate route requires an exact whole-source AI release approval,
including files unrelated to lyrics. This iteration does not refresh that approval.
The existing `ci.yml` instead gains an explicit `compile_only` manual option:
Debug App/XAML compilation with locked dependencies, no runtime/model staging,
no functional cases, no package output and no release-promotion evidence. Its
concurrency group and final check name are distinct from ordinary production
validation. Existing PR and release paths retain their production requirements.

The new job has read-only repository permissions and retains only build logs,
a binlog and a source-bound compilation receipt. It rejects `full_tests=true`.
The receipt and artifact names cannot be used as a release candidate bundle.
Full Windows execution results are recorded below after the actual run.

Local checks for the new workflow used four focused Node tests (4/4 passed;
12 actual Bash gate input combinations), then one changed concurrency case
(1/1 passed), and one new check-name case (1/1 passed; four expression inputs).
The selected historical Beta17 route compatibility case initially failed on
stale release-condition/variable-case assertions already inconsistent with HEAD.
Those assertions were aligned with the existing `isolated_beta` route, and only
that case was rerun: 1/1 passed. No release workflow was changed. Workflow audit,
YAML parsing and whitespace checks passed. These are workflow contract checks,
not App functional passes.

The continuation localization scan passed for ten languages and 923 IDs.
[continuation-summary.json](evidence/local-iterations-2026-10-17/continuation-summary.json)
binds the changed sources to these checks and preserves the earlier record.

### First Windows compile and follow-up icons

The development branch `codex/beta3-app-evolution-20261017` was pushed at
`d624782a6b1388c879b98abc4adb64425c4e09c5`. Its manual compile-only run
[39060274785](https://github.com/airanluo-dot/DropSpace/actions/runs/39060274785)
restored locked dependencies successfully but failed compilation. Two CS8602
errors in the new `OverlayWindow` cancellation/capture-loss callbacks preceded
the LocalAssembly/XAML converter-resolution errors. The callbacks were registered
before the readonly motion controller was initialized. All four pointer handlers
now register immediately after that initialization, preserving their order and
behavior; independent source review confirmed the dependency ordering. No null
suppression or converter changes were introduced. The native compilation retry
will confirm whether the downstream XAML errors disappear. Zero tests ran.

The failed run retained its diagnostics artifact. GitHub job logs were retrieved
locally, while the artifact redirect returned HTTP 403 through the environment
proxy; a direct-network attempt could not connect. Subsequent compile receipts
also print a small structured JSON record to the job log, so source-bound compile
results remain readable independently of artifact transfer. This does not change
the compile scope, publication eligibility or validation conditions.

The next source checkpoint also gives every native widget an explicit system
glyph in the App-layer `NativeWidgetIcons` mapping. World clock, focus timer,
disk, network, calculator, pinned items and week progress no longer share a
settings gear. Clock uses the actual Clock glyph instead of Recent; Countdown
shares the verified Stopwatch glyph and retains its descriptive label. Calculator,
Pinned Items and Settings reuse the same mapping in the expanded Island. Button
names remain localized, decorative glyphs are excluded from the control-level
accessibility view, and launch failures still show text with accessible help.
Glyph names/codepoints were checked against Microsoft's Segoe Fluent Icons table.
No timer behavior or resource IDs changed. XML, source/coverage, whitespace and
independent icon review passed; no functional icon tests were added or executed.

The second Windows run,
[39060604585](https://github.com/airanluo-dot/DropSpace/actions/runs/39060604585),
compiled exact commit `e3cd962ef603150f1f9b20b42eddcba7861df232` successfully:
**0 warnings, 0 errors**. The former C# and downstream XAML errors disappeared.
The receipt read from the completed job log matched the actual commit and Git
source tree, recorded zero executed tests and `releasePromotionEligible=false`.
Both production jobs were skipped and no protected production check name was
emitted. The build-log SHA256 is a runner claim retained in that receipt; it was
not recomputed locally because the diagnostic artifact was not downloaded.
This establishes full Debug App/XAML compilation, not native interaction, runtime
launch, accessibility, packaging or Release qualification.

### Continuation: native widget action lifetime

Review after the successful compile found that Pinned Items could finish a slow
query after Widgets was hidden/rebuilt and still reopen the main window. A late
Calculator/Pinned failure also read the mutable view-model field; replacement
with null could throw while handling the original error. Buttons allowed parallel
activations, and successful retries retained the old error artwork/help text.

The view now lazily owns an action retirement token. Hiding, unloading, view-model
replacement and rebuilding retire it. Each activation captures its owner and
requires the same loaded tile, current token and active view before publishing
results. A per-button busy flag prevents duplicate work. Finally restores a retained
button even if its request was retired; a replacement tile is never modified.
Retry restores the icon and clears obsolete accessible help. The host also refuses
a Pinned window-open event while closing.

The initial draft passed the view token into the shared navigation query. Further
review caught an inconsistent intermediate-state risk: `NavigateAsync` changes
the destination/title before replacing the list. Cancelling that query on a
temporary view retirement could leave a Pinned title with the old collection.
The final change lets an already-started shared navigation finish, while checking
the presentation token before/after and requiring its final destination still be
Pinned. Only its obsolete presentation result is cancelled. No shared navigation
transaction was rewritten. A Calculator launch already accepted by Windows also
continues; only the retired view's late feedback is suppressed.

The stopwatch now reads Elapsed once per display update. Previously two reads
straddling 00:59.999 and 01:00.000 could combine into a transient 00:00. The new
getter derives minutes and seconds from the same snapshot.

These changes received source/whitespace review; no functional Windows lifecycle
scenario or additional portable probe was run. The next full Windows compile
covers the combined new source, with results appended below.

The third Windows run,
[39061052563](https://github.com/airanluo-dot/DropSpace/actions/runs/39061052563),
compiled exact commit `6604f59a167d06358246b32a0c9d31f3da880f98` successfully:
**0 warnings, 0 errors**, zero executed tests. Its receipt matched that commit's
Git tree; both production jobs stayed skipped, and only the distinct Debug check
was emitted. This covers the native widget action and stopwatch changes at compile
time. Runtime lifecycle/launch/accessibility scenarios remain unexecuted.

### Continuation: collection navigation ownership

The shared navigation investigation confirmed a separate pre-existing defect:
`NavigateAsync` changed the section/title before loading, but a failed query kept
the old cards, count and paging cursor. Once IsBusy reset, the ListView could
request another page using that old cursor with the new collection filter, mixing
collections. The issue is broader than widget presentation retirement.

Navigation, loading, paging and the existing capture/removal journal now live in
`MainViewModel.Navigation.cs`. The production algorithm remains source-linked for
focused portable execution. A normalized destination change clears the previous
selection, cursor, pagination flag, cards, asset ownership and count before loading.
A same-section refresh retains its accepted cards and asset work on failure.
The destination is not marked empty until a successful query confirms it.
Cross-destination failures/cancellation show existing localized status messages;
only the current revision/request may publish them, and exceptions still reach
the original caller. Clipboard journal merging, removal filtering and overflow
pagination remain intact. Ordinary navigation has no new user-facing cancel
button; cancellation coverage concerns the existing API/lifecycle contract.

MainPage also catches a failed collection-navigation await when its page owner
has retired or a later MainPage navigation revision owns the destination. It
logs that old failure without opening its error dialog or refocusing search.
Current navigation errors and settings-save errors still use their existing
handlers. This view guard does not claim to cover direct navigation from another
surface that leaves MainPage's own revision unchanged; the shared view-model
status and query-result guards still protect the projection itself.

The additional localization scan passed for ten languages and 923 IDs. No new
resource IDs or review fingerprints were needed. The new five-scenario portable
probe and next Windows build are recorded below after actual execution.


The navigation probe executed five distinct scenarios against unchanged extracted
navigation methods from `6604f59` and then the current production partial. The
baseline passed three and failed the two expected cross-destination regressions;
the current source passed all five. This was ten actual scenario executions,
eight passes and two expected baseline failures, with no retry. The failure case
actually called `LoadMoreItemsAsync` and observed the baseline reusing its old
cursor under the new filter and mixing the destination row with prior rows.
Same-section failure preserved accepted rows, selection, cursor and asset owners;
late old success and late old failure did not overwrite the current destination
or its empty-search status. The same eleven common inputs and Core assembly were
used on both sides. An earlier compile-only probe build had zero warnings/errors
and executed no scenarios.

`scripts/navigation-projection-probe/` links production navigation, selection,
projection and card sources. Its controlled repository, managed synchronization
context, state-only view-model fixture and instrumented asset tokens do not
execute WinUI binding, SQLite, actual thumbnail cleanup, focus, accessibility or
search debounce. Complete counts, command lines, baseline extraction identities
and source hashes are retained in `evidence/local-iterations-2026-10-17/navigation-projection.json`.

The fourth Windows run,
[39061775459](https://github.com/airanluo-dot/DropSpace/actions/runs/39061775459),
compiled exact commit `e3bb526727f8179b15a959f7c7caad14e7a5a674` successfully:
**0 warnings, 0 errors**, zero executed tests. The completed job log contained a
receipt matching that commit and its Git tree. Both production jobs were skipped;
no protected production check was emitted. This confirms full Debug App/XAML
compilation of the navigation and MainPage retirement changes. The runner's raw
build-log hash has not been independently recomputed locally; its diagnostic
artifact was not downloaded. Native interaction and pixel validation remain
separate work.


### Continuation: shared navigation requests and widget feedback

A later cross-surface review confirmed the earlier MainPage-only guard's limit.
While MainPage awaited a settings flush or collection query, an Island action
could navigate directly through the shared view model. The older MainPage request
could then publish its destination, show its failure dialog or restore search
focus over that newer choice. Returning to the same section could not be
recognized using only the current section name.

The view model now has a navigation generation separate from query/search reloads.
MainPage reserves it before either settings flush; direct navigation also reserves
a generation synchronously. A reserved request must still own it before changing
the destination. MainPage retains both its page generation and shared generation
through module navigation, query completion, search focus and DLC deep links.
Retired preflight failures are logged without presenting their old errors. Current
failures retain the existing error classification, and dialogs check ownership
again after acquiring the dialog gate. Ordinary search/reload does not invalidate
navigation ownership. No settings persistence transaction was changed.

A separate widget review corrected a timer menu that reused “Reset stopwatch”.
The new `WidgetTimerReset.Text` resource says “Reset timer” in all ten languages,
with a nine-language review limited to that one new ID. Existing stopwatch labels
and historical review fingerprints were preserved. Calculator/Pinned launch
failure text and the widget editor's error text are now polite accessibility live
regions; timer readouts remain non-live to avoid second-by-second announcements.
Actual resource work executed nine extracts, nine explicit single-ID reviews,
one passing App resource gate (ten languages, 924 IDs), source-scope assertions
and whitespace review. No functional test or compilation was performed by that
resource change itself. Those command outputs were retained in the tool history,
not as standalone log files. Native screen-reader announcements remain unverified.


The shared-navigation probe ran only `--ownership-only`, once: two scenarios,
two passes, zero failures/retries. The build within `dotnet run` emitted no
warnings or errors. It called the production reservation and navigation APIs to
verify that a direct request retires a pending reserved request before any query
or destination mutation, that same-section navigation and leaving/returning create
new identities, and that ordinary/search reloads retain navigation ownership.
The original five scenario bodies were unchanged and were not rerun. MainPage's
preflight, DLC callback, dialog gate, native focus and the actual search setter
remain outside this source-linked probe. Inputs, boundaries and actual results
are retained in `evidence/local-iterations-2026-10-17/navigation-ownership.json`.


Final review found the module inventory's direct settings-page entry bypassed
`SelectSectionAsync`; it now reserves both identities before opening that module.
The normal module navigation path does not allocate a second identity. All two
`ShowModulePage` call sites were reviewed. This call-site correction received
source review only and did not trigger a repeat of the two passing probe cases.
Their original source identities are retained separately from the final reviewed
MainPage source hashes.

### Continuation: focused native UI diagnostics

A dedicated, explicit `--beta-ui-diagnostics` entry creates a fresh isolated data
root and real MainPage/OverlayWindow controls. It exercises exactly two synthetic
scenarios: the FocusTimer/Countdown duration menus and their hide/reopen lifecycle,
and real ListView multi-selection when a batch collapses. The fixture explicitly
uses two 2-by-1 timer tiles. Menu automation, container selection, geometry and
pixel capture are recorded separately. Six named captures include the actual
popup subtrees, not just the underlying overlay. This is programmatic automation
at the runner's actual DPI/text scale, not physical pointer/keyboard, mixed-DPI,
desktop compositor or screen-reader validation.

The already registered `music-visual-qa.yml` now accepts an explicit
`beta_ui_diagnostics=true` boolean. Its original music/Release route remains the
default and is skipped for this selection. The new route restores only locked App
dependencies, registers the locked Windows runtime packages, compiles Debug as an
unpackaged framework-dependent app and explicitly generates `DropSpace.resources.pri`.
It stages no model/runtime payloads and does not package or publish a release.
Source commit/tree, apphost, three managed assemblies and PRI hashes bind the
executable to its build receipt; the launcher checks those bytes again. Timeouts,
missing receipts, failed cases, incomplete captures and blank pixels cannot pass.

Before the first Windows execution, both PowerShell files passed AST syntax
parsing, YAML and exclusive-route checks passed, and the old music job was
confirmed unchanged except for its route condition. Two static workflow source
audits passed; the second covered a review correction to omit a global RID during
locked restore so Core/Infrastructure keep their committed lock targets. Those
checks executed zero App functional scenarios. Independent source review covered
isolated exception handling, service/logger retirement, process ownership and
failure accounting. Actual Windows execution is recorded after the source commit.


The diagnostic C# implementation also passed two narrow compile invocations with
zero warnings/errors: the initial implementation and then the final capture/exit
corrections. They used real Windows/WinUI/Core references and signature stubs for
unrelated App collaborators. Neither invocation executed a functional/native
scenario or compiled the App launch entry. That entry received source review.
The direct MainPage host supplies an explicit white Light fixture background;
the production MainWindow normally supplies its backdrop. The real Overlay
background is not replaced. Full preflight counts, source identities and limits
are in `evidence/local-iterations-2026-10-17/beta-ui-preflight.json`.


The first native run,
[39062855382](https://github.com/airanluo-dot/DropSpace/actions/runs/39062855382),
built exact commit `b439cd45610a18b136ccd416e10d188f7e65832c` successfully with
**zero warnings/errors**. The source/tree and apphost/PRI/three assembly identities
matched between build and launcher receipts. The original music/Release job was
skipped. Native execution started two scenarios: **one passed, one failed**.
`batch-collapse-selection` passed actual selection and trailing-row geometry
checks; its expanded/collapsed PNG readbacks both passed pixel-content checks.
The PNGs remain GitHub artifacts and were not locally inspected, so no human
visual approval is claimed.

`timer-menu-retirement` reached the FocusTimer submenu capture, then timed out
within the coarse `timer-duration-FocusTimer` stage. That initial record does not
identify which subsequent duration/start/pause/hide/reopen wait failed. Two
Overlay/menu readbacks also failed pixel presence checks; the two later timer
captures did not run. The process exited with code 1 without hitting the launcher's
timeout. A usable Windows desktop was present (1024 by 768, text scale 1), and the
batch scenario succeeded in the same process. No whole-run UI success is claimed.

Source inspection found two pixel-evidence boundaries requiring care: Overlay
content opacity is driven through Composition visuals while its XAML base opacity
may remain zero, and the reused music-fixture predicate assumes a mostly opaque
background. These observations do not prove the on-screen Overlay was blank or
prove that changing the predicate would validate its rendering. The next timer
attempt adds finer stages and pixel metrics. The already-passing batch scenario
will not be repeated merely to investigate this independent timer failure.


### Continuation: explicit accessibility error events

Official WinUI documentation/source inspection did not establish automatic
`LiveRegionChanged` events for arbitrary TextBlock text updates. Setting `Polite`
alone therefore was insufficient to claim active error notification. The widget
editor now centralizes error text/visibility changes and explicitly raises the
live-region event once for a new nonempty visible error. This also corrects a drag
error catch that previously updated text without making its collapsed label visible.
Hidden/unloaded owners retire pending announcements; clearing or repeating the
same error does not announce it. Calculator/Pinned failure text waits until it is
mounted and still belongs to the current tile/request before raising the event.
Notification exceptions cannot replace the underlying action error.

One attempted compile launch did not start because `dotnet` was absent from PATH.
Using the installed SDK's absolute path then ran one real WinUI-reference subset
compile, successfully with zero warnings/errors. No functional or screen-reader
scenario was executed. The source basis, original inputs and both launch records
are retained in `evidence/local-iterations-2026-10-17/widget-error-announcements.json`.
These evidence records are frozen before the later Pinned navigation-call wiring.


### Continuation: timer-only failure investigation

The diagnostic launcher and App now accept a fixed `all`, `timers`, or
`batch-selection` choice. Their exact case/capture maps are 2/6, 1/4 and 1/2;
unknown choices, mismatched receipts or missing captures cannot pass. A timers-only
run constructs its Overlay and neutral lifetime host without constructing MainPage
or executing the previously passing batch case. This changes the diagnostic host
scope; it is not a product timer fix. The original all route remains available.

Every timer action/wait now records a distinct stage, automation pattern and peer,
actual Click count, duration/running state, menu state and current host ownership.
Captures retain the same pixel predicate and add nonzero-alpha, opaque, transparent
and color counts. XAML/Composition opacity, native window safety/visibility and
active frame-subscription state are also recorded. The job-log receipt prints a
bounded tail of these observations without exposing PNG data. Open-source WinUI
Invoke/Toggle implementations both raise Click, so the original coarse failure
was not used as justification to alter timer input handling.

This instrumentation passed one additional narrow C# compile with zero warnings
or errors, independent source review, changed-launcher PowerShell parsing (twice,
the second after metadata logging changed), one YAML/route check and one workflow
source audit. These checks executed no functional/native cases. Their original
inputs and counts are in `evidence/local-iterations-2026-10-17/beta-ui-timer-selection.json`.


### Continuation: Island navigation result ownership

The Pinned widget's former final section-name check could accept an old request
when another surface navigated away and returned to Pinned. Clipboard's open-main
handler only checked window closure, so an old query could reopen the main window
and collapse the Island after a newer destination was selected. Both callers now
reserve their shared navigation identity before awaiting and recheck it immediately
before visible success or failure feedback. Widget error announcements use that
same current-request predicate. Retiring the UI still does not cancel an already
started shared collection load.

Clipboard also tracks presentation ownership per OverlayWindow, including hidden,
unloaded, page-switched, native-failure and fullscreen-suppressed states. It checks
the actual host visibility and the same loaded view model before opening the main
window. Final review found the selected window's position-edit path did not retire
its Widgets/Clipboard surfaces; entering placement edit now does so, preventing
pending actions from becoming valid again after commit/cancel restores the view.
The synchronous Settings action and explicit stress/smoke diagnostic callers were
not changed.

The real WinUI-reference compile subset ran twice: the first failed with two
CS0103 errors because its fixture omitted the newly required actual
`NativeWidgetIcons` source link; adding that source link led to the one necessary
retry passing with zero warnings/errors. Production compiled inputs did not change
between attempts. This did not compile OverlayWindow or execute any native/UI,
original navigation probe or already-passing batch scenario. Final OverlayWindow
visibility/placement changes received independent source review. The complete
attempt history and final reviewed source identities are retained in
`evidence/local-iterations-2026-10-17/island-navigation-consumers.json`.


### Continuation: precise timer reopening failure

The timers-only run [39063598625](https://github.com/airanluo-dot/DropSpace/actions/runs/39063598625)
built exact commit `ddadcbfecb6e72c93c39f0e6c0a08526a40e7f0f` with zero warnings/errors.
Source/tree, apphost, PRI and all three managed assembly identities matched across
the build and launcher receipts. The old music/Release job was skipped. It executed
**one timer scenario, which failed**; the previously passing batch case did not run.

The finer observations confirm the FocusTimer's real Toggle peer raised Click,
changed duration to 10 minutes, and its real start/pause button raised two Clicks
and returned to paused. The failure is now localized to reopening the duration
menu before hiding the Overlay: the second button Click occurred after IsOpen had
become false but before the first Closed event. Waiting for the second open timed
out; the first Closed event arrived during that wait. This supports investigating
the diagnostic's close-animation boundary rather than changing working duration
or start/pause behavior. The remaining Countdown/hide/reopen checks did not run.

The initial Overlay readback contained 190400 transparent pixels and no nonzero
alpha; ExpandedPanel still had XAML opacity 0 with Composition opacity 1. The submenu
readback contained 1888 nonzero-alpha pixels and 33 colors, but no opaque pixels.
Both still fail the unchanged opaque-fixture predicate. These results do not prove
that the desktop-composited Overlay is blank. PNGs remain remote artifacts without
local visual inspection. The child exited 1 without launcher timeout or unhandled
UI exceptions. Exact receipts and evidence hashes are appended to
`evidence/local-iterations-2026-10-17/beta-native-ui.json`.


### Continuation: Clipboard island refresh recovery

The Clipboard island previously left a refresh error visible even after the next
read succeeded. Its worker also accepted callbacks from a previous visibility
period if every owner had hidden and one reopened while a read was pending.
The shared view model now tracks aggregate visibility transitions and rechecks
that identity at both success and failure publication. Hiding only one of several
visible owners does not retire their shared work, and accepted cached cards remain.
Successful refreshes clear only refresh errors; a failed user action still needs
a new user action and is not concealed by a later successful read.

A small probe links the complete production ClipboardIslandViewModel, actual
dispatcher extension, item-card models and Core code. Against fixed old commit
`ddadcbfecb6e72c93c39f0e6c0a08526a40e7f0f`, only the recovery scenario ran: **0/1**,
with the stale-refresh-error assertion failing and all six assertions reached.
Preserving action failures already passed on that baseline; no before result is
claimed for the visibility cases. The final source then ran the four selected
scenarios once: **4/4**, all 13 assertions passed. Total: five scenario executions,
four passed and one expected baseline failure, zero retries. Both independent
builds emitted zero warnings/errors. Source hashes remained fixed during execution.

The controlled boundaries replace MainViewModel reads/actions, capture events and
the WinUI dispatcher. These checks do not execute the real QueryAsync/thumbnail
pipeline, SQLite, clipboard capture, native dispatch or rendering. Neither the old
navigation cases nor native scenarios were rerun. Independent source review found
no blocker. Original inputs, per-attempt counts and artifact hashes are retained in
`evidence/local-iterations-2026-10-17/clipboard-island-refresh.json`.


### Continuation: wait for the timer menu's actual close completion

The diagnostic now captures each relevant menu cycle's Closed count before opening
and waits for both IsOpen=false and a newer Closed event before reusing the button.
Capturing before opening also handles synchronous closure during selection. Real
button/Toggle invocation, production timer behavior and the existing pixel predicate
remain unchanged. The actual failed-run event sequence and WinUI's documented/source
close lifecycle support this fixture correction; upstream source is corroboration,
not proof of the exact locked runtime binary.

Independent review found no blocker. One additional narrow C# compile passed with
zero warnings/errors; it executed no functional/native cases. These inputs were
frozen before the later supplemental desktop-capture work. The receipt is in
`evidence/local-iterations-2026-10-17/timer-menu-close-barrier.json`.


### Continuation: center the Island's press feedback

Press feedback scaled the separate SurfaceContent Composition visual without
setting its center, leaving the default top-left pivot. The outer Surface's XAML
RenderTransformOrigin applies to a different transform. The content now sets its
center from its current size before applying the original scale, both on pointer
feedback and motion frames. Reduced-motion resets, disposed-input guards and the
independent top-anchored content transition pose remain intact.

Independent source review supported this small visual correction. No functional
scenario, local compile or physical press check was added; the planned Windows
Debug build will compile it, while actual press appearance remains unverified.
No previously passing pointer scenarios were repeated. Source identities and the
verification boundary are in `evidence/local-iterations-2026-10-17/overlay-press-center.json`.


### Continuation: bounded supplemental image transport

Because the artifact download redirect is unavailable in this workspace, the
explicit manual Debug route can additionally transmit the original supplemental
PNGs as bounded chunks in its job log. The script defaults to no image transmission.
It permits only four fixed timer filenames from the owned child's fresh diagnostic
directory, checks process identity, file type, recorded SHA-256 and PNG signature,
and rejects linked paths. Limits are 128 KiB per image and 256 KiB total; oversized
images are marked skipped, never resized or re-encoded. Transfer success or failure
does not change any scenario count, RTB capture requirement, original failure or
visual status. Hash verification is required before local reconstruction/viewing.

One PowerShell parse, one workflow source audit, one YAML/unchanged old-route and
primary-gate check, and one changed-file whitespace check passed. These static
checks ran no launcher/helper, App scenario or Actions workflow. The original
inputs and transport limits are in `evidence/local-iterations-2026-10-17/beta-ui-desktop-log.json`.


### Continuation: Clipboard Pin pending state and failed-toggle recovery

The native ToggleButton changes checked state before its async persistence finishes.
Its OneWay source binding previously received no notification if persistence failed
without changing the card, leaving the visual state and action label inconsistent.
Pin now disables the card's control and rejects another pending Pin request for that
same card instance. In finally it republishes the card's known pin value/action label
and reenables the control. The original binding remains; no async callback writes
a potentially recycled control. This does not serialize different card instances.

The displayed value is the last known card snapshot, not proof that a failed action
rolled back storage. A write may succeed before its follow-up read fails; the action
error remains and a later accepted refresh can recover the stored state.

Only the new `pin_action_recovery` source-linked scenario ran once: **1/1**, all
nine assertions passed, zero retries. It covers pending/repeated request, failure
notification, restored availability and a successful explicit retry. The prior four
refresh cases and baseline were not rerun; their frozen evidence is unchanged. One
additional official WinUI/Windows-reference C# compile passed with zero warnings/errors.
It did not compile XAML or run binding, ToggleButton/UIA, real persistence or native
rendering. Independent source review found no blocker. Exact inputs and boundaries
are in `evidence/local-iterations-2026-10-17/clipboard-island-pin-feedback.json`.


### Continuation: separate desktop-composited timer attachments

The focused timer diagnostic now additionally reads four bounded screen regions
through DwmFlush, desktop BitBlt and GdiFlush. Surface bounds come from the owned
Overlay HWND client area; popup bounds require the real automation peer's screen
rectangle. It validates window visibility, process identity, uncloaked state, screen
bounds and sampled HWND owner chains before and after readback. Unsupported peer
bounds/ownership remain explicitly blocked; there is no guessed fallback crop.
The helper owns and releases all native resources and records both primary read
and cleanup failures. A synchronous OS call cannot be forcibly interrupted in
process; the worker keeps resource ownership after the bounded await, and the
launcher's process deadline remains the ultimate bound.

The raw BGRX hash is recorded before setting the unused alpha byte to 255 for PNG
encoding. That alpha is explicitly synthetic and is never fed to the old RTB gate.
Supplemental images/failures are independent of required captures and status.
Sampled HWND ownership cannot prove every pixel is unobscured or visually correct;
the original RTB failures remain, and these images require actual inspection.

One narrow C# compile passed with zero warnings/errors and stable source/reference
hashes, plus independent source review. No functional/native execution was added
at this checkpoint. Full inputs and limits are in
`evidence/local-iterations-2026-10-17/beta-ui-desktop-capture.json`.


### Continuation: timer assertions pass; real images expose unresolved display behavior

Run [39064697266](https://github.com/airanluo-dot/DropSpace/actions/runs/39064697266)
built exact commit `6f0fd72af5f640c243567e9773462a8c90d64c27` with zero warnings/errors.
Build and launcher source/tree, apphost, PRI and managed assembly identities matched.
This includes the Clipboard XAML and centered-press changes in full App compilation,
without exercising their physical interactions. The old music/Release job was skipped.

The selected timer scenario ran **once and passed its implemented assertions** for
both FocusTimer and Countdown: duration selection, start/pause, root-menu close,
hiding and reopening. The batch scenario did not run. All four RTB images still
failed the unchanged pixel predicate, so the App reported blocked and exited 2;
the workflow failed rather than claiming complete native UI success.

Two supplemental desktop PNGs were captured and transmitted. Their original bytes
were reconstructed locally and SHA-256 verified against chunk and transfer metadata.
The first extraction failed with KeyError because the summary deliberately truncates
large supplemental metadata; the corrected extraction uses the complete transfer
entry and intact header prefix. This was local evidence processing, not another
App execution. Full owner-chain metadata remains remote; original RTB and assembly
hashes were not independently recomputed locally.

Actual assistant inspection of the initial desktop PNG shows only the rounded
surface, without visible timer content. The reopened PNG shows the two 10-minute
readouts/play controls and a duration submenu still visible at the upper-left.
The 2 x 1 widget layout intentionally omits titles, so that omission is not itself
a new defect. These images are not visually approved. The implemented root
MenuFlyout Closed/IsOpen checks do not prove every descendant popup disappeared.
Final observations also show invocation can occur while the Overlay is still
Entering; later observations reach its settled size. This requires further
product-versus-fixture investigation, without repeating the now-passing full timer
interaction sequence just to obtain images.

Both popup desktop captures were blocked because the reported ROI was outside the
virtual screen. Their raw bounds were lost when validation threw before creating
the snapshot, so those errors alone cannot distinguish real popup placement from
a coordinate-evidence problem. Exact results and the two byte-identical retained
PNGs are in `evidence/local-iterations-2026-10-17/beta-native-ui.json` and
`native-timers-39064697266-{initial,reopened}.png`.


### Continuation: retire pending right-button placement requests

A right-button hold could queue placement editing, then survive hiding/fullscreen
suppression before its dispatcher callback ran. The callback previously checked
monitor availability but not the originating hold. Window and service now keep
the same event signature while capturing a per-hold generation and verifying the
same window, current visibility/eligibility and unconsumed request at execution.
The explicit settings entry still opens placement editing without a gesture token.

Hiding, dismissal, native failure, suppression, matching pointer release/cancel/
capture loss, disabled setting and closing retire the pending hold and release its
capture. Elapsed-time checks prevent an older queued timer tick from prematurely
completing a new hold. A successfully consumed pointer travels separately through
synchronous service side effects to the editor, which starts dragging only after
its own capture succeeds. Existing general multi-pointer placement handlers were
not expanded or claimed tested.

Independent source review found no blocker. One changed-file whitespace check and
read-only official-reference metadata lookups completed; zero functional scenarios,
native gestures, App compiles or dispatches were performed for this slice. The
combined real Windows build remains pending. Exact source identities and boundaries
are in `evidence/local-iterations-2026-10-17/right-hold-retirement.json`.


### Continuation: preserve accepted Clipboard card identity

Clipboard publication now uses the existing Core identity synchronizer and card
merge contracts instead of clearing and recreating the displayed collection.
Unchanged record IDs keep their card instance and pending Pin state. Missing cards
retire their presentation/assets before removal; changed content follows the
existing revision/generation checks. A snapshot cannot revive a still-present
retired card at the same or an older revision, while newer revisions can restore
it. That bounded check is not a persistent tombstone or a new query coordinator.

The recent-item query/thumbnail pipeline and shared synchronizer are unchanged.
This reduces collection resets and replacement of displayed cards, not temporary
card creation or upstream IO. The shared helper uses Remove/Insert for actual
reordering, so no native focus or scroll-preservation guarantee is inferred.

Only the new `projection_identity` integrated source-linked scenario ran once:
**1/1**, all 16 assertions passed, zero failures/retries/compiler warnings/errors.
It uses actual VM, card and Core synchronization logic, with explicit read/action,
quick-action and dispatcher boundaries plus identity-only image/storage shims.
No actual repository, Undo persistence, asset IO, XAML, UIA or native focus ran.
The prior four scenarios, Pin scenario and their default selection were unchanged
and not rerun. Final source review found no blocker. Inputs and evidence are in
`evidence/local-iterations-2026-10-17/clipboard-island-projection.json`.


### Continuation: isolate first-frame and descendant-popup evidence

The new explicit `timer-visual` selection runs one different scenario,
`timer-first-frame-and-popup-retirement`, with three required captures: initial,
FocusTimer menu, and reopened. It does not repeat duration changes, start/pause,
Countdown interactions or batch selection. Old selection contracts and the RTB
pixel predicate remain unchanged.

The scenario waits for the production Overlay motion/page transition to settle,
then Rendering, a real compositor commit request and another Rendering event.
Desktop readback precedes RTB at each capture. Before UIA expansion it requires the
root menu's Opened event, a loaded/nonzero submenu in its actual popup and stable
peer bounds. It tracks actual root and descendant Popup instances, requiring a
Closed event, unloaded child, IsOpen false and absence from the open-popup list
after hiding. Logical removal from that list alone can happen during unloading.
If hiding fails, it still tries to reopen and retain the final image without
manually collapsing the suspect child. Early and hide-stage errors survive later
cleanup failures, which remain separately recorded.

Raw before/after popup rectangles and virtual-screen bounds now survive validation
failure. Log summaries preserve capture identity and these failure probes while
explicitly bounding nested ownership records; the existing PNG transport is
unchanged. Independent source review found no blocker. This establishes diagnostic
conditions, not physical display correctness: desktop GDI readback can omit hardware
planes, and actual PNG inspection remains necessary. Native execution of this new
selection is pending at this checkpoint.

Preflight includes two successful narrow C# compilations with official WinUI
references, each with zero warnings/errors. The second was necessary after review
added the Closed/unloaded requirements and early-error preservation; both inputs
and logs remain recorded. Launcher/workflow checks executed five times in total:
AST once, source audit twice across the before/after boundsAfterProbe revision,
YAML/old-gate integrity once and changed-file whitespace once. These are compilation
and static checks, zero functional scenarios. Final frozen inputs and the earlier
snapshots are in `evidence/local-iterations-2026-10-17/timer-visual-preflight.json`.

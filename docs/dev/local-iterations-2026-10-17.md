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

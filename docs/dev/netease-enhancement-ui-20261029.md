# NetEase enhancement entry-point repair — 2026-10-29

## Beta5 migration onto the actual Beta4 release

Earlier work in this record was accidentally developed on local main a80940a3
(Beta2). Those results are historical and do not establish Beta5 acceptance.
The corrected branch starts at the verified published Beta4 tag and remote main:
648cc6d856f558367b4c598220e9b403a67ef68a. RELEASE_VERSION is v1.1.0-beta.5.

Three-way migration retains Beta4's centered Music empty state, global lyric target
placement, liquid-glass settings and Music window behavior. Translation review conflicts
were resolved from Beta4's records, then only the five changed classification IDs were
reviewed again. Other Beta3/Beta4 source changes remain untouched. The user-owned
AGENTS.md changes in E:/Dev/DropSpace were not included or overwritten.

Validation is recorded below as it completes. The first locked restore omitted
PublishSingleFile=false and failed NU1004; retry with matching build properties passed
without modifying dependency lockfiles. Fresh worktree localization required npm ci
before review commands could run.

## Live uninstall/reinstall regression and repair

The revised build still failed after moving the same card between inventory panels:
the visible install button silently returned when its Loaded/dialog lifetime had been
retired by Unloaded. Reproduced through the actual old window: click produced neither
confirmation nor installer log. This was not a slow download.

DlcPage now retires the previous card and constructs a fresh view when the verified
inventory group changes. The singleton view model and approved installer operation
remain intact; replacement views do not start a redundant inspection. Also corrected
the remove confirmation's primary button, which had incorrectly said Confirm and install.
No change to the agreed Music navigation-only surface or DLC grouping contract.

Native functional coverage on lifecycle-fix (same process, no restart between actions):
- Initial installation: Enhanced at 23:08:50 China time.
- Remove: Removed at 23:09:52; card entered Not installed.
- Click install again: confirmation appeared immediately, request started at 23:10:43,
  visible Verifying/progress, Enhanced at 23:12:20; UI showed complete media control
  and the card returned to Installed. This is one complete installation/removal/
  reinstallation scenario, plus one old-build defect reproduction.

Both lifecycle-fix and lifecycle-final App/XAML builds passed with zero warnings/errors.
The only subsequent final-build difference is the remove confirmation button label;
the native installation cycle was not repeated for that text-only correction.
Existing localization results are reused because no resource values changed.
diff --check passed. No broad suite, publication or installed-app replacement occurred.
The final debug executable is artifacts/netease-ui-20261029/lifecycle-final/DropSpace.exe.

## Superseding user decision after interactive review

The user rejected the top-level Music card and the unclassified DLC card. The final
implementation removes enhancement management and inspection from Music entirely;
only a navigation entry remains after media application settings. DLC owns the full
enhancement card and places it in the same installed/not-installed inventory hierarchy
as other packages. Busy operations retain their current group until completion, then
refreshed verified inventory determines the group. Five classification/empty-state
resource IDs were revised in all ten languages with explicit translation reviews.
UX.md and ARCHITECTURE.md now describe this decision, superseding the initial approach below.

Follow-up validation: Debug App/XAML build succeeded with zero warnings/errors in
artifacts/netease-ui-20261029/revised; localization integrity and diff whitespace checks
passed. No additional functional tests were run for this presentation-only revision.
The first localization script attempt used the inactive Windows python alias and wrote
nothing; retried successfully with py. The review command rejects en-US because only
translated locales need review records; the nine translated locales were then reviewed.
Fresh offline PRI generation succeeded, and the previously launched debug test process
was replaced with the revised executable for user testing, without replacing the installed app.
Actual visual/navigation and installation acceptance remain with the user's manual test.

## Findings

- MusicPage relocated the enhancement card on every IsEnhanced transition. Detecting,
  Preparing and other busy states are not enhanced, so a bottom-of-page action moved
  its own progress to the top, raising Unloaded/Loaded and retiring local UI ownership.
- Settings DLC rendered a generic PackageCard instead of NeteaseEnhancementCard.
  NeteaseComponentsDlcProvider only announced terminal state changes and did not report
  numeric download progress. The generic card could not explain installer phases and
  exposed a different set of actions.
- RunAsync performed a 60-second live media verification under Detecting even for a
  forced reinstall, although neither native-support nor same-version shortcuts apply
  to forced reinstalls.
- The local application log records a DLC attempt canceled at 22:29:31 China time;
  the cancellation origin is not established. The next attempt ran from 22:29:33 to
  22:31:11: approximately 60 seconds before Preparing, 14 seconds preparing, 5 seconds
  deploying, and 19 seconds restarting/verifying. This is observed historical runtime
  evidence, not a test of the repaired application.

## Changes

Both pages now construct the same enhancement card with the same singleton view model.
The card has a stable location, description, readable installed size, explicit status,
indeterminate progress, errors, responsive action layout and shared installation cancellation.
Install/update/reinstall/remove confirmations and available actions use the same implementation.
Approved operations continue across navigation; cancel stays busy until recovery returns.
Managed state survives passive detection, queued notifications read the latest state, and
the UI becomes busy synchronously before worker publication.

Forced reinstalls skip the redundant pre-install verification. Normal installation/update
still checks native capabilities, now explicitly labeled Verifying. Post-install verification,
transactional commit, rollback and receipt ownership remain intact.

UX.md previously prescribed moving recognized installations below settings; that behavior
caused this feedback failure. The user's current stable, unified UI requirement supersedes it.
UX.md now specifies a fixed position; ARCHITECTURE.md identifies the shared Music/DLC card.

## Execution record

- No agents, CI jobs, publication or installed-app replacement.
- Two initial Debug App builds failed on a missing AppUiMessage namespace import, with
  cascading XAML resolution errors. Corrected the import to DropSpace.Core.Models.
- Focused App test invocation then compiled App/XAML and passed one presentation case.
- After adding shared cancellation, repeated the affected case and added a cancellation
  recovery case: 2 passed, 0 failed. Total functional executions: 3 across two runs,
  representing 2 distinct cases. No broad suite or native installer scenario was run.
- Latest TRX: artifacts/netease-ui-20261029/Razer_RAZERBLADE_2026-10-29_22_45_46_net10.0.trx.
- App localization integrity passed: 10 languages, 1035 IDs; no resources changed.
- git diff --check passed before this record was added; final check includes this record.
- Remaining validation: actual installation/removal/cancellation against the player,
  graphical review of both entry points, narrow/large-text layout and navigation during
  a real deployment. Synthetic view-model tests do not establish those outcomes.

## Beta5 glass appearance follow-up — 2026-10-29

Fixed the island root theme: Liquid Glass selects the dark content palette without
modifying the persisted Acrylic theme. The appearance selector maps Ink glass and
Ultra-clear glass to the existing LiquidGlassBlackGradient boolean; the separate
switch is removed. Existing glass values and Acrylic preferences are retained.

Validation: app/XAML Debug Portable build passed with zero warnings/errors;
localization integrity passed for 1052 IDs across ten languages. Only the new
Ultra-clear glass label was translated/reviewed. Initial resource-generation attempt
via the host python alias did not execute; reran with PowerShell successfully.
Diff whitespace check passed. No new functional cases or native theme-switch tests
were run; the user requested to handle interactive validation. New build is at
artifacts/beta5-glass. Design-system wording and Beta5 draft notes were updated.

Initial launch used a stale copied unpackaged PRI and failed on the new label. Regenerated the standalone PRI from current resources with Generate-PortableResourcesPri.ps1 before relaunching.

## Inline version and foreground update check — 2026-10-29

Music now includes a brief purpose/absence explanation. The DLC card reads the
actual local InfLink-rs manifest version, rather than assuming the receipt is current.
Main-window visible activation triggers one read-only release request per process;
constructing the background window does not. Automatic failures are silent; manual
checks show results inside the card. Installation remains an explicit separate action.
The complete upstream BetterNCM/InfLink integration is preserved; the proposed custom
fork was canceled, with no replacement made.

Validation: app/XAML compiled, 3 focused NeteaseEnhancementPresentationTests passed
(0 failed/skipped), and localization passed for 1059 IDs/10 languages. The new test
covers no construction-time request, one first-open request, actual-version comparison,
silent automatic network failure, and an inline manual failure. Existing two cases
cover cancellation cleanup and queued-stage retirement. Native foreground/background
launch scenarios and a real newer-release install were not exercised. Updated PRI
copied into the Debug build before launch; user retains interactive validation.

### Music entry layout correction

Moved the explanation inside the enhancement settings card, below its title, with
secondary 12-point text and wrapping. The DLC navigation button remains in the right
column. SettingsForm.AddRow now accepts an optional description key so this layout
uses the shared card padding, spacing, and styling. Removed the standalone text below
the card. This is a layout-only correction; no functional tests were added.
App/XAML build passed with zero warnings/errors; diff whitespace check passed. New layout build launched for user validation.

### Glass defaults and percentage baseline correction

Blur defaults and invalid-value fallback are now 2; existing explicit blur values
are retained. Expanded refraction width uses the former 200% effect as the new
100% UI baseline. UI bounds/step become 25–150% / 5%; read/write scale by 1/2 and 2
against the unchanged persisted renderer units, preserving existing visual settings.
The renderer itself and compact refraction are unchanged.

Focused LiquidGlassSettingsTests: 2 passed, 0 failed/skipped. Initial --no-restore
attempt failed NETSDK1004 because the Core test assets were absent in this worktree;
normal restore and retry succeeded. No native visual scenarios were run.
App/XAML build passed with zero warnings/errors; diff whitespace check passed. Updated build launched for user validation.

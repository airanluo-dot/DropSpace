# beta18 Round 2 — App/UI full-source review

## Baseline and actual coverage

The immutable review baseline is `/workspace/scratch/beta18-round2`, source commit
`729d5b179f34ab2f852e7975f129431f77e8dc7a`, after the round 1 fixes and integrated
PRs 107/108/109 plus island appearance/content-priority changes. This is a fresh
review of complete current files, not a diff review or a repetition of round 1
findings.

**58/58 scoped files and 17,850/17,850 physical text lines were read completely.**
The newline count is 17,849 because `packages.lock.json` has a final unterminated
line; that final line was read. Scope: [round2-app-ui-scope.txt](round2-app-ui-scope.txt).
Every file was physically read from its first line through its last line,
including XAML, both complete locale resources, manifests, the project, RC, and
the lock file. No assigned source output remained truncated. The parent App
review covers all App modules this round through its four parallel source
partitions; no App module is deferred to round 3.

Coverage was split into three disjoint reads: `vm_full` read all 10 ViewModel files
(3,364 lines); `main_settings_full` read MainPage, all Settings views, dialog lifetime,
tokens, and converters (14 files, 4,830 lines); this reviewer read the remaining
34 root/window/project/resource/Music/Island files (9,656 physical lines).
All reviewers read `/workspace/DropSpace/AGENTS.md` and the canonical
`.agents/skills/dropspace-maintainer/SKILL.md`; relevant App/UI guidance and callers
were also inspected. The full per-file coverage table follows below.

Scope-list SHA-256:
`f684faa6d9cf911335c1cb447b36b1546b9d7eb18ce6665bcacbf793220c7b35`.
Reviewed content-set SHA-256:
`056aa5580fb5b231572a1e45753adf95763235030e7583c674b7d31e62422d98`.
The latter hashes, in scope order, each UTF-8 path, a NUL, its lowercase file
SHA-256, and a newline. Individual file hashes are retained in
[round2-source-manifest.json](round2-source-manifest.json), whose SHA-256 is
`f3be1f8a232db46d8eff7d557661d3316a3da658948f4520703abe15ff137ac8`.

## New actionable findings

Line references below refer to the immutable baseline, with paths relative to
`src/DropSpace.App/` unless otherwise specified. These findings are source-confirmed;
the listed interleavings and failure branches were not executed.

### R2-UI-01 — P2: Reload journal truncation loses the paging obligation

`ViewModels/MainViewModel.cs:1572–1576` caps the active reload capture journal at
250 entries without retaining whether entries were evicted. `ReloadAsync:803–808`
derives `liveOverflow` only from the already bounded journal merged with the old
page, then `814–820` overwrites the cursor and `HasMoreItems` produced by live
trimming (`1604–1609`). When an empty first-page snapshot is waiting and more than
250 captures arrive before publication, the journal contains exactly 250 and the
merge reports no overflow. The empty snapshot restores `HasMoreItems=false` and a
null cursor, although evicted capture rows remain in the repository. One permitted
clipboard-file capture can contain up to 1,000 entries
(`DropSpace.Core/Policies/SettingsValidationPolicy.cs:21`). Older retained rows are
therefore inaccessible through paging until a later reload.

**Minimal fix:** retain an overflow flag in the same reload-owned journal; set it
whenever the journal evicts an entry. During publication, use that flag alongside
merge overflow to force `HasMoreItems=true` and derive the cursor from the retained
tail. Preserve the bounded journal and its request identity. This is a new boundary
case in the round 1 journal fix, not a restatement of the old capture-overwrite bug.

### R2-UI-02 — P2: Live clipboard batches never receive grouping state

`ViewModels/MainViewModel.cs:1570–1597` inserts or updates captured cards without
calling `ApplyBatchProjectionState`. `ItemCardViewModel.cs:18–20` defaults new cards
to no batch header and visible members, while current clipboard file capture
persists valid batch metadata (`Services/ClipboardCaptureService.cs:1658–1676`).
The main view exposes the group heading/expand button and group actions only on a
header (`Views/MainPage.xaml:229–232,278–279`). A newly copied multi-file batch thus
has no group heading, collapse/expand, or batch actions until reload. Trimming a
previously collapsed batch's header (`MainViewModel.cs:1604`) also leaves remaining
members hidden because no replacement header is elected.

**Minimal fix:** reapply grouping after captured insertion/update and trimming.
Coalesce capture callbacks or their grouping refresh if needed so a large capture
does not recompute the entire group projection separately for every member.

### R2-UI-03 — P2: An in-flight projection can restore a removed row

`ViewModels/MainViewModel.cs:1155–1173` successfully marks a single-item removal and
removes its visible card, but neither fences an older reload nor removes or
tombstones that ID in `_clipboardReloadCaptures`. A previously materialized page
(`793`) or capture journal (`805–812`) can then repopulate the removed row.
New repository queries correctly exclude pending removals
(`DropSpace.Infrastructure/Data/SqliteItemRepository.cs:365`); the stale materialized
results bypass that exclusion. Clipboard-island removal invokes this path directly
(`ClipboardIslandViewModel.cs:46`, `ClipboardIslandView.xaml.cs:15`), so it can
overlap a main-window reload. The removal appears undone until another refresh or
the Undo expiration refresh.

**Minimal fix:** give the projection owner removal tombstones and filter both
materialized pages and the journal before publication, with explicit Undo and fresh
projection reconciliation. If invalidating the reload generation instead, restart
or finish the interrupted reload: incrementing `_reloadRevision` alone prevents its
`finally` (`831–835`) from clearing `IsBusy`.

### R2-UI-04 — P2: A delayed older navigation overrides the latest selection

`Views/MainPage.xaml.cs:264–278` waits for settings flushes before assigning the
selected navigation item and calling `NavigateAsync`. The editor takes pending
edits out of its queue before waiting for persistence (`NativeSettingsEditor.cs:84–88`),
and a subsequent flush returns immediately when that queue is empty. With a pending
edit, selection A can wait for a slow save, selection B can navigate immediately,
then A's continuation navigates back to the older selection. `RunAsync:2354` does
not serialize these requests. MainViewModel's projection revision is created only
after this outer flush, so it cannot reject the stale navigation intent. A queued
Downloads concurrent-count change is a concrete trigger (`DownloadPanel.cs:97–100`).

**Minimal fix:** capture a MainPage navigation revision and page lifetime at the
start of `SelectSectionAsync`; after flushing, abandon a stale or retired request
before changing the navigation selection or MainViewModel. Let the settings save
finish under its existing owner.

### R2-UI-05 — P2: Hidden Downloads and DLC pages continue native UI work

`Views/MainPage.xaml:819` hides Settings through ancestor `Visibility`. The selected
settings subpage stays loaded when the main section changes; tray hiding and
minimization also retain the tree. `DlcPage.cs:57–73,82–90` gates rendering only on
its loaded lifetime, while `DownloadPanel.cs:127–139,224–229` gates on `_loaded`.
The explicit main-window presentation signal added in round 1 reaches only Music
(`MainPage.xaml.cs:213`, `MainWindow.xaml.cs:121–145`). Download/DLC service progress
is published every 200 ms (`DownloadManager.cs:353–367`,
`Services/Dlc/DlcManagerService.cs:270–298`), so invisible settings controls continue
updating native text/progress/layout. DLC additionally recreates four button-content
TextBlocks per package update (`DlcPage.cs:269`). The source fanout is confirmed;
no CPU or allocation measurement was performed.

**Minimal fix:** propagate window, main-section, and settings-subpage presentation
activity to these views. While inactive, retain latest invalidation and skip native
render work; refresh once when active again. Transfers remain owned by their app
services. Reuse unchanged DLC button content when labels do not change.

### R2-UI-06 — P2: Failed overlay construction leaves global subscriptions alive

`OverlayWindow.xaml.cs:239–257` installs theme, visual-preference, media, and view
subscriptions before presenter/backdrop/native setup has completed. A presenter or
backdrop COM failure after `242` enters the constructor catch (`306–314`), which
only stops frames, removes the Closed handler, disposes the transparent-host hook,
and closes the HWND. It never marks the object `_closing`, detaches the global
subscriptions, or disposes its initialized material/motion/glow ownership.
Because `Closed -= OnTransparentHostClosed` occurs before `Close`, the normal
Closed callback also cannot set `_closing` on this branch.

`Services/OverlayWindowService.cs:967–990` stores the window only after its
constructor returns. Its topology-refresh catch leaves the process running, but
the failed object is absent from `_windows`, so later shutdown cannot retire it.
Global visual-preference/media events retain it across subsequent monitor rebuilds.
In the early failure interval before `_glow` is assigned (`254`), a later visual
preference event can enter `OnSystemVisualPreferencesChanged` and `UpdateGlowTarget`
with `_closing=false` and dereference that uninitialized field. Later failure
intervals retain completed resource owners instead. The App Services reviewer
independently confirmed the caller's ownership gap.

**Minimal fix:** use a constructor-safe, idempotent retirement routine shared with
normal shutdown: mark closing first; detach every subscription whose source was
initialized; stop/remove timers and callbacks; dispose initialized motion/material/
glow/transparent-host resources; then close the HWND. Cleanup must tolerate partial
initialization and continue after one cleanup failure.

### R2-UI-07 — P3: Main-window theme updates depend on tray construction

`MainWindow.xaml.cs:148–175` attaches `OnViewModelPropertyChanged` only after
`new NativeTrayService` succeeds (`166`). That handler (`438–444`) is the ongoing
main-window theme application path. The tray constructor has explicit icon-load
and native-subclass failure throws (`Services/NativeTrayService.cs:61–71`); the main
window catches them and remains usable. On that supported recovery branch, later
Theme settings persist but never update the visible main-window theme. Startup's
one-time `ApplyTheme` does not repair subsequent changes.

**Minimal fix:** attach the window's settings/theme observer during normal window
construction, independently of tray availability; retain the existing shutdown
detachment and nullable tray update.

## Round 1 and feature-path reread

The round 1 fixes were physically reread in the integrated snapshot. The journal
ownership, merge, bounded live projection, cancellation, and cursor paths led to
the new R2-UI-01/03 cases. Main Music's explicit host-active signal now reaches
MusicPage and MediaExpandedView through construction, section changes, tray hide,
minimization, language rebuild, retirement, and shutdown. Its inactive branch
cancels render/seek state; no repetition of R1-UI-02 is counted. All island player
views, rendering queue, clocks/highlighting/scrolling, lyrics cache rows, font-size
editing, AI/Qq/NetEase cards, and both complete locale files were read this round.
The DLC confirmation wording and off-thread payload-store prewarm were reread;
the former matches provider behavior and the latter remains awaited before
MainViewModel resolution.

Independent island themes, ThemeResources for outgoing/active content, priority
settings/editor notifications, monitor placement and editing, fullscreen/dismissal
generations, host/native-region geometry, glow handoff, drag handlers, constructor/
close paths, single-instance activation, first-run consent, recovery, and shared
shutdown completion were all covered. Service/native implementation coverage is
owned by the companion full Services/Infrastructure/Core partitions. The Services
reviewer also found synchronous repeated image-codec preflight from MainViewModel
quick-action evaluation; that finding belongs to the Services report, not this
report's seven-issue count.

Monitor-choice ItemsSource replacement and directly resolved imperative card
brushes remain native-dependent observations. Without framework/native evidence,
they are not counted as confirmed defects. The prior direct-brush observation is
not reused as a new round 2 finding.

## Limits and disposition

This partition performed source reads, caller traces, metadata/hash counting, and
this review-document write. After every round 2 source partition completed its full
read, the parent authorized minimal fixes. All seven findings now have source fixes
in the canonical worktree; the immutable review source was not changed. This
partition ran **zero tests, builds, fixtures, probes, app/native runs, or performance
measurements**, and performed no commits or remote mutations.

The fixes received focused source and diff review, including an independent
ViewModel overlap review. The authorized round 3 full-source reread must use a fresh
snapshot after the complete round 2 fix commit. This review does not establish
compilation, actual WinUI
layout/theme behavior, monitor/DPI/hit-test behavior, COM failure recovery, SMTC,
GPU execution, resource plateaus, or native performance.

## Focused fix evidence

The following references describe the fixed canonical source, before the parent
creates the round 2 fix commit. All seven findings are **fixed in source; runtime
validation remains outside this review**.

| Finding | Source disposition |
|---|---|
| R2-UI-01 | `MainViewModel.cs:789,805,819,1601,1642` retains eviction overflow in the bounded request-owned capture journal and derives the continuation cursor from its retained boundary. |
| R2-UI-02 | `MainViewModel.cs:1625,1629` queues one grouping refresh behind pending capture callbacks; it reelects loaded batch headers after insertion/update/trim and checks disposal. |
| R2-UI-03 | `MainViewModel.cs:789,859,1174,1642` gives every pending reload/page its own removal IDs, filters materialized pages and captures, and retires journals in `finally`. The returned removal token fences stale continuations. A token mismatch refreshes authoritative storage instead of mutating a newer projection. |
| R2-UI-04 | `MainPage.xaml.cs:230,278` captures navigation revision and page lifetime, rejects stale requests after each settings flush, and invalidates pending requests on retirement. `MainPage.Settings.cs:123` also fences the DLC shortcut's follow-on tab selection. |
| R2-UI-05 | `MainWindow.xaml.cs:141`, `MainPage.xaml.cs:215,2215`, and `MainPage.Settings.cs:113,131` compose host visibility/minimization, main section, and selected settings tab. `DlcPage.cs:77,90,109` and `DownloadPanel.cs:145,203,221,235` skip inactive render/dispatch and read latest service snapshots on activation. DLC button content is allocated once (`DlcPage.cs:212,277`). |
| R2-UI-06 | `OverlayWindow.xaml.cs:308,734,738` shares idempotent partial-construction retirement with normal shutdown. It sets closing first, checks initialized owners/tokens, isolates each unsubscribe/stop/disposal failure, and retires the window cancellation source. |
| R2-UI-07 | `MainWindow.xaml.cs:116` installs the theme/settings observer during window construction; tray creation no longer controls subscription. Existing shutdown detachment at `230` remains. |

Focused source traces covered empty/overlapping first-page snapshots, capture
journal eviction, removed retained boundaries, pending reload/load-more removal,
Undo restoration, replacement Remove/Pin finalization, and the unsuccessful Undo
branch that can suppress the state-event refresh. The independent ViewModel review
caught the stale removal continuation and suppressed-refresh cases; both were
reconciled before completing this disposition. Removal leaves the existing reload
revision intact, so its tombstones do not strand `IsBusy`.

The settings fixes were separately reviewed across startup, settings-tab and
main-section changes, host hide/minimize, pending callbacks, unload/reload,
language replacement, and retirement. Active settings refresh from the service's
current state; services continue owning transfers. The source-only check also
confirmed that MainWindow's caller uses the replacement presentation API and no
`SetMusicPresentationActive` references remain.

Overlay review traced failure before XAML owners/tokens exist, failure after each
global subscription, and normal shutdown. Motion-blur reset, timer stop, and the
global `CompositionTarget.Rendering` unsubscribe are separate cleanup stages, so
one COM failure cannot prevent the other stages. Both motion and composition
owners have idempotent `Dispose`; explicitly retiring composition also covers a
failed motion constructor. Queued glow/geometry/logo work checks closing or the
cleared snapshot, and quick-action continuations check closing before reading a
retired cancellation source's token.

The complete seven-file UI fix diff was read, original CRLF source endings were
preserved, and scoped `git diff --check` passed. These are static checks only.

## Full-read coverage

Every interval is `1..N`, including the lock file's unterminated last line.

| File | Physical lines | Reader |
|---|---:|---|
| `src/DropSpace.App/App.xaml` | 19 | r2_app_ui |
| `src/DropSpace.App/App.xaml.cs` | 975 | r2_app_ui |
| `src/DropSpace.App/Converters/BoolToVisibilityConverter.cs` | 23 | main_settings_full |
| `src/DropSpace.App/Converters/NullToVisibilityConverter.cs` | 23 | main_settings_full |
| `src/DropSpace.App/DropSpace.App.csproj` | 166 | r2_app_ui |
| `src/DropSpace.App/DropSpace.rc` | 33 | r2_app_ui |
| `src/DropSpace.App/MainWindow.xaml` | 22 | r2_app_ui |
| `src/DropSpace.App/MainWindow.xaml.cs` | 446 | r2_app_ui |
| `src/DropSpace.App/OverlayWindow.xaml` | 317 | r2_app_ui |
| `src/DropSpace.App/OverlayWindow.xaml.cs` | 2,377 | r2_app_ui |
| `src/DropSpace.App/Package.appxmanifest` | 54 | r2_app_ui |
| `src/DropSpace.App/Properties/AssemblyInfo.cs` | 3 | r2_app_ui |
| `src/DropSpace.App/Properties/launchSettings.json` | 7 | r2_app_ui |
| `src/DropSpace.App/Strings/en-US/Resources.resw` | 878 | r2_app_ui |
| `src/DropSpace.App/Strings/zh-CN/Resources.resw` | 878 | r2_app_ui |
| `src/DropSpace.App/ViewModels/ClipboardIslandViewModel.cs` | 94 | vm_full |
| `src/DropSpace.App/ViewModels/ItemCardViewModel.cs` | 208 | vm_full |
| `src/DropSpace.App/ViewModels/MainViewModel.cs` | 1,880 | vm_full |
| `src/DropSpace.App/ViewModels/MediaViewModel.cs` | 290 | vm_full |
| `src/DropSpace.App/ViewModels/NativeSettingsEditor.cs` | 149 | vm_full |
| `src/DropSpace.App/ViewModels/NeteaseEnhancementViewModel.cs` | 72 | vm_full |
| `src/DropSpace.App/ViewModels/OverlayViewModel.cs` | 511 | vm_full |
| `src/DropSpace.App/ViewModels/QuickActionButtonViewModel.cs` | 34 | vm_full |
| `src/DropSpace.App/ViewModels/SystemActivityViewModel.cs` | 31 | vm_full |
| `src/DropSpace.App/ViewModels/WidgetViewModel.cs` | 95 | vm_full |
| `src/DropSpace.App/Views/ContentDialogLifetime.cs` | 94 | main_settings_full |
| `src/DropSpace.App/Views/DesignTokens.xaml` | 68 | main_settings_full |
| `src/DropSpace.App/Views/Island/ClipboardIslandView.xaml` | 32 | r2_app_ui |
| `src/DropSpace.App/Views/Island/ClipboardIslandView.xaml.cs` | 18 | r2_app_ui |
| `src/DropSpace.App/Views/Island/ExpandedIslandMusicView.xaml` | 63 | r2_app_ui |
| `src/DropSpace.App/Views/Island/ExpandedIslandMusicView.xaml.cs` | 332 | r2_app_ui |
| `src/DropSpace.App/Views/Island/MediaCompactView.xaml` | 50 | r2_app_ui |
| `src/DropSpace.App/Views/Island/MediaCompactView.xaml.cs` | 348 | r2_app_ui |
| `src/DropSpace.App/Views/Island/MediaExpandedView.xaml` | 50 | r2_app_ui |
| `src/DropSpace.App/Views/Island/MediaExpandedView.xaml.cs` | 353 | r2_app_ui |
| `src/DropSpace.App/Views/Island/MediaRenderQueue.cs` | 48 | r2_app_ui |
| `src/DropSpace.App/Views/Island/WidgetsExpandedView.xaml` | 9 | r2_app_ui |
| `src/DropSpace.App/Views/Island/WidgetsExpandedView.xaml.cs` | 154 | r2_app_ui |
| `src/DropSpace.App/Views/MainPage.Settings.cs` | 124 | main_settings_full |
| `src/DropSpace.App/Views/MainPage.xaml` | 822 | main_settings_full |
| `src/DropSpace.App/Views/MainPage.xaml.cs` | 2,429 | main_settings_full |
| `src/DropSpace.App/Views/Music/AiLyricsSettingsCard.cs` | 507 | r2_app_ui |
| `src/DropSpace.App/Views/Music/LyricsFontSizeControl.cs` | 209 | r2_app_ui |
| `src/DropSpace.App/Views/Music/LyricsFontSizeEditSession.cs` | 125 | r2_app_ui |
| `src/DropSpace.App/Views/Music/LyricsGlowModeControl.cs` | 35 | r2_app_ui |
| `src/DropSpace.App/Views/Music/LyricsRowCollection.cs` | 65 | r2_app_ui |
| `src/DropSpace.App/Views/Music/MusicPage.cs` | 500 | r2_app_ui |
| `src/DropSpace.App/Views/Music/NeteaseEnhancementCard.cs` | 147 | r2_app_ui |
| `src/DropSpace.App/Views/Music/QqMusicLoginCard.cs` | 69 | r2_app_ui |
| `src/DropSpace.App/Views/Settings/DlcPage.cs` | 277 | main_settings_full |
| `src/DropSpace.App/Views/Settings/DownloadActionPanel.cs` | 45 | main_settings_full |
| `src/DropSpace.App/Views/Settings/DownloadPanel.cs` | 323 | main_settings_full |
| `src/DropSpace.App/Views/Settings/SettingsEditBehavior.cs` | 46 | main_settings_full |
| `src/DropSpace.App/Views/Settings/SettingsForm.cs` | 128 | main_settings_full |
| `src/DropSpace.App/Views/Settings/SettingsValueSlider.cs` | 91 | main_settings_full |
| `src/DropSpace.App/Views/Settings/WidgetEditorView.cs` | 337 | main_settings_full |
| `src/DropSpace.App/app.manifest` | 21 | r2_app_ui |
| `src/DropSpace.App/packages.lock.json` | 346 | r2_app_ui |
| **Total: 58 files** | **17,850** | **All complete** |

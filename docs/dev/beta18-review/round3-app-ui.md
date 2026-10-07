# Third whole-source review — App/UI

Frozen source: `/workspace/scratch/beta18-round3/src`, commit `f22bcc4bdd5c2d477a7880fe02c87f6035392ab9`. Scope: exactly **58 files / 17,998 physical lines** from `round3-app-ui-scope.txt`. This is the third sequential whole-source pass after the first and second pass fixes were frozen. All source bodies were newly read from the frozen snapshot; prior reports/diffs/searches were not used as coverage or as findings.

The App/UI owner physically read the following 36 files / 9,730 lines. Two independent children physically read all ten ViewModels (3,412 lines) and the twelve MainPage/Settings/design/dialog files (4,856 lines). Their fresh per-file evidence is included below. These sets are disjoint and exhaust all 58 scope paths. Relevant caller slices outside this partition were traced after whole-file reads and are not counted as additional full-source coverage.

`AGENTS.md`, canonical `.agents/skills/dropspace-maintainer/SKILL.md` and `references/app-ui.md` were read. Architecture/design sections were consulted for context. No tests, fixtures, probes, builds, native execution, commits, remote mutations or production edits were performed during the read/review phase. The authorized test ceiling remains 21 cases; actual executed cases remain **0**.

## Parent reader exact per-file evidence

Every span below was displayed as physical numbered source lines and read, including unchanged resources/project data. Small files were displayed completely in a numbered batch; large files were read in sequential `nl -ba`/`sed -n` chunks. One combined Island output clipped the middle of `MediaCompactView.xaml`; its entire 1–50 span was reread separately. No clipped span is counted as full coverage.

| File | Physical lines | Full-read spans | Fresh evidence reviewed |
| --- | ---: | --- | --- |
| `src/DropSpace.App/App.xaml` | 19 | 1–19 | Merged WinUI/design dictionaries and four visibility converters. |
| `src/DropSpace.App/App.xaml.cs` | 975 | 1–260; 261–530; 531–765; 766–975 | Launch/privacy/persistence before capture; DI graph; activation guards; staged shutdown and kept dispatcher. |
| `src/DropSpace.App/Converters/BoolToVisibilityConverter.cs` | 23 | 1–23 | Boolean/inversion conversion and unsupported reverse path. |
| `src/DropSpace.App/Converters/NullToVisibilityConverter.cs` | 23 | 1–23 | Null/inversion conversion and unsupported reverse path. |
| `src/DropSpace.App/DropSpace.App.csproj` | 166 | 1–166 | Pinned project graph, platform/deployment branches, embedded AI/CUDA/resource-index targets. |
| `src/DropSpace.App/DropSpace.rc` | 33 | 1–33 | Icon and version placeholders, debug flags and language translation. |
| `src/DropSpace.App/MainWindow.xaml` | 22 | 1–22 | Root theme foundation, title bar and MainPage presenter. |
| `src/DropSpace.App/MainWindow.xaml.cs` | 446 | 1–446 | Main root theme, host activity, language page retirement, owned privacy/close dialogs, tray and exit. |
| `src/DropSpace.App/OverlayWindow.xaml` | 317 | 1–317 | Fixed transparent host; material/fallback layers; music/files/activity pages; item actions/drop and placement edit. |
| `src/DropSpace.App/OverlayWindow.xaml.cs` | 2,405 | 1–280; 281–560; 561–840; 841–1120; 1121–1400; 1401–1680; 1681–1960; 1961–2230; 2231–2405 | Partial constructor rollback, per-host lifecycle, independent theme, snapshot/native geometry, motion/glow/seek hosts, drag generation, disposal. |
| `src/DropSpace.App/Package.appxmanifest` | 54 | 1–54 | OS baseline, localized package strings, logos, share formats and capabilities. |
| `src/DropSpace.App/Properties/AssemblyInfo.cs` | 3 | 1–3 | Internal visibility declaration. |
| `src/DropSpace.App/Properties/launchSettings.json` | 7 | 1–7 | Packaged launch profile. |
| `src/DropSpace.App/Strings/en-US/Resources.resw` | 878 | 1–300; 301–600; 601–878 | Every English data entry, format placeholder, XAML/automation key and new island/glow wording. |
| `src/DropSpace.App/Strings/zh-CN/Resources.resw` | 878 | 1–300; 301–600; 601–878 | Every Chinese data entry, format placeholder, XAML/automation key and new island/glow wording. |
| `src/DropSpace.App/Views/Island/ClipboardIslandView.xaml` | 32 | 1–32 | Clipboard list, action bindings, pause, error and navigation names. |
| `src/DropSpace.App/Views/Island/ClipboardIslandView.xaml.cs` | 18 | 1–18 | Visible owner registration and delegated copy/pin/remove/pause handlers. |
| `src/DropSpace.App/Views/Island/ExpandedIslandMusicView.xaml` | 63 | 1–63 | Bounded current lyric viewport, secondary/next lyric status and native playback controls. |
| `src/DropSpace.App/Views/Island/ExpandedIslandMusicView.xaml.cs` | 332 | 1–180; 181–332 | Coalesced render generations, translation viewport intersection, track seek retirement and acknowledgment timer. |
| `src/DropSpace.App/Views/Island/MediaCompactView.xaml` | 50 | 1–50 | Measured lyric canvas and clips, secondary scroll host, interlude dots, spectrum bars. |
| `src/DropSpace.App/Views/Island/MediaCompactView.xaml.cs` | 348 | 1–180; 181–348 | Host activity/render generation fences, immediate first-frame geometry, text measure invalidations, highlight/marquee. |
| `src/DropSpace.App/Views/Island/MediaExpandedView.xaml` | 50 | 1–50 | Main music playback/lyrics view, bounded viewport and command/accessibility bindings. |
| `src/DropSpace.App/Views/Island/MediaExpandedView.xaml.cs` | 353 | 1–185; 186–353 | Main seek activity guards, held preview/acknowledgment state, track retirement and rendering. |
| `src/DropSpace.App/Views/Island/MediaRenderQueue.cs` | 48 | 1–48 | Dirty/queued/generation ownership and immediate render consumption. |
| `src/DropSpace.App/Views/Island/WidgetsExpandedView.xaml` | 9 | 1–9 | Widget tiles/empty native view tree. |
| `src/DropSpace.App/Views/Island/WidgetsExpandedView.xaml.cs` | 154 | 1–154 | Loaded observer, visible-owner forwarding, tile rebuilding and snapshot/timer button rendering. |
| `src/DropSpace.App/Views/Music/AiLyricsSettingsCard.cs` | 507 | 1–260; 261–507 | AI/GPU/model consent, shared DLC status, glow latest selection, unload generation and retired task drain. |
| `src/DropSpace.App/Views/Music/LyricsFontSizeControl.cs` | 209 | 1–209 | Shared preview session, typed/pointer commits, debounce, unload and culture validation. |
| `src/DropSpace.App/Views/Music/LyricsFontSizeEditSession.cs` | 125 | 1–125 | Revision-gated latest-value write loop, explicit retry and observer isolation. |
| `src/DropSpace.App/Views/Music/LyricsGlowModeControl.cs` | 35 | 1–35 | Native enum selection ordering and guarded programmatic synchronization. |
| `src/DropSpace.App/Views/Music/LyricsRowCollection.cs` | 65 | 1–65 | Stable original index/identity and bounded row reuse across progressive translations. |
| `src/DropSpace.App/Views/Music/MusicPage.cs` | 500 | 1–250; 251–500 | Music host activity, restart/page cancellation, stable lyric rows, source allow-list/icon lifetime, settings/card construction. |
| `src/DropSpace.App/Views/Music/NeteaseEnhancementCard.cs` | 147 | 1–147 | Owned consent dialog and approved deployment lifetime, status/button projection. |
| `src/DropSpace.App/Views/Music/QqMusicLoginCard.cs` | 69 | 1–69 | Session subscription, check/sign-out control ownership and localized state projection. |
| `src/DropSpace.App/app.manifest` | 21 | 1–21 | Sparse identity, compatibility and per-monitor DPI declaration. |
| `src/DropSpace.App/packages.lock.json` | 346 | 1–346 | Both target dictionaries; direct/transitive/project resolutions, dependency versions and hashes. |

## Confirmed new findings

### R3-UI-01 — Inactive loaded widget views continue rebuilding/rendering

`Views/Island/WidgetsExpandedView.xaml.cs:32–36` forwards activity only to the shared `WidgetViewModel` and keeps a `PropertyChanged` subscription whenever the view is loaded. There is no view-local activity check. Consequently a previously displayed Widgets page continues calling `RenderData`/`Rebuild` after the island selects Music/Files/Clipboard or empties the native region. `OverlayWindow.xaml.cs:703`, `1016` and `1072` correctly send `SetActive(false)`, but that call only removes a sampling owner and does not fence this view's observer work.

The concrete publisher path is `NativeWidgetDataService:35–77` (one-second samples while any owner is active) → `WidgetViewModel:87` (Snapshot and StopwatchText notifications) → `WidgetsExpandedView:35–36`. `OverlayWindowService:946–964` constructs one view per monitor with the same WidgetViewModel; `921–926` activates one monitor while hiding the others. Thus an inactive loaded view can also render every publication from a different visible monitor. Ordinary settings changes publish Layout/Enabled/ClipboardPauseLabel (`WidgetViewModel:80–85`) and rebuild the hidden tile tree twice.

Minimal fix: retain a local active state plus separate structure/data invalidation flags; inactive or unloaded observers retain dirty state without touching the visual tree, and reactivation consumes the latest layout/data. Keep shared owner registration for sampling and unregister on unload. This is a confirmed source workload/lifecycle defect; no latency, CPU, frame-rate or runtime performance improvement is claimed.

### R3-UI-02 — Programmatic card brushes bypass destination theme resources

The independent Main/Settings reader confirmed direct `Application.Current.Resources` brush assignments in SettingsForm, DLC, Downloads and widget editor surfaces, plus a fixed gray Quick Actions description. The parent fresh reads confirm the same card background/stroke ownership in `Views/Music/MusicPage.cs:341–347`, `AiLyricsSettingsCard.cs:121–124`, `LyricsFontSizeControl.cs:82–83`, `NeteaseEnhancementCard.cs:43–46` and `QqMusicLoginCard.cs:49–52`.

MainWindow theme changes only its content root's `RequestedTheme` (`178–189`, `438–444`). The programmatic local brush values retain application-context lookup rather than destination-element `{ThemeResource}` expressions; controls can inherit the explicit Light/Dark window theme while those surfaces retain the application-default palette. This also bypasses the resource-expression refresh semantics used by XAML surfaces. The child report below gives the Settings construction/edit/publication caller chain and exact owned locations. No actual pixels or contrast were measured.

Minimal fix: shared declarative styles with ThemeResource setters for cards, normal/selected widget surfaces and secondary foregrounds; apply the styles to the programmatic elements. Do not mutate a globally shared brush to match one window, because Main and Island may use independent themes. The fixed gray description should use the same theme-aware secondary foreground mechanism.

## Focused requested feature and glow trace

- Independent island appearance: `OverlayWindow.ApplyTheme:329–340` sets only `Root.RequestedTheme`; Follow maps to `ElementTheme.Default`, preserving WinUI's existing Windows Apps appearance source (including Windows Custom mode). MainWindow independently sets its own root theme at `178–189`. The island's `ActualThemeChanged` observer at `342–348` retires captured Acrylic motion and reapplies material, while XAML content uses ThemeResource. Direct programmatic card-resource observations are separately evaluated below.
- Shared Music/Files preference: the MainPage settings binding and OverlayViewModel forwarding are covered by the independent child reports. Defaults/persistence arbitration belong to the independently whole-read Core/Infrastructure partitions; this UI pass does not replace their evidence.
- Glow controls: AiLyricsSettingsCard `116–119` persists SimplifiedGlow through SettingsForm; `303–335` persists Off/AiLyrics/Music using the latest pending selection, independent of AI enabled state. LyricsGlowModeControl `14–32` maps native indices directly to the three enum values. `OnEnabled:219` applies the documented default mode only when the AI toggle changes.
- Glow presentation: OverlayWindow `826–892` checks real host visibility/activity/native safety, non-idle variant, high contrast/effects, `_glow.IsAvailable`, playing/title, opacity and Core policy. Music mode does not require translated lyrics; AiLyrics requires an actually visible LocalAi translation. SimplifiedGlow is passed only as the final renderer shape input at `890`, with no eligibility gate. The controller/audio ownership forwarding was freshly traced in the ViewModel child section.
- Both Simple and Regular being unavailable is not explained by a UI selector branch. Focused caller read of IslandGlowController `178–185` shows any native render failure permanently sets `_failed` and disposes the companion window for that controller lifetime; this observation was passed to the App/services owner for independent validation/disposition. The source alone does not prove the user's exact machine failure or certify a visual fix.

## Fresh R2 mechanism checks and excluded observations

- OverlayWindow constructor `163–315` fences partial construction through idempotent per-resource retirement, including initialized preference/theme/media observers, timers, native hook, material/motion/glow owners and Closed handling. Native geometry recovery remains constrained by backoff and same-HWND validity (`1043–1060`). No new constructor/cleanup defect was confirmed.
- The active Island media views use generation-bound frame handlers and hidden dirty state; `RefreshForPresentation` consumes invalidation synchronously before the first geometry selection. Host panel callbacks update active rendering, and expanded seek state is canceled on inactive/unloaded/track transitions. MediaRenderQueue's old callback cannot clear a newer generation.
- Glow capture/handoff evaluation uses the current track/policy before restoration, and hidden/placement/fullscreen paths clear the transfer. No UI mode-only eligibility regression was established.
- Lyric rows preserve original-line indices; progressive secondary-text changes update existing rows. Icon/restart jobs carry owned cancellation and retired-page generations. No stale main Music restart publication was established.
- Theme brush snapshots in programmatic Settings/Music cards were investigated independently by the Main/Settings child; its evidence/disposition is appended below.
- `App.ShutdownCoreAsync` keeps the last HWND/dispatcher alive while service cleanup drains. Some public mutators and untracked short UI awaits were considered; without an escaping caller/failure path they are not promoted to findings.
- Unsafe native show may be followed by content preparation that makes local child panels Visible. This is not claimed as the user's missing glow cause or a measured performance defect; the window safety path still keeps the HWND hidden.
- Native high contrast/effects changes, layered-window Z order, glow pixel visibility, input pass-through, DPI/work-area geometry, WinUI resource resolution, seek acknowledgment scheduling and exit behavior remain unexecuted. Static source reads do not certify visual/native behavior.


## Independent full-file ViewModel evidence

### Fresh ViewModel reader record

Review source: frozen `/workspace/scratch/beta18-round3/src`, identified by the parent task as commit `f22bcc4bdd5c2d477a7880fe02c87f6035392ab9`. The frozen `src` directory is the physical read root; repository-relative paths below include the conventional `src/` prefix. No previous report, fixture, probe, test result, or finding was reused as coverage or as a finding.

Instructions physically read: `/workspace/DropSpace/AGENTS.md`, canonical `/workspace/DropSpace/.agents/skills/dropspace-maintainer/SKILL.md`, and its `references/app-ui.md`. Relevant architecture/design orientation was read, but is not counted as scope coverage.

## Exact full-read evidence

`wc -l DropSpace.App/ViewModels/*.cs` reported the following 10 files and **3,412 total lines**. Every numbered line from 1 through the last line was physically read using `nl -ba`, with `sed -n` chunks for the two larger files.

| Repository-relative file | Lines | Complete physical-read chunks |
| --- | ---: | --- |
| `src/DropSpace.App/ViewModels/ClipboardIslandViewModel.cs` | 94 | 1–94 |
| `src/DropSpace.App/ViewModels/ItemCardViewModel.cs` | 208 | 1–208 |
| `src/DropSpace.App/ViewModels/MainViewModel.cs` | 1,928 | 1–240, 241–480, 481–720, 721–960, 961–1200, 1201–1440, 1441–1680, 1681–1928 |
| `src/DropSpace.App/ViewModels/MediaViewModel.cs` | 290 | 1–290, separate complete reread |
| `src/DropSpace.App/ViewModels/NativeSettingsEditor.cs` | 149 | 1–149 |
| `src/DropSpace.App/ViewModels/NeteaseEnhancementViewModel.cs` | 72 | 1–72 |
| `src/DropSpace.App/ViewModels/OverlayViewModel.cs` | 511 | 1–260, 261–511 |
| `src/DropSpace.App/ViewModels/QuickActionButtonViewModel.cs` | 34 | 1–34 |
| `src/DropSpace.App/ViewModels/SystemActivityViewModel.cs` | 31 | 1–31 |
| `src/DropSpace.App/ViewModels/WidgetViewModel.cs` | 95 | 1–95 |

The initial combined Clipboard/ItemCard/Media/NativeSettings output was truncated inside Media lines 98–114. Media was then reread separately, completely, lines 1–290 with no truncation. The other files' complete spans were visible, and no truncated span is counted as coverage.

## Confirmed new findings

**None confirmed in these 10 ViewModels from this fresh static read.** This conclusion is limited to the ViewModel implementations and the caller slices traced below; it is not a Windows runtime qualification or an assertion that the wider application has no defects.

## Caller tracing and fresh integrity observations

- Main collection acquisition: `MainPage.xaml.cs` 250–299 calls incremental loading/navigation. `MainViewModel.ReloadAsync` 781–844 checks reload revision, query identity, disposal and cancellation before replacement; per-load tombstones protect delayed results. `LoadMoreItemsAsync` 846–888 uses its own journal and load gate. `ApplyCapturedItem` 1584–1627 keeps reload captures bounded, updates by identity, and batches regrouping. `ApplyBatchProjectionState` 1142–1163 selects a persisted index-zero header when available and one loaded fallback otherwise. These mechanisms were read directly in the frozen source, independently of any R2 report.
- Settings editing: `SettingsForm` callers and `DownloadPanel` flush callers were located; `MainPage.SelectSectionAsync` 278–299 flushes both queues and checks navigation ownership after each await. `NativeSettingsEditor` 32–100 carries queued/in-flight ownership counts so an older save's notification does not overwrite protected controls. `UpdateAsync` 116–134 evaluates the change under its save semaphore. `MainViewModel` 1287–1332 serializes committed publication, and `SettingsApplicationCoordinator` 87–225 loads current persistence, merges changed fields, rolls back runtime stages, then recovers the persisted snapshot. Timestamp publication at Main 1852–1875 preserves preference identity. These current code paths support the earlier fix intent without reusing earlier results.
- Main exit/background ownership: `MainWindow.PrepareForShutdown` 220–233 retires the page and cancels Main before service disposal; `App.ShutdownCoreAsync` 417–465 stops media/overlay/window work before DI cleanup. Main 1403–1457 cancels its query/lifetime and drains its tracked thumbnail, search, storage, batch-drag and undo-refresh tasks. Already accepted capture/update/status callbacks check `_disposed` before publication.
- Overlay refresh/acknowledgement: Overlay 406–439 applies recent rows on the dispatcher using `ProjectionCollection.SynchronizeById`; 469–482 leaves the count notification passive so `SpaceProjectionChanged` owns refresh. Its shell acknowledgement 305–372 clears the owning cancellation field before disposing the local source and does not clear a replacement acknowledgement. Dispose 385–404 unsubscribes and cancels pending acknowledgement/coordinator work.
- Island appearance and shared Music/Files preference: Main carries the full settings snapshot. Overlay `ContentPriority` 83–84 reads pending/current `IslandContentPriority`, and its notification diff 486–498 includes that preference. `MainPage.Settings.cs` 44–56 binds island appearance independently through `IslandAppearance.Theme` and the Music/TemporarySpace choice. `SettingsChangePolicy.MergeIsland` 66–76 merges island fields independently of `Theme`. `MediaExperienceService` 299–310 receives the Main settings snapshot; its dispatcher publication 375–402 updates `MediaViewModel.Settings`; `OverlayWindowService.OnMediaSettingsChanged` 646–653 applies `IslandAppearance.Theme`. The default Follow Windows *source* is outside these ViewModels and remains assigned to the parent/native appearance review; it was not inferred from this forwarding path.
- Visibility/native sampling ownership: Media 39–53 uses separate UI-owned sets for presentation versus island glow; MainWindow 141–146 contributes only presentation visibility, while OverlayWindow clears both owners during disposal/suppression. Widget 69–94 serializes visible-state requests through a one-slot channel and stops sampling on disposal. `NativeWidgetDataService` 17–92 was read to confirm sampling starts/stops under its lifecycle gate. ClipboardIsland 38–43 reference-counts visible owners, serializes refresh through a bounded channel and cancels/drains its worker on disposal. NeteaseEnhancement 57–71 dispatches state changes and rejects accepted callbacks after disposal.

## Focused glow trace requested during this pass

No ViewModel branch distinguishes Simple from Regular as an eligibility condition. Media 39–45 publishes `IsIslandGlowActive` when the aggregate owner state changes, while 184–194 publishes updated settings. `MediaExperienceService.OnPresentationChanged` 728–731 subscribes to that owner property, queues the coordinator and updates frame sampling. The coordinator's source request at 435 is playing media plus either visible spectrum presentation or island glow ownership. `LyricsGlowPolicy` 20–30 admits Music while playing/visible, and admits AiLyrics only with a visible LocalAi translation. `OverlayWindow.UpdateGlowTarget` 826–858 first gates native visibility, non-idle presentation, accessibility/effects and `_glow.IsAvailable`, then computes policy eligibility; 868 publishes the owner flag. `SimplifiedGlow` is passed only to `_glow.SetTarget` at 889–890. Therefore a report that both Simple and Regular cannot open is not explained by a mode exclusion or dropped owner notification in the ViewModel. Controller/native availability and actual Windows policy/geometry still require the separate owning review.

## Excluded observations and limits

- Language-change item-label staleness was considered but excluded: `MainWindow` 123–138 retires/recreates MainPage when the language preference changes, rebinding the collection rows. It is not established by the lack of item-label notifications alone.
- `MainViewModel.Settings` 346–349 rebuilds each loaded card's primary actions on every changed settings snapshot, including update metadata. This is a static workload observation, not a confirmed user-visible performance defect without a measured qualifying loaded-item case; no performance claim is made.
- `MediaExperienceService` publishes a new session and renders a frame at 375–402 before the selected audio source transition at 435 onward. A cached fresh spectrum may deserve a source-identity trace across selected-player changes. This was passed to the parent as an unconfirmed cross-file observation; the source/capture ownership implementation was not fully assigned to this reader and no VM defect is claimed.
- Shutdown paths may intentionally flush settings after Main has retired. The absence of a disposed guard on every public mutator was not treated as a finding without a caller path that escapes the application lifetime.
- Other App/Core/Infrastructure files were read only in focused caller/context slices (except the short `DispatcherQueueExtensions`, `LyricsGlowPolicy`, `SettingsChangePolicy`, `NativeWidgetDataService`, and `ClipboardIslandView` files, whose complete displayed bodies were inspected). These do not enlarge the assigned full-read coverage.
- No tests, fixtures, probes, builds, native execution, remote mutations, commits or production edits were performed. Only this scratch review section was written. Real Windows dispatch scheduling, high contrast/effects behavior, process-loopback availability, render readiness, DPI/geometry, native glow availability and shutdown runtime behavior remain unexecuted.

## Independent full-file Main/Settings evidence

### Fresh Main/Settings reader record

Frozen source: `/workspace/scratch/beta18-round3/src`, supplied commit `f22bcc4bdd5c2d477a7880fe02c87f6035392ab9`. This directory is a source export, without `.git`; commit identity was supplied by the task, not independently recovered with `git rev-parse`.

Instructions physically read: `/workspace/DropSpace/AGENTS.md`, canonical `/workspace/DropSpace/.agents/skills/dropspace-maintainer/SKILL.md`, and its `references/app-ui.md`. `DESIGN_SYSTEM.md` was also read in full for theme/UI context. No earlier audit reports were read or reused for coverage or findings.

## Exact full-file coverage

Paths below are relative to `DropSpace.App/Views/`. All ranges are inclusive. Every owned file was physically read from line 1 to its final line with numbered source; the twelve files total **4,856 lines**. The corrected scope contains seven Settings files.

| Owned file | Lines | Read ranges | Tool output chunk evidence |
|---|---:|---|---|
| `MainPage.Settings.cs` | 143 | 1–143 | `2a04b1` |
| `MainPage.xaml` | 822 | 1–210, 211–430, 431–630, 631–822 | `d69185`, `cb22d9`, **`f2286b`** (complete reread of 431–630 after aggregate output truncated `d9d7a6`), `006c14` |
| `MainPage.xaml.cs` | 2450 | 1–250, 251–500, 501–750, 751–1000, 1001–1250, 1251–1500, 1501–1750, 1751–2000, 2001–2250, 2251–2450 | `97b032`, `c9fdb9`, `ee5719`, `4e18f5`, `76ee73`, `c40ec6`, `dcbb7a`, `ac41b2`, `2f3434`, `a5dcf0` |
| `ContentDialogLifetime.cs` | 94 | 1–94 | `b2969b` |
| `Settings/DlcPage.cs` | 290 | 1–160, 161–290 | `f79df6`, `f59ade` |
| `Settings/DownloadActionPanel.cs` | 45 | 1–45 | `e223f2` |
| `Settings/DownloadPanel.cs` | 342 | 1–180, 181–342 | `03bc2f`, `2e948e` |
| `Settings/SettingsEditBehavior.cs` | 46 | 1–46 | `e223f2` |
| `Settings/SettingsForm.cs` | 128 | 1–128 | `e223f2` |
| `Settings/SettingsValueSlider.cs` | 91 | 1–91 | `e223f2` |
| `Settings/WidgetEditorView.cs` | 337 | 1–180, 181–337 | `1f36da`, `87c039` |
| `DesignTokens.xaml` | 68 | 1–68 | `27952e` |

Line counts were independently collected by `wc -l` (`d9f6df`). No file coverage is inferred from search hits or other owners' reports.

## Confirmed new finding

### R3-UI-MS-01 — Programmatic Settings brushes do not follow the main window's effective theme

Severity: medium. Confidence: high for the resource ownership defect; actual contrast and pixels were not measured.

Affected owned locations:

- `Settings/SettingsForm.cs:83,124`: slider-row and generic-row card backgrounds directly capture `(Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]`.
- `Settings/DownloadPanel.cs:290–295`: custom-download, settings and task cards capture application background/stroke brushes.
- `Settings/DlcPage.cs:228–234`: package cards capture the same application brushes.
- `Settings/WidgetEditorView.cs:73,79,99,130,234,248,336`: grid, preview, placed-widget and selection/size surfaces read brushes through the application-level `Brush` helper. Rebuilds retain placed controls and existing grid cells; refreshing those parts does not establish theme-resource expressions.
- `MainPage.xaml.cs:1905`: the generated Quick Actions description uses a fixed gray foreground, likewise bypassing the effective theme/high-contrast palette.

Cause and production caller chain:

1. `MainPage.xaml.cs:127` calls `BuildSettingsPages`, which constructs the forms, widget editor, DLC page and download panel (`MainPage.Settings.cs:44–87`) once for the page.
2. General → Theme changes call `OnThemeChanged` (`MainPage.xaml.cs:1639–1645`) → `MainViewModel.UpdateSettingsAsync` (`ViewModels/MainViewModel.cs:1298–1331`) → settings transaction/publication.
3. `MainWindow.OnViewModelPropertyChanged` (`MainWindow.xaml.cs:438–444`) calls `ApplyTheme`, which sets the window content root's **element** `RequestedTheme` to Light/Dark/Default (`178–187`). It does not change `Application.RequestedTheme` or rebuild these settings controls.
4. Direct `Application.Current.Resources` lookup is an application-context brush lookup and an ordinary local assignment, not a `{ThemeResource}` expression evaluated for each destination element. The manually selected main theme can therefore update inherited foregrounds/native controls while the generated surfaces retain the application-default palette. This also fails to give these assignments the theme-expression refresh semantics used by declarative surfaces.

Concrete trigger: start with the Windows app appearance dark, choose General → Theme → Light, then open Island, DLC, Downloads or Widgets. The controls inherit the light main-window theme while the direct application resource assignments remain tied to application resource resolution. The reverse override is affected too. This is a source-level defect, not a claim that this sequence was executed or visually measured.

Minimal fix: move programmatic surface background/stroke/foreground assignments to shared declarative styles/templates with `{ThemeResource ...}` setters, applied to each element. Replace the fixed gray description with `TextFillColorSecondaryBrush` through a theme-aware style. Give normal/selected widget states theme-aware styles rather than refreshing through an application-resource lookup. `DesignTokens.xaml:57–59` already demonstrates a style with a ThemeResource setter; the same mechanism can support cards and widget states. Do not mutate globally shared brushes to follow one window, because the main window and Island may intentionally use different themes simultaneously.

The finding covers only the freshly read locations listed here. Other UI owners may consolidate additional locations supported by their own reads.

## Fresh checks of the requested behavior and revised ownership paths

- Island controls expose exactly Follow Windows, Light and Dark, and write `IslandAppearance.Theme`, not main `Theme` (`MainPage.Settings.cs:45–51`). `SettingsForm.AddChoice` captures the selected value and sends a latest-settings mutation through `NativeSettingsEditor.UpdateAsync` (`SettingsForm.cs:49–55`; `ViewModels/NativeSettingsEditor.cs:116–133`).
- The traced Island application uses `OverlayWindow.Root.RequestedTheme` and `ElementTheme.Default` for Follow Windows (`OverlayWindow.xaml.cs:329–339`), preserving WinUI's Windows app-appearance source. Its root registers `ActualThemeChanged` (`239`) and refreshes the material/motion state (`342–347`). `DesignTokens.xaml:6–43` supplies separate Light, Dark and HighContrast dictionaries. No new defect was established in this independent Island theme route.
- New defaults are System for `IslandAppearance.Theme` (`Core/Models/NativeIslandSettings.cs:60`) and Music for `IslandContentPriority` (`Core/Models/AppSettings.cs:78`). `IslandAppearanceMigration.cs:9–18` preserves an explicit Island theme and maps a missing legacy Island theme from the former main override. The settings menu writes the shared priority field (`MainPage.Settings.cs:52–56`).
- `SettingsChangePolicy.Merge` merges Island appearance field by field and priority separately (`Core/Models/SettingsChangePolicy.cs:25–26,66–76`), so an unrelated main preference edit does not itself remap a manual Island choice.
- Traced `IslandExperienceCoordinator.UpdateContentPriority` updates the default only on a changed preference and clears the default override as an explicit action (`Core/Island/IslandExperienceCoordinator.cs:43–51`). Passive media refresh only clears a Music selection when its content ceases to exist (`63–73`); `SelectPage` records an explicit selection (`86–87`); reconciliation respects an available selected page (`109–113`). `IslandContentSelectionPolicy.cs:9–14` centralizes Music/Files default resolution. The overlay service invokes this shared coordinator at construction and settings updates (`Services/OverlayWindowService.cs:123,693`). No new manual-choice-preservation defect was established by this trace.
- Navigation revision/cancellation is checked after both edit flushes (`MainPage.xaml.cs:278–298`), and Open DLC verifies the requested navigation revision/section before selecting its subpage (`MainPage.Settings.cs:121–128`).
- Clipboard-limit sliders skip refresh while their pointer or edit key is owned (`MainPage.xaml.cs:2150–2154`); generic form sliders do the same (`SettingsForm.cs:85–99`), and download limits protect active/pending ownership (`DownloadPanel.cs:208–213`). The editor tracks both queued and saving edit keys and raises a final Settings refresh after saving ownership retires (`NativeSettingsEditor.cs:68,82–99`).
- DLC and Downloads have explicit active-page presentation gates (`MainPage.Settings.cs:131–134`; `DlcPage.cs:77–111`; `DownloadPanel.cs:145–149,219–248`), with Loaded/Unloaded subscription ownership. DLC operations remain manager-owned beyond page lifetime, while page cancellation fences confirmation acceptance (`DlcPage.cs:142–186`).
- Root-level dialog serialization retains the gate until native ShowAsync completion after caller cancellation (`ContentDialogLifetime.cs:34–57`), and dismissal handles unavailable native UI/dispatcher retirement (`80–92`). Main-window shutdown retires the root and page (`MainWindow.xaml.cs:220–231`). No new leak/deadlock was confirmed in this owned dialog helper.
- PDF creation disposes a failed host; page rendering owns each PdfPage and output stream; media creation disposes a failed player; dialog completion disposes disposable preview content (`MainPage.xaml.cs:784–820,914–987,1070–1083,1086–1107,1369–1375`). The WinRT PDF document has no explicit Close member in this target projection. These are source ownership observations, not proof of native exit timing.

## Excluded observations and limits

- `App.ShutdownCoreAsync` stops DLC/download/system activity/media services before `MainWindow.PrepareForShutdown` and later service-container disposal (`App.xaml.cs:434–465`); `NativeSettingsEditor.DisposeAsync` flushes edits at disposal (`147–148`). This was sent to the parent as a cross-owner trace observation. Without proving a concrete failure against those service lifetimes, it is **not** a new finding here.
- PDF cancellation can overlap platform rendering and stream cleanup. The read shows managed cancellation/publication fencing, but no native failure was demonstrated; no native resource race finding is asserted.
- Fixed-width legacy settings rows, large-text wrapping and `SettingsValueSlider`'s logarithmic native range were read. No Windows layout/automation run was authorized, so no clipping, automation-range accuracy or accessibility-performance claim is asserted from possible appearance alone.
- Failed-save rollback, language replacement, pending widget edits and notifications were traced to the editor/transaction boundaries. No additional confirmed defect was established; possible timing sequences without a demonstrated source invariant violation were not promoted to findings.
- No tests, fixtures, probes, builds, native execution, performance measurements, remote mutations, commits or production edits were performed. Only this scratch read report was written. Caller reads outside the twelve-file ownership were focused ranges/searches, except the small `NativeSettingsEditor` and theme-selection support files shown in the tool output; they are not counted as ownership coverage.

## Fresh Beta 16 / Beta 17 animation and glow comparison

After all 58 App/UI files had been read, the user's report that Beta 16 had working glow and Island animations while Beta 17 had neither prompted a focused historical source comparison. The comparison used local immutable tags `v0.3.1-beta.16`, `v0.3.1-beta.17`, and the third-round commit `f22bcc4bdd5c2d477a7880fe02c87f6035392ab9`; it did not reuse an earlier review or execute a test, probe, build, native call or performance measurement.

- The complete Beta 16 → Beta 17 `src` diff contains twelve changed files. In the shared Island effects path, `OverlayWindow.xaml.cs` adds media ancestor visibility ownership, immediate presentation refreshes and a compact-geometry preparation guard. `SystemVisualPreferenceService.Resolve` switches from calling `ReadPreferences` to projecting a requested Full/Reduced/System motion mode over `Current`. The motion controller/orchestrator/composition animator, native glow controller/window/rasterizer, region/show implementation and timer/display-frame scheduler are unchanged in that tag interval.
- The shipping expanded view is `ExpandedIslandMusicView`, selected by `OverlayWindow.xaml:120`; the separately retained `MediaExpandedView` is not that XAML host. `MediaCompactView.SetActive` and `ExpandedIslandMusicView.SetActive` only change their own presentation eligibility, cancel their own queued rendering handlers and seek work, and publish translation visibility. Their generation-bound callbacks unsubscribe exactly the delegate they installed. They do not stop the overlay animation scheduler, write visual preferences, retire the glow controller or hide the native owner.
- `RefreshForPresentation` cancels the corresponding media callback and consumes retained dirty state synchronously before the host reads its target geometry. The compact refresh runs under `_preparingMediaGeometry`; `OnMediaGeometryChanged` declines that synchronous callback and preserves the existing queued invalidation. Translation visibility callbacks lead to `UpdateGlowTarget`, not `StopAnimationFrames`. Subsequent `PrepareContentForTarget` explicitly restores the media ancestor visibility ownership even when the panel's initial Visibility was already Visible. This trace does not establish an all-animation disable in those changes.
- The preference service constructor's initial `ReadPreferences(System)`, event subscriptions and repeating 250 ms dispatcher poll are unchanged. `App.BuildServices` supplies the UI `DispatcherQueue` at line 523. The current settings motion preference still comes from `OverlayViewModel.MotionPreference`, and explicit Full/Reduced overrides are applied by `Resolve`. Thus the historical change introduces cached motion reads, but the shipping source does not prove that `Current` remains stale.
- Beta 16 already gates glow on `_visualPreferences.Current` in `UpdateGlowTarget`, while the changed `Resolve` affects motion resolution. Cached `Resolve` alone therefore does not establish a newly disabled glow path. Both versions disable motion/glow together if the actual shared system snapshot reports reduced animation and disabled advanced effects; the existing grouped native-preference read also maps a preference-getter failure to that fallback. No observed Windows preference value or getter exception was available in this source-only review, so neither condition is asserted as the user's shipping cause.
- Beta 17 → the frozen third-round snapshot adds the independent Island theme route, theme-triggered material/motion-blur retirement, shared default content priority and comprehensive partial-construction cleanup. The animation scheduler and glow native renderer still have no corresponding source change. Those changes were cross-checked afresh in the full-file read; no alternate animation engine or speculative production change was introduced.

Result: the source comparison narrows the suspected shared gate and excludes a direct media-view cancellation of all Island animations, but it **does not establish the user's exact shipping regression cause or certify a visual correction**. The missing-glow and missing-animation report remains unresolved by static evidence in this partition. Core and App/services owners received the caller evidence for their independent shared-gate and native-resource traces.

## Authorized fixes after third-round full coverage

The production fix phase began only after the parent confirmed fresh complete third-round coverage of all 425 App/Core/Infrastructure files and explicitly authorized these two confirmed UI findings. The frozen 58-file / 17,998-line read record above remains the evidence for the reviewed snapshot; production changes listed here occurred afterward.

### R3-UI-01 implemented — Retain inactive widget invalidation

`Views/Island/WidgetsExpandedView.xaml.cs` now owns a host-local `_active` flag and separate structure/data dirty flags (`18–21`). ViewModel replacement, load/unload and notifications record invalidation rather than directly rebuilding or rendering. `RenderPending` (`86–102`) returns while this host is inactive or unloaded; reactivation consumes the latest structure and data once, or only updates the existing data controls when the layout is unchanged. `SetActive` preserves the requested presentation state, while `WidgetViewModel.SetVisible` receives only loaded active ownership (`46–65`). Unload and ViewModel replacement remove the old sampling owner, and subscription helpers prevent duplicate loaded observers (`67–77`). The shared native data service and its existing reference-counted sampling protocol are unchanged.

Source review confirms that hidden notifications now reach dirty flags and the active/loaded gate before either `Rebuild` or `RenderData`; no frame scheduler, animation/glow logic or alternate widget renderer was added. The existing Island tile ThemeResource style is retained. This is a control-flow correction, not a measured frame-time claim.

### R3-UI-02 implemented — Resolve programmatic visuals at the destination theme

`Views/DesignTokens.xaml:57–85` adds eight declarative styles with ThemeResource setters: card background/stroke, secondary text, widget editor cells, drop-preview stroke, normal/selected widget buttons and normal/selected size previews. The selected styles inherit the ordinary styles and override only the relevant accent brush.

The confirmed application-context brush captures were replaced with style assignments in `SettingsForm`, `DownloadPanel`, `DlcPage`, `WidgetEditorView`, `MusicPage`, `AiLyricsSettingsCard`, `LyricsFontSizeControl`, `NeteaseEnhancementCard`, and `QqMusicLoginCard`. The Quick Actions description in `MainPage.xaml.cs:1905` now uses the secondary-text style. Programmatic code still looks up the shared **style**, whose declarative ThemeResource setters retain destination-element theme refresh semantics; it no longer captures the corresponding application-context brush as a local value. Widget selection changes switch the theme-aware style on the existing control. Geometry, local padding/thickness and control instances remain intact; no global brush mutation or whole-window rebuilding was introduced.

### Static checks and final scope

- Independently reviewed the complete 12-file UI production diff: 118 insertions and 35 deletions.
- Scoped `git diff --check` completed cleanly.
- Standard-library XML parsing accepted `DesignTokens.xaml`; source/reference inspection resolved all eight newly used style keys and both selected-style BasedOn references. Original CRLF endings were preserved in the changed source files.
- Source inspection found no remaining raw application brush capture in the ten affected imperative card/settings source files. Native resource lookup, actual theme/high-contrast rendering and widget scheduling were not executed; XML/reference checks do not substitute for a WinUI build or native visual validation.
- **Executed test cases: 0. Builds, fixtures, probes, native execution, performance measurements, commits and remote mutations: 0.**

The user subsequently confirmed that the disabled animation/glow behavior came from the reduced-dynamic-effects setting and explicitly cancelled those fixes. No animation/glow source was changed in this fix phase. A later lyrics no-match wording request is owned separately by the Core/media reader and is not included in the two-finding UI diff or validation count above.

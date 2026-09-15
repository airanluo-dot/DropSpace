# Preview.24 rebuild execution ledger

## September 14 amendment — active

All settings must apply immediately without saved-success notices or Save/Apply
buttons; only island position retains explicit confirmation. Failures still
need visible errors. Recheck real widget dragging, including visible movement
and capture/scroll handling. Continue the original full release plan.
Checkpoint `ecf732a` contains the phase 7 work and September 13 widget changes;
it is not a phase/release completion. Current uncommitted work adds scaled host
geometry (12 placement tests passed), removes settings success messages and
widget Apply, and adds pointer-following widget visuals. Latest App build passes
with zero warnings/errors. Native follow-up: language switching to English
updates all seven settings tabs without restart and remains responsive.
Destroying/recreating island HWNDs for language caused Microsoft.UI.Input.dll
fast-fail 0xc0000409; refreshing the same windows fixes it. Widget drag from row
0 to row 2 persists, and selecting 1x1 saves immediately without Apply. Normal
Button capture release must not cancel the drop before routed PointerReleased.
Two-times expanded Files/Widgets fit the enlarged host (`expanded-scale-two.png`,
`widgets-scale-two.png`). Date formatting now uses the selected app culture.
Localization gate passes: 555 keys, 226 imperative references, 181 XAML IDs.
Core full suite: 199 passed. Latest native full smoke passed
(`instant-settings-native-smoke.json`, PID 5804): 1,000 lifecycle/geometry cycles,
zero region failures, source-safe removals, native compact/expanded CF_HDROP,
clipboard pause/resume/self-write handling and resource plateau. Do not repeat
this unchanged suite until subsequent runtime changes require the final gate.

## User amendment — 2026-09-12

The user's latest request supersedes the ZIP's exactly-three-page and 6 by 3
constraints. Final order is Widgets -> Music -> Files -> Clipboard, retaining
non-wrapping side rails. The grid is interpreted as four rows and eight columns
for the horizontal island. Add useful local widgets and supported size variants.
Expand lyrics validation to repeated track changes, playback transitions and
delay against the actual player. This amendment is part of the current full
release scope; release and skill synchronization remain required.

## September 13 widget editor verification

The latest amendment requires pointer dragging/repositioning and visible
selection, with no relocation of existing widgets when adding. Five targeted
WidgetCatalog tests pass, including occupied-cell placement and insufficient
contiguous room. Native mouse movement/release moved Clock from row 0 to row 1
and persisted collision resolution. Native remove/add preserved all seven
remaining placements exactly (`widget-native-add-result.json`). Selection is
exposed as UIA ItemStatus and an accent border; `widget-selection-fixed.png`
records corrected small-cell labels. Synthetic SetCursorPos alone did not
update WinUI PointerPoint reliably on this host; the successful gesture used
injected mouse movement. Final source removes temporary gesture diagnostics.
Build/publish passed; final clean-source native check remains open.

Repository maintainer Skill records the amended source contract. Personal
`dropspace-codex` destination has not been found or synchronized; Skill gate and
publication remain incomplete.

## Scope and source

User-authorized full plan: `E:/Dev/reference/Preview24-plan/DropSpace_Preview24_WindowsLocal_FullRelease_Codex_Package/DropSpace_v0.3.0-preview.24_WINDOWS_LOCAL_FULL_RELEASE_PLAN_for_Codex.md`.
All 15 target/failure images were opened individually before UI work.
Baseline: `820daa9ed02bd2943100a718b3c16acd1b93cdb8` (Preview.21).
Branch: `agent/v0.3.0-preview.24-rebuild-from-preview21`.
Do not merge Preview.22/23 or transplant their UI/state machine.
Preserve the pre-existing untracked `.codex/environments/` configuration.

## Checkpoints

- `13491f6`: SDK 10.0.401 in global.json, CI and release workflow. Solution restore passed.
- Baseline Release WinUI build passed on this Windows host: 0 warnings, 0 errors.
- `67c917a`: build 20348, settings schema 14 and migration coverage. Release/Debug WinUI builds: zero warnings/errors. Migration/concurrency: 7 passed; Core compatibility/settings: 19 passed. Compatibility and localization gates passed.
- No Preview.24 release has been prepared, pushed or published.

## Media backend checkpoint

SMTC service rebuilt with a one-slot coalescing event channel, serialized lifecycle,
cancellable five-second reads, source selection, bounded metadata/artwork and drained
shutdown. Release build passed with zero warnings/errors. Real Windows SMTC smoke
passed: enable, observed snapshot, disable/drain, re-enable, disable. This does not
yet prove playback controls or artwork against a playing app.

The App test packaging emitted PRI257/PRI263 for MSTest's `zh-hans` satellite
resource without an `en-US` satellite. These are test-host localization warnings,
not shipped App resource failures; the native test executed successfully and App
localization parity passed. Retain this distinction in final validation.

## Process loopback checkpoint

The new dedicated MTA adapter activates Windows process loopback for a resolved
SMTC process, consumes real stereo PCM and computes six Hann-windowed FFT bands.
Capture stops and drains on pause/source removal. Process identity resolution is
bounded and refuses ambiguous matches. Activation parameters survive cancellation
until the asynchronous Windows callback completes.

Real Windows native tests passed (2/2, 629 ms): SMTC enable/disable/re-enable and
WinMM-rendered quiet test tone captured through WASAPI, nonuniform FFT bands,
pause, restart and stop. Test evidence: `preview24-media-native.trx` under the App
test project's ignored TestResults directory. COM completion interfaces must be
public and COM-visible so Windows can query their agility; the original private
CCW returned E_ILLEGAL_METHOD_CALL and was fixed, not bypassed.

This checkpoint does not yet establish identity resolution or music capture for
an external player, which remains a required end-to-end gate.

## System observer and widget checkpoints

- Notification listener: Release build zero warnings/errors; native lifecycle test
  passed. Actual access in the unpackaged test host was Unavailable. This is only
  graceful-unavailability evidence; packaged permission and incoming-toast tests
  remain open. No history replay and no automatic permission request.
- Volume observer: real default render endpoint callback test passed (389 ms).
  The test changed one percentage point and restored the exact previous scalar in
  finally. Disable/re-enable passed. Actual output-device switching remains open.
- Widget data: real system time, CPU delta and memory sampling passed (1 s).
  Hiding cancels and drains sampling. This service has no island presence API.

## Material checkpoint evidence

Actual Windows 26200 screenshots inspected at 125% scale in light/dark themes,
with island keyboard focus and with focus returned to the main window. Bounded
blur remains; no host-sized backdrop, outer stroke or shadow. Screenshots retained
in ignored `artifacts/preview24/material-{light,dark}-{focused,unfocused}.png`.
An initial black rim revealed that Preview.21 passed client coordinates to the
window-relative HRGN and added an extra right/bottom pixel. Both causes were fixed.
A native regression test uses an actual window with a non-client inset.

Full existing native smoke passed: 1,000 lifecycle cycles, 1,000 geometry stress
cycles with zero region failures, compact/expanded CF_HDROP, 200 deletion cycles,
source sentinel preserved, clipboard pause/resume and loop suppression, no idle
frame loop, stable resource plateau. Evidence: `material-native-smoke.json`.

Test data override is only honored alongside `--test-mode` via
`DROPSPACE_TEST_DATA_ROOT`; normal launches retain the current-user data root.

## Build notes

## Phase 5 — Compact media

Owned SMTC/lyrics/artwork/process-audio presentation now drives a measured compact
surface. Presence policy separates media, file, manual and transient state; widgets
cannot keep the island alive. Monotonic playback interpolation handles players that
return the Windows epoch and zero timeline without jumping to the final lyric.
Core regression cases cover unavailable/native timelines, track changes, pause,
resume, grace expiry, page retention and widget-only hidden state.

Native same-track comparison with installed WinIsland 1.3.3 and NetEase passed:
both showed the same current lyric, with real process audio bars. Pause returned
both islands to hidden. Evidence: `artifacts/preview24/compact-clock-fixed.png` and
`compact-paused-hidden.png`. NetEase exposes no valid SMTC position on this host;
position is estimated from observed track start, so mid-track attachment and seeks
cannot be claimed precise. Expanded pages and remaining acceptance gates are open.

Use the checked-in CI commands. A build with an explicit global `RuntimeIdentifier=win-x64`
can regenerate Core/Infrastructure lock files with RID sections; rerun ordinary solution
restore before locked solution restore. Do not commit that incidental lockfile churn.
Git HTTPS direct transport was reset; per-command use of the existing Windows user proxy
allowed fetch/clone without changing repository or global proxy configuration.

## Phase 7 and user amendments — in progress

September 12 amendment: four pages in order Widgets, Music, Files, Clipboard;
4 rows by 8 columns, eight widget types, and per-widget supported sizes.
Native UI Automation verified both page endpoints, all four pages, real battery,
CPU/memory and uptime values, and a running stopwatch. Screenshot:
`artifacts/preview24/widgets-eight-native.png`. Existing test-root layouts were
preserved; resetting through the native editor produced all eight widgets.
The Clipboard page uses existing source-filtered records and actions; list rows
expose title and pin state. Mouse dragging and final accessibility gates remain open.

September 13 amendment: Apple Music lyric jitter and missing audio meter; NetEase
default and automatic fallback to the other four online lyrics providers.

- Twelve sequential NetEase track changes plus rapid reverse navigation exercised
  the live compact surface. Observed display changes were 231–576 ms after the
  control command (not an absolute lyric-synchronization measurement).
  Independent diagnostic lookups sometimes returned no result while the app had
  valid cached lyrics, so those probes cannot alone classify a stale lyric.
- Artwork updates no longer cancel/restart lyrics requests. Each pipeline owns its
  own cancellation generation; old generations cannot repaint a changed track.
- Multi-artist scoring previously preferred a shorter live-version credit list.
  Preserve provider artist boundaries. `Color Your Night` changed from 0 to 62
  matched lines; actual same-line parity with WinIsland was inspected in
  `artifacts/preview24/multi-artist-fixed.png`.
- Apple Music emits frequent new timestamps for unchanged whole-second positions.
  Ignore timestamp-only corrections and prevent subsecond backward correction
  when the native whole-second position advances normally. Actual backward seeks
  still reset the clock. Thirty steady native samples had zero backward steps:
  `artifacts/preview24/apple-clock-steady.jsonl`. Initial/cross-track stale timeline
  observations remain a separate concern, not a steady-playback result.
- Apple Music audio comes from the separately launched package application
  `AppleInc.AppleMusicWin_nzyj5cx40ttqa!LibraryServer`, not the UI's `!App` PID.
  Exact renderer identity resolved successfully. Four-second native capture:
  UI PID peak 0; renderer peak 0.769 with 87 real PCM-derived spectrum frames.
  `artifacts/preview24/apple-music-fixed.png` shows the live spectrum and lyrics.
- Online lookup tries the preferred provider first (fresh default NetEase), then
  up to four other online providers concurrently. Each has an eight-second budget;
  the first nonempty timed document cancels/drains the remaining jobs. Empty/error
  results are not cached. Local mode never invokes online fallback.
- Core tests: 196 passed. Infrastructure targeted migration/fallback suite:
  12 passed, including winner cancellation/draining, track cancellation without
  cache pollution, primary preference, and local-mode isolation.
- Seven native settings categories and the main Music page are implemented.
  AutoHide now affects retained media presence. Visible music surfaces own frame
  updates and process audio capture, including the main Music page when island
  media activity is disabled. Secondary lyrics now have a compact second line.

The optional compact-widget feature (plan 36.4, "if retained") is omitted; no
nonfunctional mode selector is exposed. Persisted compact slots are preserved.
Latest full Windows smoke passed (`phase7-native-smoke.json`, PID 9168): 1,000
lifecycle/geometry cycles, zero region failures, native drag/drop, 200 removals
with source preservation, clipboard pause/resume and self-write suppression,
resource plateau and no continuous frame loop. This does not close scale/UX gates.

Still open before phase/release completion: expanded scaling/host sizing,
right-button hold movement, notification/volume UI wiring,
native resize/drag/restart and accessibility checks, final full Windows smoke,
protected CI/merge/release and website/API verification, and both Skill destinations.
No Preview.24 publication or Skill synchronization is claimed by this checkpoint.

## Historical phase 6 checkpoint

## Phase 6 — Independent expanded pages

The existing Files panel is preserved inside a three-page shell with non-wrapping
side rails. Music renders real artwork, metadata, controls, spectrum and timeline
capabilities. Widgets renders the saved 6 by 3 layout with visible-only native
clock/calendar/CPU/memory sampling. Playback changes preserve the selected page;
drag temporarily exposes Files. Paused Quick Panel defaults to Files.

Native UI Automation and screenshots verified Files -> Music -> Widgets, live
resource values, and Widgets remaining selected after pause. Same-track artwork
now refreshes on subsequent metadata events instead of caching an early thumbnail.
Evidence: `expanded-music.png`, `expanded-widgets.png`, `compact-artwork-fixed.png`.
The full existing Windows smoke passed again (`expanded-native-smoke.json`):
1,000 lifecycle and geometry cycles, zero region failures, drag/drop, 200 record
removals with source preservation, clipboard pause/resume, stable resource plateau
and no idle frame loop. Five presence/page regression tests pass. Final scale,
accessibility and release gates remain open.

Lyrics backend: separate NetEase/QQ/Kugou/LRCLIB/AMLL/local adapters, bounded HTTP,
LRC/YRC/TTML parsing, matching, word timeline and memory cache are implemented.
Live adapter smoke on this host returned 50/14/14/49/54 timed lines respectively;
AMLL returned 405 word timings. This proves provider retrieval/parsing, not yet
same-player visual synchronization with WinIsland. Offline LRC/YRC/TTML/timeline,
local metadata matching and edited-file reread smoke passed. No lyrics are logged.

1. Finish platform/schema validation and checkpoint.
2. Audit pinned WinIsland and Microsoft/Files/EarTrumpet sources; record parity and license boundaries.
3. Backend checkpoints: SMTC, lyrics providers/matching, process loopback/FFT, system observers, widget data.
4. Bounded material and focused/unfocused dark/light native smoke.
5. Compact media with real lyrics, PCM spectrum and actual XAML measurement; native smoke.
6. Independent Files/Music/Widgets pages preserving Preview.21 Files UI; native smoke.
7. Music page and working widget/settings editor; restart persistence smoke.
8. Transient activities and final product hardening.
9. Full local Windows gate, required GitHub checks, merge, release, five assets/checksums, website/API verification.
10. Synchronize and verify repository and installed `dropspace-codex` maintenance skills.

Do not mark completion or publish with any plan release blocker unresolved.

## September 14 follow-up — language, widget movement, NetEase timeline

English mode now filters Chinese secondary translations without changing the
original lyric line. Widget movement preserves the grab offset and placement
order: empty moves preserve neighbors, exact equal-size drops swap two widgets,
and blocked resizing/drop attempts leave the layout intact. Grid guidance now
explains these interactions.

Core suite: 205 passed, including language policy and five movement/resize cases.
Release app build: zero warnings/errors. Localization: synchronized keys passed.
Native acceptance for the latest movement behavior remains open.

Live NetEase SMTC observations during playback and after a track change both
returned position/start/end zero and LastUpdated 1601-01-01. Evidence:
artifacts/preview24/netease-timeline-before.json and netease-timeline-after.json.
Exact native synchronization is unavailable on this installed player. The UI
explicitly warns that estimated progress can leave lyrics out of sync. No claim
of fixing external player timeline support is made. Playback was paused after
the probe. Transient activity runtime wiring is in progress, not accepted yet.

Repository Skill text updated. Personal Skill synchronization/publication and
all remaining phase 8/9 release gates are still open; no release is claimed.

## September 15 Beta 24 release validation

Current canonical version is v0.3.0-beta.24; the earlier Preview.24 sections
are execution history, not published releases. See beta-migration.md for the
version/parser/channel contract and approved Preview.23 manual upgrade boundary.
Core 212, Infrastructure 151, App 40 local tests pass; real PCM and volume
were retried with 2 passed and zero skipped. Website 27 unit and 6 Chromium
browser tests pass. Latest native widget drag swaps Clock and ResourceUsage,
preserves the other six placements, and retains scroll/focus after saving.
Placement editing shows Confirm/Cancel after release and does not persist the
preview; Cancel restores the original. Right-hold input is implemented but its
actual hold gesture has not been independently exercised by the current tool.
Full native smoke beta24-final-native-smoke.json passed lifecycle/geometry,
source preservation, clipboard and resource plateau checks. Repository/personal
Skill copies are synchronized and both validate.

The hosted installer caught premature final-HWND closure during maintenance.
Shutdown now drains native/DI services before closing that HWND. Two local
startup/maintenance cycles exited successfully; the measured second request
returned 0 in 406 ms. The existing installer lifecycle regression is rerunning
in hosted CI, followed by publication and live website/API verification.
No public Beta release is claimed until those gates complete.

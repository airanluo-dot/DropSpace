# Native lyric timing and scrolling

Fixed reference: Windows `v0.3.2-beta.7`, commit `1cd4620dd58a67b0963b73487743137813b8864a`.
This is scoped implementation and acceptance evidence, not completion of MEDIA-02 or the port.

## User-approved default difference

On 2026-10-20 the user explicitly chose: “Mac 默认使用按句时长自适应滚动，记录为明确的行为差异”.
Mac `lyrics.translationScrollMode` therefore defaults to `adaptive`. Music Settings also offers
`baseline`, retaining Windows Beta 7's repeating 24-point/second translation marquee and
1.2-second end holds. This is a deliberate product difference, not a platform limitation.
Existing Mac settings without the field merge in the adaptive default; an explicit choice
persists. It does not change provider order, lyrics, translation targets, prompts, cache limits,
or require another source lookup.

Adaptive translation scrolling uses the displayed translation width minus its viewport width,
and the remaining interval of the current original lyric line. At each end it holds for
`min(1.2 seconds, 15% of the remaining interval)`. The remaining movement time determines the
speed. It reveals the translation once and holds the ending; it does not loop mid-sentence.
New text, layout changes or actual seeks replan from the current native time. Pause retains the
current offset. Scrolling disabled or reduced motion produces zero offset.

The native readability ceiling is 96 points/second (four times the baseline speed). Late or
very long translations may not finish before the line ends; the settings explanation and
compact help disclose this and direct the user to expand the Island for the full text.
`LyricsAdaptiveScrollSession.Frame.canFinish` records this condition. This ceiling is a
native presentation policy, not a Windows threshold or a claim of validated reading speed.
Translation receives no invented per-word timestamps.

`LyricLine.timeBasis` distinguishes reliable line intervals, synthetic end estimates, untimed
plain text and unknown older cache provenance. LRC uses the next real cue/boundary; a terminal
five-second fallback remains `estimatedEnd`. YRC and explicit TTML ends retain their real
intervals. Whole-track duration binding does not turn plain text into timed sentences.
Without a reliable interval, the translation uses the baseline marquee; the main Music view
discloses that for the currently displayed line. Refreshing a source re-establishes provenance.

## Preserved rules and native implementation

| Contract | Fixed source | Native owner / behavior |
|---|---|---|
| Current/retained lyric, first-line preview, interlude after 3 seconds, next distinct nonempty timestamp group | `Core/Lyrics/LyricsDisplayPolicy.cs`, `LyricsPreviewPolicy.cs`, `LyricsModels.cs` | `LyricsDisplay` / `LyricsTimeline`; compact three-dot interlude, expanded current and next rows; empty document falls back to the track title |
| Word timing and compact original overflow | `LyricsDisplayPolicy.CompactScrollOffset` and actual Island rendering | `CompactLyricsView` measures native glyph prefixes and clips a continuous highlight mask. Real word progress follows source timestamps. Without word timing, original overflow retains the baseline 24-point/second rule; the approved adaptive option applies to translation overflow |
| Secondary formatting | `LyricsDisplayPolicy.SecondaryPresentation` | Inline `AI · ` only for local-AI origin, current target filter, compact newline normalization, secondary font = original font × 0.875, full accessibility value |
| Fixed translation marquee | `LyricsMarqueePolicy.cs` | `LyricsMarqueeSession`: repeating 24 points/second, 1.2-second leading/trailing holds; layout/session/actual-seek retirement |
| Playback observation and motion lifetime | Windows media clock anti-rewind behavior; public macOS player sampling differs | `NativePlaybackClock` / `MediaSnapshot`: monotonic uptime, bounded two-second interpolation, stale-command poll retirement, explicit seek revision; views do not run an independent song timer |
| Panel geometry and decoration | `OverlayWindow` and Island display policy | Native glyph sizes, minimum 28-point first row, 40-point interlude, available-display width clamp, settled-geometry gate and physical-pixel viewport rounding; translation presence stays connected to the exterior glow owner |

Source correction after reviewing `Views/Music/MusicPage.cs:355–448` and
`Views/Island/ExpandedIslandMusicView.xaml:30–36`: neither surface implements a continuous
per-word effect in Beta 7. Main Music emphasizes the active original line with semibold weight
and opacity1 (inactive0.58), secondary opacity0.72, up to2,000 visible rows and a360-point
viewport. It centers the current line independently of the compact Scrolling setting.
Expanded Island displays complete wrapped current/secondary text and a next-line preview;
its preview font is13/16 of the original size. The earlier native attributed per-word opacity
was not this baseline behavior. The native code now follows these actual rules; main centering,
named native seek/centering and accessibility scrolling have actual evidence below.
Wheel containment and full layout/accessibility acceptance remain independently tracked.
No claim that every lyric surface or player has completed parity is made.

## Actual verification

Host: macOS 27.0.1 arm64, Xcode 27.1 / Swift 6.4, Apple Music 1.7, isolated
`build/CompactLyricsNativeSmoke`. Only task-authored silent audio and lyric files were used.
Evidence: `build/CompactLyricsNativeEvidence`, named build/test logs in `build/`.

- Frozen C# execution: 105 display/preview/timeline vectors, 144 original offsets,
  33 marquee-session updates and three newline examples. The first selected Swift test exposed
  CRLF normalization and floating cycle-boundary differences; both were corrected, retry passed.
- Four selected adaptive/settings/clock tests passed. They cover different line durations,
  late text, speed cap, no repeat, seek revisions, reduced motion, missing timing, existing
  settings merge, explicit baseline persistence and invalid-value preservation.
- Actual compact screenshots show first-line preview, moving word mask, independent translation
  overflow, full accessibility text, expanded current/next and three-dot interlude. Native
  seek values were read back in Apple Music; physical mouse dragging remains unverified.
- Actual adaptive entry at 20 seconds in the 10–50 second line produced about 31.4 points/second;
  entry at 15 seconds produced about 26.6; entry at 12 seconds produced about 24.3. The same
  translation and 259-point viewport therefore use different speeds for different remaining time.
- Actual seek to 40 seconds retired the old phase. The oversized translation moved at the
  96-point/second cap and stopped at 729.6 points, below the 865.95-point overflow; the subsequent
  gap retained the line briefly and then showed the interlude. This is an observed incomplete
  reveal under the documented readability cap, not a complete reveal claim.
- External Music pause originally reset the secondary row. Stable render ownership and pixel
  rounding alone did not fix it. Numeric traces then identified a delayed pause observation
  more than one second behind the interpolated display. The final clock recognizes a plausible
  pause interval from the previous raw source sample while preserving the actual lyric position.
  App43870 retained adaptive offset387.77 and fixed-mode offset415.06 rather than resetting.
  Explicit seeks and distant external jumps still retire the phase.
- Native Music Settings selected and persisted baseline, then restored adaptive. The fixed
  mode measured about 24 points/second. Replacing only the authored fixture with untimed text
  and selecting Refresh displayed the missing-timing explanation and real `METAL` translation;
  its scrolling used fixed speed. The timed fixture was restored byte-for-byte.
- A separate MainLyricsNativeSmoke app45266 used60 authored lines with compact scrolling off
  and AI off. Missing artist metadata was first rejected by the unchanged matcher; adding the
  fixture author's metadata and native Refresh succeeded. Actual paused10s→20s showed line11→21
  semibold and centered. The first CUA scroll did not move; a fresh native `Scroll Down`
  accessibility action moved the viewport0.3193→0.4407 without moving playback. Seek10s
  centered line11 again. These are actual AX/native screenshots, not physical-wheel acceptance.
  `lyrics-main-line-build.log` passed; original task song remained paused10s on normal Quit.

Polling cannot distinguish every simultaneous subsecond external seek/pause. A bounded pause
correction can briefly hold the adaptive offset until native time catches up after resume;
original highlighting always follows the corrected lyric clock. Seek gesture/cancellation,
playback-rate controls, multiple players, late live provider changes, all display/font/scale/
language/RTL combinations, VoiceOver, reduced-motion/contrast transitions, main viewport wheel
containment, sleep/Spaces and
macOS 26 runtime acceptance remain open. Unit fixtures and local code signing do not close them.

The final validation instance was quit normally. Music was restored to the original task
`DropSpace Validation`, paused at 10 seconds, with its title filter and favorites-only view.
Ad-hoc signature checks concern local integrity only; no Apple notarization or identity is implied.


## Native lyric setting controls — 2026-10-20

MusicPreferences now uses native sliders with the frozen MusicPage ranges:
−30000…30000ms/100ms offset,80…600pt/10pt width with610 as the unlimited
sentinel preserving the previous finite width, and0…10 integerGB cache quota.
The existing coalesced settings editor now accepts computed read/write mappings
without moving persistence ownership into the view. Playback/lyrics field order
and the empty-player100pt versus populated380pt card height follow the actual
frozen MusicPage. The user-approved adaptive translation setting remains.

`music-lyrics-settings-sliders-build.log` succeeded. Actual isolated
LyricsSettingsNativeSmoke59183 via native AX controls: paused10s local fixture
changed from line11 to line16 at+5000, line06 at−5000, then line11 at0. Player
position remained10s. Unlimited saved true while retaining260;600 saved finite,
then260 restored. Quota0 removed five independent cache copies (3440 bytes),
showed0 usage and left all five source SHA256 values unchanged; restored1GB.
The shared scalar slider still saved16.5 and restored16. All settings compared
equal to the initial object. Normal Quit exited. Evidence, screenshot, saved
values and receipts: `build/LyricsSettingsNativeEvidence`.

This proves these actual setting/service paths on one Chinese macOS27.0.1 UI.
It does not prove physical dragging, keyboard/navigation flush timing, every
width/display scale, cache IO failure/rollback or macOS26 acceptance. No broad
suite or new implementation-mirroring unit tests were run for these view edits.

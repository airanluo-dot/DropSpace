# Native Island contracts

## NetEase enhancement amendment

- Enhancement is explicit third-party component management, separate from general media.
  Runtime chain remains NetEase → BetterNCM → unchanged official InfLink-rs → Windows SMTC
  → DropSpace. Never call the plugin JavaScript API or vendor/fork its implementation.
- One app confirmation covers download, managed deployment, player restart and bounded
  playback verification. Windows UAC remains OS-owned. Never bypass elevation/security.
- Use official upstream artifacts only, bounded downloads, SHA-256 verification, architecture
  checks, atomic writes and persistent ownership/rollback receipts. Preserve unrelated
  components and settings. Missing VC runtime requires Microsoft's signed official installer.
- Presence of DLL/plugin/receipt does not mean Enhanced. Require real Windows metadata,
  artwork, playback state, actual changing timeline and enabled controls plus command effects.
  A complete native player skips plugin installation. Failed verification rolls back.
- Upstream tested versions do not establish support for newer NetEase versions. Revalidate
  current target-machine installation and rollback before publishing claims.
- D-064 removes shuffle/repeat expansion, UI controls and acceptance requirements. The exact
  12 core capabilities are Title, Artist, Album, Artwork, PlaybackState, Play, Pause,
  Previous, Next, Timeline, LiveProgress and Seek. Require actual command effects and
  advancing playback; state-specific Play/Pause flags need not be enabled simultaneously.
- InfLink 3.2.11's null initial modes and toggle-only handlers remain historical upstream
  limitations, not blockers for revised acceptance. Never use a fork, private JavaScript
  API or guessed states to bypass Windows SMTC. Preserve earlier failure evidence.
- Same-source/same-track duplicate sessions prefer richer standard timeline/seek information,
  within a two-second comparison budget and preserving cross-player priority. Different
  tracks remain distinct; dispatch commands to the exact selected session object.
- Close/re-enumerate exact-path player processes under a total shutdown budget. Retry only
  actual owned-file access/sharing failures (seven attempts, 3.15s backoff); persistent
  failures retain receipts and remain visible. Attempt player restart even if rollback fails.
- A healthy manager may remain connected, but retire concrete sessions before restart.
  Discard invalid/stale COM candidates immediately and rediscover live sessions. Connection,
  discovery and capability failures have separate diagnostic stages; connection failures
  do not establish that a plugin is missing. Prove live progress through session events
  without polling or carrying pre-restart session evidence into a new verification.
- Publication is reauthorized after real one-click acceptance and release gates, followed
  by protected merge, Beta26 publication and live website/API verification. D-064 supersedes
  the earlier pause; never treat authorization as an unobserved success claim.


The current Beta 26 audit preserves the Beta 24/25 four-page implementation,
which originated from Preview.21. Do not transplant Preview.22/23 UI or state
machines. SDK 10.0.401, x64 Windows build 20348 minimum and settings schema 14
with migration coverage from 11/12/13 remain unchanged. Read the Beta 26
lyrics/media/native-data audit reports under `docs/dev` for executed evidence;
`beta26-rc-audit.md` owns consolidated release status. Preview/Beta 24 reports
remain historical evidence, not the current completion checklist.

- User amendments set the non-wrapping page order to Widgets, Music, Files,
  Clipboard. Widget settings use four rows and eight columns, eight local widget
  types, and catalogued size choices. Adding a widget must find vacant contiguous
  space and preserve every existing placement; reject insufficient room.
  Selected widgets need visible emphasis and accessible selection state.
  Editing preserves the grabbed pointer offset and existing list order. Moves
  into vacant space preserve all other positions; exact same-size drops swap
  only the two widgets. Reject obstructed resizing or incompatible drops without
  cascading a normalization/reflow through the grid.
- September 14: all settings apply on change without save-success notices or
  Save/Apply buttons; only island placement retains explicit confirmation.
  Widget position and size controls commit directly. Pointer dragging must show
  movement, preserve capture, and not be consumed by scrolling. Language changes
  refresh existing surfaces; do not destroy native island HWNDs just to relabel
  them. Resource contexts for the two supported languages are initialized once
  and remain immutable during concurrent reads.
- Main Music and seven native settings sections share serialized settings
  transactions. Explicit media allow-list enabled with no entries means none;
  disabled means all. Preserve runtime-owned pause and update fields.
- NetEase is the default preferred lyric provider. An empty/failed preferred
  online result tries the other four providers with bounded concurrency and
  cancellation; local mode remains offline. Never cache empty results or allow
  artwork updates to cancel the current track's lyric lookup.
  English display suppresses Chinese secondary translations while preserving
  original lyrics. Previous NetEase native probes reported zero position/duration
  and an invalid 1601 timestamp while playing and after switching tracks; show the
  estimated-timeline/possible lyric desynchronization limitation, not a claim of
  exact synchronization. Recheck native capabilities before changing this limit.
- Provider responses supply their own identity; never fill missing candidate
  metadata from the requested track. NetEase searches title plus artist. LRCLIB
  uses search for incomplete exact metadata and after 400/404 or rejected exact
  matches. Kugou song-search duration is seconds, lyric duration milliseconds;
  only a validated song's hash lookup can inherit that song's album. HTTP uses
  bounded same-origin HTTPS redirects while automatic redirects remain disabled.
  Keep explicit YRC/TTML line ends across instrumental gaps; infer LRC ends only
  when no end was supplied. Bind each external translation to its closest
  original once; zero-time/nearby credits must not reuse a sung translation.
  Online smoke must exercise the production transport,
  registry and service fallback; Local LRC must demonstrate zero network calls.
- Artwork is optional and has its own bounded read. Artwork failure cannot retain
  previous-track metadata; exhausted/detached media candidates publish Empty.
  Keep the preferred player's recovery events subscribed independently of a
  fallback control target. Ordinary timeline events during artwork I/O must not
  invalidate unchanged metadata; real metadata revisions still reject stale work.
  The playback clock observes visible frames as well as metadata callbacks so
  sparse callbacks cannot rewind an actively observed estimated timeline.
  On pause-to-play transitions an old native timestamp must not add paused wall
  time to the resumed position. Exercise both contracts with actual player changes.
  Play/Pause uses the capability for the current playback state, and every Music
  surface honors ShowArtwork and clears old artwork before track notifications.
- Apple Music's repeated timestamps for unchanged whole-second positions must
  not rewind the interpolated playback clock. Capture its exact package
  LibraryServer audio identity, with UI identity fallback, rather than assuming
  that its renderer is a child process. Spectrum bars require actual PCM/FFT.
- Media display owners control frame/capture visibility. Clipboard remains an
  event-driven bounded canonical projection, not duplicated widget storage.
- Required validation is proportional: targeted regression tests and native
  acceptance for changes, documented build, then final Windows/CI/release gates.
  Do not repeat unchanged passing full smoke suites without a new concern.
  Successful probes do not prove untested DPI, accessibility or release rows.

Startup window invariant: `--startup` must create the main HWND for backend services without an initial `Show`/`Activate`; only normal and redirected activation may show it. Keep this covered by the portable smoke test.

Placement editing supports right-button hold and explicit settings entry. Releasing a drag keeps a preview; only Confirm persists it. Cancel/Escape restores the original placement. Multiple preview drags must accumulate without persisting early.

Audio hardware smoke tests require an actual render endpoint. A host without one reports Inconclusive, never a successful audio validation. Before release, run the same PCM/volume tests on an equipped Windows desktop and record zero skips.

Widget settings retain existing button instances when placements change. Recreating the focused grid on every save transfers focus to the numeric editor and scrolls away from the drop target; verify focus and scroll position in native drag acceptance.

Maintenance shutdown must drain window-owned work and DI/native services before closing the final main HWND. Detach the window callbacks first, keep its dispatcher alive during async cleanup, then close after cleanup. The installer lifecycle gate must verify graceful /UPDATE shutdown and restart.

Virtual-file COM marshaling must preserve the real HRESULT/out-pointer ABI and
release unused marshal packets as well as consumed interfaces. Reserve output
names across the entire batch before writing. Payload reads/writes/deletes and
deferred-delete recovery all enforce the reparse-safe owned-path policy; this
does not claim atomic resistance to concurrent junction replacement. Cleanup
outbox retries must not starve newer obligations. Once a clipboard resume is
durable, complete its runtime duplicate-state reset despite caller cancellation;
shutdown drains initialization before disposing its synchronization resources.
ZIP exports preserve directory hierarchy and empty folders and charge directory
entries against the traversal budget. None of these operations modifies source
files.

The NetEase verifier's Windows calls use the existing dispatcher and preserve its
context across awaits; installation/downloads remain background operations. This
avoids the target-machine RPC_E_WRONG_THREAD observed in metadata and commands.
Distinguish ERROR_NOT_READY during initialization from disconnected session objects.
Seek must cause an observed position change toward its target; the tolerance is one
second, matching the product's position display and asynchronous native updates.
No-op or out-of-tolerance seek responses must fail regression tests. Keep diagnostic
stage/HRESULT and command observations, without metadata payloads or filesystem paths.

After Previous, metadata may precede track-load completion. Restoration retries once after
an unobserved bounded operation and reasserts saved playback state after the seek. Passive
inspection can retain a previously committed verification only when current core signals
remain healthy; a receipt alone never proves enhancement. Generic same-source/same-track
selection supports ordered primary/full artist credits without substring matching.

D-064 local acceptance completed on NetEase 3.1.40 with official BetterNCM 1.3.4 / InfLink
3.2.11: one-confirmation install/reinstall, fresh-session 12-capability verification,
actual page slider/play/pause, and Apple Music selection/progress/pause. The generic GSMTC
service uses the same dispatcher-context discipline as the verifier; retain that ownership
for discovery, subscriptions, commands and disposal. Local suites total 531 passing tests.
Publication completion still requires protected merge and live release/website checks.

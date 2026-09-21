# Native Island contracts


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

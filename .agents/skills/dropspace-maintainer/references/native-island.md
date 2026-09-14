# Native Island contracts


The active Beta 24 rebuild starts from Preview.21; do not transplant the
Preview.22/23 UI or state machine. The authorized task includes the supplied full
plan through release. Read `docs/dev/preview24-progress.md` for checkpoints and
open acceptance gates; this section describes source behavior, not a published
release. The current rebuild uses SDK 10.0.401, x64 Windows build 20348 minimum,
and settings schema 14 with migration coverage from 11/12/13. These active facts
supersede older historical build-17763 entries below for this rebuild.

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
  original lyrics. NetEase on this host reports zero position/duration and an
  invalid 1601 timestamp even while playing and after switching tracks; show the
  estimated-timeline/possible lyric desynchronization limitation, not a claim of
  exact synchronization. Recheck native capabilities before changing this limit.
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


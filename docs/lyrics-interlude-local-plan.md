# Local candidate: lyric waiting and adaptive top placement

Status: local only, on top of the beta30 source tree. Do not push, merge, tag or
publish until the user asks. The user is collecting more issues and requests.

## Behavior
- A matched lyric document owns the presentation throughout the song. A missing
  active line is a waiting state, not evidence that the song has no lyrics.
- Brief gaps retain the previous lyric and translation dimensions. Before the
  first timestamp, preview the first lyric without switching to the title.
- After three seconds of a timestamped gap, cross-fade to three gentle breathing
  points without changing the retained lyric layout. Resume at the next line.
- Pause freezes the phase; reduced motion uses stationary points. The animation
  reuses existing visible-media frame notifications, adding no background timer.
- Plain LRC with no true end timestamps cannot reliably identify vocal silence;
  do not infer an instrumental break from audio volume or invent lyric end times.
- Default anchoring keeps 8 DIPs below the monitor work area's top. The former
  76-physical-pixel Smart/Drop Tray reservation is removed for every state.
- Preserve user custom placement. Fit expanded/music content to the work area;
  constrain rendered geometry to the host and current monitor. A top taskbar is
  respected through the monitor work area rather than a guessed fixed offset.

## Verification
- All 289 Core tests passed locally, including lyric gaps, intro/outro, delay,
  seeking, and an aspect-ratio/DPI/origin matrix (1080p, 1440p, 4K, portrait,
  ultrawide, 800x480; 100–300% scale; negative-origin/mixed-monitor examples).
- Compact XAML is well-formed XML; git diff whitespace check passed.
- Added App view-model regression for matched lyrics staying out of title mode.
- Actual WinUI compilation, animation appearance, and physical multi-monitor
  behavior have not been run on this Linux workspace. They remain required before
  a future published version. No remote CI was triggered for this local-only work.

## Additional local work — October 1, 2026 (Asia/Shanghai)

- Main window now requests its own focus-independent Acrylic instance, matching the island's activation policy. Windows may still enforce opaque fallback for accessibility, disabled transparency, remote sessions or unsupported composition. This does not change Windows settings or other applications.
- Expanded page changes crossfade with a continuous, retargetable frame-driven state. Only the new target accepts input; previous pages remain drawn until faded. Expanded/compact/drag content uses independent translation and subtle scale driven by existing spring progress, so interruption does not restart the pose. Reduced motion removes spatial effects.
- Widget inventory grows from 8 to 16, appending enum values without renumbering saved layouts: UTC clock, 25-minute focus timer, 5-minute countdown, system-disk free space, network-adapter connectivity, calculator shortcut, pinned-items shortcut, ISO week/progress. These are local widgets; connectivity is not an Internet reachability test. Timers are session-only, with start/pause and context-menu reset, and do not promise background notifications or persistence across restart.
- Widget editor adds valid drag landing preview, directional nudge buttons, two-column inventory, explicit position/size application and one-step undo. Existing supported sizes and collision-safe swaps remain. Errors appear next to the preview.
- New widgets are available in the library, not forcibly inserted into existing layouts. A finite 8×4 dashboard need not show every library widget at once.
- Validation: 297 Core tests passed; changed/new C# parses successfully; localized resources have unique keys. Linux checks do not establish Windows XAML compilation, native launch, Acrylic behavior or animation visual quality. Windows verification remains required before any future release.
- Still local-only: no push, CI dispatch, version bump or release authorization.

## Release scope update

The user subsequently authorized v0.3.0-beta.31 publication and installer delivery. The earlier local-only hold is superseded, subject to Windows validation and preserved upgrade compatibility.

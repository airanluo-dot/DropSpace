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

## Release scope update

The user subsequently authorized publication as v0.3.0-beta.31 and requested the installer through WeChat. The earlier local-only hold is superseded for this release, subject to successful Windows build and necessary release validation. Preserve normal upgrade identity and ordering.

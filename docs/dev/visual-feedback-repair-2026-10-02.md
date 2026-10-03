# Follow-up to the Windows visual feedback

Base candidate: `631a58bc387273902b128146cd8a4e891c1fa7e0`.

The user rejected the installed candidate's straight-looking glow, center-cone simplified effect, clipped marquee tail and dark island edge. Its successful Windows CI does not accept these defects or validate this follow-up.

## Changes

- Parameterize the light by projected rounded-contour arc length. The simplified mask follows the bottom quarter of that contour with feathered endpoints, constant across the outward normal, rather than a cone from the island center.
- Drive traveling crest displacement with six measured bands, perceptual amplitude gain and faster attack. Use RMS plus peak for overall energy instead of averaging away a localized vocal peak. Silence has no animated shape displacement. Keep the bounded halo and fixed physical island contour.
- Arrange full-width original/translation text in unconstrained Canvas hosts behind the existing fixed viewport clips. Extend native diagnostics to check the translation layout slot, stationary viewport and tail position.
- Paint material one physical pixel beyond the unchanged native region to address exposed host-edge pixels. This is a candidate edge fix requiring Windows visual verification, not a confirmed closure.

## Evidence and remaining gates

- Parent managed harness: 115 passed, including 24 actual rasterizer tests and 91 service regressions. Both moderate (0.3) and stronger (0.8) audio amplitudes must produce traveling nonuniform crests; silence remains geometrically stationary.
- Core: 522 passed after envelope changes.
- Static checks: 361 passed. Native WinUI diagnostics cannot be executed in the Linux parent environment; exact-commit Windows validation is pending.
- Linux production-rasterizer benchmark (180 frames, not Windows compositor timing): median 3.69 ms compact, 3.84 ms expanded, 4.53 ms expanded at 200% scale; p95 at most 5.25 ms.
- Inspected synthetic-band frames from the production rasterizer. These establish geometry and response changes only, not Windows desktop appearance or real process-audio capture.
- Still required: Windows build/native diagnostics, actual playback responsiveness, visible complete lyric tails at adjustable font sizes, and visual black-edge inspection. Translation semantic quality and prior review gates remain open.

No version change, model download, publication, merge, or quota-card use is part of this repair.

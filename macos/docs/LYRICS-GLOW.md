# Native Island exterior glow

Baseline: Windows `v0.3.2-beta.7`, commit `1cd4620dd58a67b0963b73487743137813b8864a`.
Implementation and focused acceptance are in progress; this does not close MEDIA-02.

| Contract | Native implementation | Evidence / remaining acceptance |
|---|---|---|
| Off / AI Lyrics / Music | Frozen `LyricsGlowPolicy`; playing, visible eligible Island; AI requires the current translated text to intersect its viewport, localAI origin and current target identity |216 frozen eligibility vectors passed. Actual native AI enable selects AI Lyrics and enables lyrics/secondary. Real1.8B Metal translations triggered the glow; hiding secondary stopped capture/glow |
| Continuous brightness, phase, contour and simplification | `LyricsGlowEnvelope` and `LyricsGlowAudioResponse`; quiet0.08 baseline, cap0.46, frozen time constants and six-band local contrast |240 consecutive fixed-C# frames passed within1e-10. Pause/off freezes phase during fade. Static/reduced-motion and high-contrast physical acceptance remains open |
| Audio demand independent of visible meter | `NativeSpectrumService.setGlow` shares the same confirmed player tap; ShowSpectrum=false does not disable eligible glow capture | Actual Apple Music silence capture and halo while meter stayed off; pause and secondary-off confirmed zero native tap handles. No global audio fallback |
| Exterior light / simplified lower light | SwiftUI color-effect Metal shader on a nonactivating, click-through NSPanel child below the unchanged Island;18pt finite halo,3pt inner overlap, premultiplied color, projected lower tangent/corners | Actual full and simplified halo screenshots. SwiftUI GPU evaluation uses analytic Gaussian floats instead of Windows' CPU lookup interpolation; same field equations/geometry. Pixel equivalence, scale/display matrix and GPU energy acceptance remain open |
| Geometry and lifetime | Owner bounds/opacity/Spaces; panel follows native movement; excludes decoration from window menu/accessibility; immediate hide for placement editing, idle, increased contrast or reduced transparency; normal fade otherwise | One actual run exposed transient nil `NSWindow.screen` resetting phase; now only a confirmed nonnil monitor change resets it. Subsequent actual trace retained phase across main-window hide/return and AI mode change. Physical click-through remains unverified: CUA targeted the decorative panel despite its accessibility-hidden flag |
| Translation rendering | Inline `AI · ` prefix; secondary target filter; compact line-break normalization without modifying source; glyph measurement includes prefix | First AI screenshot exposed an extra third-line badge clipping compact layout. Corrected; actual compact/expanded screenshots show both lines within bounds. Compact overflow now has independent native rows and user-approved adaptive/default or fixed translation modes, with named live evidence in LYRICS-DISPLAY.md. Combined glow/scroll visibility and all font/scale/language layouts remain open |

The original text-shadow approximation was removed. Music glow is an Island effect, not a main
lyrics-row shadow. Native reduced-transparency/high-contrast preferences suppress this decoration;
reduced motion keeps static low light with state fades. The public Metal compiler component is a
build dependency (`xcodebuild -downloadComponent MetalToolchain`), not a paid developer credential.

Actual evidence resides in `build/GlowNativeEvidence`: own300s silent WAV, actual native Apple Music,
native settings persistence, full/simplified/paused/AI screenshots, numeric-only debug traces, failures
and retries. The first metadata-free own LRC was rejected by the existing recording matcher; adding
its authored title/artist metadata enabled lookup. Production matching thresholds were unchanged.
No microphone, audio file recording, global security preference or unrelated library item was changed.

Final app35707 actually exercised compact AI→pause/fade→expanded AI/play→pause→Files→resume:
Files stayed visible with no halo while Music's Pause button confirmed continuing playback.
Returning to Music restored the halo. Three capture starts had three confirmed stops, zero logged
drops and no backward phase changes. Normal Quit exited, with0 file-import leases. The prior task
`DropSpace Validation` song was restored paused10s and its title/favorites-only filter restored.
The final ad-hoc `codesign --verify --deep --strict` check passed; this is integrity only.

Remaining: nonzero audio-driven halo, clipped/scrolled AI visibility,
provider translation no-glow, timeline boundaries, reduced-motion/contrast/transparency, physical
click-through, multiple displays/Spaces/fullscreen/sleep, native GPU error handling, macOS26 and
Release performance. Compact marquee, waiting/interlude and continuous word mask now have scoped
evidence in LYRICS-DISPLAY.md; main/expanded viewport behavior and broader acceptance remain open.
No public package/release qualification is implied.

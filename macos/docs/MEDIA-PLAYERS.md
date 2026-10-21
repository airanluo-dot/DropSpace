# Native player capabilities

Fixed feature baseline: Windows v0.3.2 Beta7,
`1cd4620dd58a67b0963b73487743137813b8864a`.
Capabilities below distinguish working native operations, observed exposure and
unimplemented behavior. A player being installed or a button appearing is not a
successful control/metadata test.

| Player | Current native path | Evidence and remaining work |
| --- | --- | --- |
| Apple Music1.7 | Public scripting/Automation, playback clock, artwork, confirmed-source process tap | Actual own recording metadata/paused and playing state, seek commands, lyrics and scoped capture evidence exist in MUSIC-RUNTIME, MEDIA-TAPS and LYRICS-DISPLAY. Physical seek gestures, full permissions/player transitions/minimum OS acceptance remain open |
| Spotify | Public scripting adapter and artwork service | Implemented; this current machine audit did not find `/Applications/Spotify.app`. Native end-to-end verification remains open |
| NetEase Music3.1.12/build3444 | New public Accessibility menu adapter | App bundle declares no scripting definition/AppleScript enablement. CUA observed a populated window but only window/menu AX nodes; Control menu exposes Play/Next/Previous. Actual current-build permission recovery, Play/Pause commands and external playback-state observation passed. Next/Previous changed tracks, but complete queue semantics remain unverified. Ad-hoc rebuild identity mismatch and recovery are recorded below |
| QQ Music and other Windows sources | Further native adapters required | No verified metadata/transport adapter yet. QQ lyric-session storage/authenticated requests have scoped evidence in QQ-SESSION.md; user explicitly deferred login/handoff on2026-10-20, separately from the still-missing player adapter |

## NetEase implementation and scope

`NativeNeteaseControls` owns native inspection/control tasks; the main Music card
owns presentation only. `NeteaseMenuAdapter` reads the target application's menu
bar and the observed Chinese Control menu. It accepts only uniquely identified,
enabled transport items that advertise a public action. Unsupported/ambiguous
controls remain unavailable, with a clear capability result. It never substitutes
coordinates, keyboard shortcuts, window-title guesses or private media APIs.

Commands are bound to the detected process and launch identity, inspect current
menu semantics before acting, and are never automatically retried after a
messaging error. Native calls run off MainActor, with a two-second operation
budget and0.15-second timeout on each specific AX object, bounded menu traversal
and cancellation. While the card is visible, a one-second observer uses the same
operation slot. After Play/Pause delivery, bounded readback waits for the opposite
menu state without repeating the action. Hiding stops observation; shutdown cancels
and joins both owners. Permission requests remain explicit.

The public API timeout applies to each supplied object, not automatically to
equal/child objects; each accessed element is configured separately.
[Apple AX timeout documentation](https://developer.apple.com/documentation/applicationservices/1459345-axuielementsetmessagingtimeout),
[public action enumeration](https://developer.apple.com/documentation/applicationservices/1462053-axuielementcopyactionnames),
[Accessibility API declarations](https://developer.apple.com/documentation/applicationservices/axuielement_h).

The card offers actual capability inspection and available transport actions.
No Windows DLL is deployed or installation status invented. **This is an
intermediate functional adapter, not completed NetEase enhancement parity.**
It currently provides no track metadata, lyrics, artwork, seek or spectrum source.
The observed AX limitation in this player version does not establish that all
other public native routes are impossible. Further investigation and, for any
irreducible difference, user confirmation remain required before release.

## Actual native validation and permission recovery

On2026-10-20 the user confirmed the existing permission switch was enabled and
explicitly authorized refreshing DropSpace's system permission. The user personally
unlocked System Settings. The system log identified a stored ad-hoc code requirement
that did not match the current executable; this explains why the switch alone did
not establish trust. Off/on did not fix it. Removing that stale entry and adding the
exact current Debug application through System Settings restored public AX trust.
The updated build required the same scoped refresh. No TCC database, entitlement,
Gatekeeper, SIP or global protection was changed.

Actual NetEase3.1.12/build3444 and isolated DropSpace66505 established native
Play→Pause and Pause→Play transitions and observation of playback started from
NetEase itself. Initial code published the old menu state after an asynchronous
command; the bounded readback/visible observer correction passed the repeated
native paths. Commands are acknowledged by menu state, not invented metadata.
Previous/Next changed tracks, but stale background screenshots and an accidental
CUA menu selection invalidate a precise queue-order claim. They are not full
transport-matrix acceptance. The original user track was restored and left paused
near its prior position (roughly three minutes; exact original timestamp was not
recorded). Existing queue count remained35; no library or queue entry was removed.

Evidence is local under `build/NetEaseCapabilityEvidence/`: permission-code-requirement,
permission-current-build-available, before-refresh-fix, fixed-playing, fixed-paused,
external-play-observed, final-native-paused and final-restored-paused. Intermediate
background screenshots alone are not reliable playback-state evidence. The exact
build passed Xcode arm64 compilation and ad-hoc deep/strict integrity verification;
normal Quit exited, and isolated settings were unchanged.

These are same-machine development checks, not first-install/macOS26 acceptance.
App quit/relaunch recovery, in-flight permission revocation, command timeout,
alternate player language and complete queue behavior remain unverified. Metadata,
artwork, timeline, lyrics, seek and spectrum source integration remain missing.

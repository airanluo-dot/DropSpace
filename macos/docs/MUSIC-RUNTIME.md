# Music runtime recovery

Fixed source: Windows `v0.3.2-beta.7`, commit
`1cd4620dd58a67b0963b73487743137813b8864a`.

`MusicPage.cs:195–269`, `MediaExperienceService.cs:172–226` and
`MediaSoftRestartOperation.cs` define an app-owned, single music restart.
The 20-second deadline is independent of native retirement. Settings, model
installation and persistent lyric caches remain intact. A second restart cannot
overlap an operation which timed out but has not exited. Leaving Music retires
only the page's waiter on Windows; the recovery continues in the app.

Mac implementation:

- `NativeMusicRestart` owns the operation/deadline and publishes stable state,
  rather than a translated string. The view uses the five existing ten-language
  Music refresh/status/help identities. Page navigation does not own or cancel
  native work. Timeout reports failure, requests cancellation and keeps the slot
  until actual retirement; late completion cannot replace failure with success.
- `WorkspaceModel.reconnectMusic` suspends new media/lyric work, cancels and drains
  lyric lookup/translation, retires the process audio tap, clears the artwork
  cache through the script actor, then performs a fresh native player read.
  Generation checks reject old poll/command/cover results. The same lyric query
  reloads after stop; it may reuse persistent cached data.
- Settings changes are saved while the recovery owns the runtime, and the resumed
  services use the latest settings. Media disabled remains disabled. A stopped
  supported player is a valid native read; absent players return an empty state.
  A running selected player with no readable session reports the real native
  error. Refresh never asks for Automation permission: Connect remains explicit.
- Quit cancels recovery, joins its retirement and then drains the normal owners.
  Native script requests have 3-second timeouts, optional artwork 1 second.
  A permanently nonreturning system API still needs a verified application-wide
  shutdown policy; the 20-second button result does not claim such work exited.

## Evidence and limits

On this Mac (macOS27.0.1, arm64/Xcode27.1), two focused Swift tests passed in
`build/music-reconnect-tests.log`: timeout plus duplicate admission/late success,
and failure plus quit/confirmed retirement. The timeout test deliberately uses a
noncooperative continuation. These tests validate ownership, not a real OS hang.
`build/music-reconnect-native-build.log` passed.

Actual `MainLyricsNativeSmoke` profile:

1. The rebuilt ad-hoc app initially received Automation status `-1744`.
   Refresh displayed failure and became retryable. Connect requested permission;
   the human granted it. No security setting was changed by the agent.
2. Refresh while the authored silent `DropSpace Validation` song was paused at10s
   returned Music reconnected, restored the actual track and60-line local fixture,
   and centered its active line. The settings JSON remained byte-identical.
3. Play, refresh while playing, pause and seek to10s completed through native app
   controls. Music itself confirmed paused10s. One stale AX element error required
   a fresh tree before the successful seek.
4. Normal Quit exited. Evidence lives in `build/MusicReconnectNativeEvidence`.

Spectrum/AI were off in this particular profile. It does **not** verify retirement
of active capture/inference, stale remote-provider completions during reconnect,
native permission revocation, native timeout or sleep/display/player changes.
The main playback/lyrics/inline settings composition now has actual native
metadata and CPU/Metal recovery evidence in AI-RECOVERY.md. Other player adapters,
optional enhancement/session management and full layout/interaction acceptance
remain unfinished.


## Source-selection correction and native evidence

The frozen `NativeIslandSettingsPolicy`, `WindowsMediaSessionService.SetAllowedSources`
and `MusicPage` source rows require a nonempty stored list to restrict even if the
explicit flag is false. The initial Mac implementation ignored that case and used
case-sensitive lookup. `MediaSourcePolicy` now preserves the128-source/512 UTF16
bound, case-insensitive identity, all-clears/list-seeds transitions and explicit
empty restriction. Native rows combine discovered and saved offline sources
(maximum512); choosing a source does not grant permission or invent an adapter.

`NativeMediaService` immediately withdraws a disallowed current snapshot, rejects
commands for it and filters candidate reads. The Connect entry obeys the same
restriction. Playback operations have one owned slot, canceled on configuration
change/stop and drained before native retirement. An already delivered system
command cannot be undone; its bounded call must still return before releasing
ownership. This active-operation race has not yet been exercised natively.

`MediaSourcesNativeSmoke` / `build/MediaSourcesNativeEvidence/receipt.json`:
- A saved uppercase Apple Music identity with flag=false still restricted sources;
  the authored silent recording remained paused10s with60 local lyric rows.
- Native uncheck persisted exclusion and retired track/lyrics; recheck restored
  the same recording/position/lyrics. The saved offline source stayed visible.
- All=true saved an empty unrestricted list; all=false seeded actual current
  Music. Unchecking the last source saved an empty **restricted** list.
- Normal Quit exited57359. Initial UI exposed an inapplicable Connect button for
  the excluded source; fixed its filter. Final build restarted empty-restricted
  without that button, then selection restored paused10s. Quit exited58034.
- No playback/seek/library/security change, network lyric source or AI inference
  was used. This is one-player/macOS27.0.1 evidence, not a simultaneous-player,
  failure/write rollback, active command retirement or macOS26 acceptance.

Two selected policy tests passed0.001s (`media-source-policy-retry-tests.log`).
The first run failed and crashed after using a stateful lazy collection filter;
replaced it with an explicit bounded loop and retained the failed log.
`media-applications-native-build.log` and
`media-applications-connect-filter-build.log` succeeded. Builds and catalog
integrity are separate from the native observations above.

# Beta 18 round 1 — App media and audio services

Review source: immutable `/workspace/scratch/beta18-round1`; the initial review did not edit production files. The canonical `/workspace/DropSpace/AGENTS.md`, `.agents/skills/dropspace-maintainer/SKILL.md`, its App/UI reference, and relevant architecture guidance were read. This is a full-source manual review, not a diff-only review. Subsequent authorized fixes are recorded below. No tests or builds were run.

## Confirmed findings

### R1-MEDIA-01 — A retired provider callback can revoke the current track's AI translation

**Priority:** P2. **Files:** `src/DropSpace.App/Services/Media/MediaExperienceService.cs:509–522,544–554`; `src/DropSpace.App/Services/Media/AiLyricsService.cs:227,229–243`.

Provider preview callbacks check `IsLyricsRequestCurrent` at line 509, then clean the document and call `AiLyrics.ObserveSource(cleaned)` at line 521. Final-source publication likewise checks the request at line 544 and calls the same unfenced method at line 554. Both checks precede the actual global AI-state mutation. Each request has its own `sourceGate`, so that lock does not serialize different songs. `ObserveSource` deliberately passes `static () => true` to `TryObserveSource`; the latter therefore replaces `_sourceAdmission`, increments `_statusGeneration`, and requests cancellation of whichever `_activeRequest` is current without checking the originating song.

An A-provider callback can pass its request check, be descheduled, and resume after an A→B transition has started B's source/translation. Its late call then installs A's admission and cancels B. The UI callbacks have current-request checks, so A's lyrics do not need to appear for this fault to occur: B's own progress/final publication fails the source fence, leaving B with source lyrics and no automatic AI retry. The race also exists at the final-source call after asynchronous language preparation. Cancellation and generation changes can happen concurrently with these provider workers.

**Minimal fix:** expose the existing fenced `TryObserveSource` behavior to the media coordinator and pass `() => IsLyricsRequestCurrent(session, settings, generation, token)` at both call sites. Evaluate that predicate while holding `AiLyricsService._stateGate`, as `TryObserveSource` already does, before changing admission/status or capturing a cancellation target. Keep the UI dispatch checks as well. Avoid an additional outside-lock precheck as the sole authorization for this mutation.

### R1-MEDIA-02 — A different recording with the same title does not wake a dismissed music island

**Priority:** P2. **File:** `src/DropSpace.App/Services/Media/MediaExperienceService.cs:396–399`. Supporting policy: `src/DropSpace.Core/Island/IslandExperienceCoordinator.cs:63–69,89–96,118–143`; runtime track identity: `src/DropSpace.Core/Media/MediaModels.cs:42–43,56–66`.

The music coordinator supplies the island with `SessionId`, source application, and track title as its `contentIdentity`. After the user dismisses a playing music island, the Core coordinator resets that dismissal only when this identity changes or playback becomes active after being inactive. A continuously playing player that advances to another artist's same-title song, or another album/track-number recording of the same title, keeps all three supplied identity fields unchanged. The app recognizes the recording change through `IsSameTrack` and reloads its lyrics/artwork, while the island retains `_dismissed` and stays hidden through the new recording.

**Minimal fix:** pass `session.TrackIdentity` to `UpdateMedia`; that existing runtime key includes artist, album artist, album, and track number while excluding position and volatile timestamps. This finding was independently suggested by the Core review and verified against the complete media coordinator source and the dismissal policy.

## Coverage

All 17 requested files, totaling 3,289 lines, were physically read from first through last line:

| File under `src/DropSpace.App/Services` | Lines |
| --- | ---: |
| `Audio/ProcessLoopbackInterop.cs` | 65 |
| `Audio/WindowsProcessLoopbackService.cs` | 207 |
| `Media/AiLyricsService.cs` | 525 |
| `Media/BoundedMediaOperation.cs` | 93 |
| `Media/LyricsRapidSkipDiagnostic.cs` | 70 |
| `Media/MediaApplicationIconService.cs` | 48 |
| `Media/MediaArtworkService.cs` | 38 |
| `Media/MediaEventSubscription.cs` | 49 |
| `Media/MediaExperienceService.cs` | 775 |
| `Media/MediaLyricsRefreshRequest.cs` | 19 |
| `Media/MediaProcessResolver.cs` | 61 |
| `Media/MediaSessionOwner.cs` | 53 |
| `Media/MediaSoftRestartOperation.cs` | 67 |
| `Media/MediaSubscriptionAdmission.cs` | 12 |
| `Media/QqMusicLoginService.cs` | 198 |
| `Media/RetirableMediaWork.cs` | 127 |
| `Media/WindowsMediaSessionService.cs` | 882 |
| **Total** | **3,289** |

Supporting reads traced `AiLyricsWorkLifetime`, `LyricsInferenceCircuit`, `LyricsCache` policy publication, `NativeAsyncLifetime`, `AudioCaptureRecoveryPolicy`, and production composition/call sites. In particular, the apparent absence of cache-quota publication in `MediaExperienceService.OnSettingsChanged` is covered by `SettingsApplicationCoordinator`, so it is not reported as a defect.

The other files had no additional sufficiently evidenced finding in this pass. Manual inspection covered native cancellation ownership, retained subscription pools, preparation/commit ownership, model/cache maintenance, dispatcher publication, loopback packet release, login-window lifecycle, and bounded recovery. Windows COM/WinRT execution and real-model behavior remain outside this no-test review.

## Authorized fix notes

Both media findings were minimally fixed in the live `/workspace/DropSpace` checkout. The immutable review snapshot was left unchanged. Production changes are confined to `AiLyricsService.cs` and `MediaExperienceService.cs`.

- **R1-MEDIA-01:** `AiLyricsService.TryObserveSource` is now assembly-visible (`AiLyricsService.cs:229`). Preview and final source publication call it with their existing song/settings/generation/token predicate (`MediaExperienceService.cs:520–522,553–556`). The predicate still runs inside `_stateGate` before `_sourceAdmission`, `_statusGeneration`, or `_activeRequest` changes (`AiLyricsService.cs:234–241`). Rejected observations return from the originating provider callback or lyric-load method before replacing `observedSource`. The public unfenced `ObserveSource` compatibility API remains available to existing diagnostics/tests; the media coordinator has no remaining calls to it.
- **R1-MEDIA-02:** `UpdateMedia` receives `session.TrackIdentity` (`MediaExperienceService.cs:396–399`), matching the existing runtime key for artist/album/track-number transitions. Position/timeline timestamps remain excluded, so ordinary playback refreshes do not wake a manually dismissed island.

Diff review confirmed two changed production files, seven inserted lines and five removed lines, with the original CRLF convention preserved. `git diff --check` passed for these two files. Source inspection verified both fenced observation call sites, the under-lock predicate ordering, and the updated identity against the Core dismissal policy. No tests, build, Windows execution, remote action, or commit was performed; these fixes have source-review evidence only until later authorized runtime verification.

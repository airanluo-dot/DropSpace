# Preview.24 rebuild execution ledger

## Scope and source

User-authorized full plan: `E:/Dev/reference/Preview24-plan/DropSpace_Preview24_WindowsLocal_FullRelease_Codex_Package/DropSpace_v0.3.0-preview.24_WINDOWS_LOCAL_FULL_RELEASE_PLAN_for_Codex.md`.
All 15 target/failure images were opened individually before UI work.
Baseline: `820daa9ed02bd2943100a718b3c16acd1b93cdb8` (Preview.21).
Branch: `agent/v0.3.0-preview.24-rebuild-from-preview21`.
Do not merge Preview.22/23 or transplant their UI/state machine.
Preserve the pre-existing untracked `.codex/environments/` configuration.

## Checkpoints

- `13491f6`: SDK 10.0.401 in global.json, CI and release workflow. Solution restore passed.
- Baseline Release WinUI build passed on this Windows host: 0 warnings, 0 errors.
- `67c917a`: build 20348, settings schema 14 and migration coverage. Release/Debug WinUI builds: zero warnings/errors. Migration/concurrency: 7 passed; Core compatibility/settings: 19 passed. Compatibility and localization gates passed.
- No Preview.24 release has been prepared, pushed or published.

## Media backend checkpoint

SMTC service rebuilt with a one-slot coalescing event channel, serialized lifecycle,
cancellable five-second reads, source selection, bounded metadata/artwork and drained
shutdown. Release build passed with zero warnings/errors. Real Windows SMTC smoke
passed: enable, observed snapshot, disable/drain, re-enable, disable. This does not
yet prove playback controls or artwork against a playing app.

The App test packaging emitted PRI257/PRI263 for MSTest's `zh-hans` satellite
resource without an `en-US` satellite. These are test-host localization warnings,
not shipped App resource failures; the native test executed successfully and App
localization parity passed. Retain this distinction in final validation.

## Process loopback checkpoint

The new dedicated MTA adapter activates Windows process loopback for a resolved
SMTC process, consumes real stereo PCM and computes six Hann-windowed FFT bands.
Capture stops and drains on pause/source removal. Process identity resolution is
bounded and refuses ambiguous matches. Activation parameters survive cancellation
until the asynchronous Windows callback completes.

Real Windows native tests passed (2/2, 629 ms): SMTC enable/disable/re-enable and
WinMM-rendered quiet test tone captured through WASAPI, nonuniform FFT bands,
pause, restart and stop. Test evidence: `preview24-media-native.trx` under the App
test project's ignored TestResults directory. COM completion interfaces must be
public and COM-visible so Windows can query their agility; the original private
CCW returned E_ILLEGAL_METHOD_CALL and was fixed, not bypassed.

This checkpoint does not yet establish identity resolution or music capture for
an external player, which remains a required end-to-end gate.

## Build notes

## System observer and widget checkpoints

- Notification listener: Release build zero warnings/errors; native lifecycle test
  passed. Actual access in the unpackaged test host was Unavailable. This is only
  graceful-unavailability evidence; packaged permission and incoming-toast tests
  remain open. No history replay and no automatic permission request.
- Volume observer: real default render endpoint callback test passed (389 ms).
  The test changed one percentage point and restored the exact previous scalar in
  finally. Disable/re-enable passed. Actual output-device switching remains open.
- Widget data: real system time, CPU delta and memory sampling passed (1 s).
  Hiding cancels and drains sampling. This service has no island presence API.

## Build notes

Use the checked-in CI commands. A build with an explicit global `RuntimeIdentifier=win-x64`
can regenerate Core/Infrastructure lock files with RID sections; rerun ordinary solution
restore before locked solution restore. Do not commit that incidental lockfile churn.
Git HTTPS direct transport was reset; per-command use of the existing Windows user proxy
allowed fetch/clone without changing repository or global proxy configuration.

## Required remaining order

Lyrics backend: separate NetEase/QQ/Kugou/LRCLIB/AMLL/local adapters, bounded HTTP,
LRC/YRC/TTML parsing, matching, word timeline and memory cache are implemented.
Live adapter smoke on this host returned 50/14/14/49/54 timed lines respectively;
AMLL returned 405 word timings. This proves provider retrieval/parsing, not yet
same-player visual synchronization with WinIsland. Offline LRC/YRC/TTML/timeline,
local metadata matching and edited-file reread smoke passed. No lyrics are logged.

1. Finish platform/schema validation and checkpoint.
2. Audit pinned WinIsland and Microsoft/Files/EarTrumpet sources; record parity and license boundaries.
3. Backend checkpoints: SMTC, lyrics providers/matching, process loopback/FFT, system observers, widget data.
4. Bounded material and focused/unfocused dark/light native smoke.
5. Compact media with real lyrics, PCM spectrum and actual XAML measurement; native smoke.
6. Independent Files/Music/Widgets pages preserving Preview.21 Files UI; native smoke.
7. Music page and working widget/settings editor; restart persistence smoke.
8. Transient activities and final product hardening.
9. Full local Windows gate, required GitHub checks, merge, release, five assets/checksums, website/API verification.
10. Synchronize and verify repository and installed `dropspace-codex` maintenance skills.

Do not mark completion or publish with any plan release blocker unresolved.

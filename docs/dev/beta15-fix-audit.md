# Beta15 implementation and verification record

This is a working delivery record for the fifteen-item October 6 owner request.
Reviewed published Beta14 App source: `bf4b97a893476b27d9007aa7ea0a511ae8ac90fe`.
Beta15 uses the current shared checkout and retains existing downloader, settings,
models, components, user library and normal cache files. Earlier release assets
and historical approval/evidence files are not replaced.

## Item status

| Item | Code and confirmed cause | Verification / remaining evidence |
| --- | --- | --- |
| 1 Native translations overwritten by AI | Language admission and snapshot publication protections are being integrated in `LyricsLanguagePolicy`, `LyricsTranslationOutput`, `PlainHyLyricsBackend`, `AiLyricsService` and `MediaExperienceService`. This row must be completed using the actual captured song/response chain, rather than attributed from static clues. | Parent investigator owns before/after playback evidence, exact recording/source identities and coverage. Pending final record. |
| 2 Chinese/English missed rows | Host policy v11 and its new 48-line computation preserve independent fixture annotations. No model classifier or extra translation prompt is introduced. | Parent investigator owns actual songs, row outcomes and model execution. Controlled host output is not native inference evidence. |
| 3 Performer and recording identity | `LyricsQuery.ArtistCandidates` no longer treats a narrower AlbumArtist as an equivalent alternative to present performer metadata; matcher identity advances to v3. | Parent investigator to record focused checks and any actual song recheck. |
| 4 Sliders | Shared and independent slider readouts have fixed columns; settings ownership distinguishes queued, saving and active pointer interaction; completion/leave flushes the last edit. | Parent investigator owns actual dragging at fixed window/DPI, held-pause and focus/page transitions. Pending final record. |
| 5 Lyric width wording | `MusicPage` uses independent `LyricsWidthUnlimited` bilingual resources rather than download speed text. Existing numeric range, natural short-row width and monitor limit are retained. | App compilation and actual width control observation pending. |
| 6 Paused music and timer | Static cause confirmed: a single playing/presence value also removed content. The coordinator now receives content availability and playing separately; same-track paused content, cover, lyrics and established audio-bar presentation remain through the saved hide deadline. Repeated pause retains the deadline, resident content remains, resume retires it. | Five focused coordinator lifecycle tests passed. Actual pause/resume layout, audio-bar state and timeout rendering remain for parent verification. |
| 7 Idle Logo flash | Static cause confirmed: `EmptyWake` made the Logo unconditionally visible during transition, ignoring the idle preference. `OverlayWindow` now applies the saved idle preference to EmptyWake and Idle while retaining the legitimate FileLogo variant. | Source reviewed; actual expansion/collapse/rapid click frames pending. |
| 8 Auto-hide animation | Static cause confirmed: expiry went directly to Hidden, reaching the immediate native hide path before the existing spring animation; near-transparent motion also cleared the native region before settling. Expiry/manual dismissal now enters Dismissing. The existing motion/page transition completes before native hiding and a generation-bound completion acknowledgement. New valid visibility reverses hiding and stale completions are rejected. No second full delay is added. | Five focused coordinator tests passed, including stale completion, new file/open/resume, resident and immediate manual paths. Real rendered animation, reduced-motion and cancellation frames pending. |
| 9 Model delivery approval fingerprint | Static omission confirmed: model delivery authorization code, embedded transport JSON and Infrastructure resource binding were absent. The code-owned scope now includes all three. JSON hashes exact embedded bytes; source hashes normalize LF. Resource names, binding and literal inventory are checked before reuse. Removed, redirected, globbed or unlisted delivery resources fail closed. | Four focused Node fingerprint checks passed. They cover host/manifest changes, URLs/sizes/hashes, BOM/newlines, missing/redirected/unlisted bindings and rejection before artifact reuse. No model execution or transport download was performed. |
| 10 ConfirmedInstrumental | Provider reporting, candidate collection, cache/fallback selection and final outcome now preserve identity-validated LRCLIB evidence; verified usable lyrics retain priority over empty-body evidence. | Parent investigator to add focused-check and any actual provider evidence. |
| 11 Completed download cleanup | Auxiliary cleanup has separate pending/error fields, independent of transfer outcome and UI failure text. | Downloader investigator owns focused verification; pending final record. |
| 12 Reservation ownership | History removal retains a hidden durable cleanup owner until marker release and owned staging cleanup complete; deletion failures surface and retry after restart without deleting the final file. | Downloader investigator owns focused verification; pending final record. |
| 13 Two-second operation text | Static cause confirmed: `CompactFileTitle` ignored `_shellAcknowledgement`, although the existing guarded two-second lifecycle still set/cleared it. The getter now prefers valid acknowledgement text, then restores the current filename/count. Existing independent timer and ownership guard remain. | Source reviewed; actual consecutive operations and title restoration pending. |
| 14 Immediate cache stop | The saved policy is synchronously applied before the settings success boundary returns, shared by source/AI stores. Background trimming reads the latest policy and cannot reapply a stale quota. Disabling persistence retires cache generations, not live inference. | Parent investigator owns focused cache checks and settings/App verification. No automatic old-cache/model deletion is introduced. |
| 15 Build and real verification | No full suite, extra model download, bilingual rebuild matrix or encryption regression is requested. Actual song and animation observation remains distinct from code inspection and controlled host checks. | Final App commit, version, package hashes, real observations, limitations and release links must be filled by the delivery owner. |

## Checks recorded so far

- `node --test --test-name-pattern="^model delivery" scripts/test-ai-release-approval.test.mjs`: four passed. This isolates the model-delivery fingerprint checks; it is not the whole release-gate suite.
- `dotnet test tests/DropSpace.Core.Tests/DropSpace.Core.Tests.csproj --no-restore --filter FullyQualifiedName~Beta15IslandLifecycleTests --verbosity minimal`: five passed; Core and Core.Tests compiled. This checks coordinator state/timer generations, not WinUI animation rendering.
- Parent investigator reports eleven directly focused Infrastructure host checks passed and a freshly generated v11 host admission computation. Exact command/result and coverage are to be entered by that investigator. Controlled returns do not establish native model/CUDA success.

Modified older assertions in `IslandPresencePolicyTests`, `Beta13RegressionTests`
and `Beta14FocusedTests` reflect the two-phase Dismissing/Hidden contract. These
older classes were not run by the island/fingerprint investigator; no pass is
claimed for them. No App compilation or UI observation is claimed here yet.

## Release and approval preparation

The fresh Beta15 owner record and source scope are preparation, not proof that
the remaining live checks have completed. Historical model captures retain their
original budgets, failures and qualification limits. New source edits after
preparation make the review stale until explicitly inspected and rebound.

Required Beta15 release-path adjustments:

- Exact Beta15 allowance for reviewed CUDA metadata staging and compiler-input-only installer inspection. The original native payload/manifest identities stay pinned; actual App source SHA and the new asset name are rebound for Beta15.
- Exact Beta15 CI/website test waiver for unrelated broad checks; mandatory compilation/packaging and byte/metadata binding still apply. Existing Beta14 asset and App commit pins remain untouched.
- A fresh staging directory is required by `stage-reviewed-cuda-metadata.py`. Preserve prior metadata before staging; never relabel an existing Beta14 App package as Beta15.
- Release workflow promotion must use a fresh, source-identical Beta15 bundle. Its ordinary no-promotion path otherwise runs broad regression steps; this is not satisfied by the existing Beta14 pinned reuse route.
- Final release uses a new Beta15 installer, PE/MSIX version, SHA256 inventory, update manifest and runtime publication binding. Parent delivery owner will complete those fields after actual verification.

## Actual playback, animation and final build

Pending parent investigator entries: song/performer/album/version/duration,
request/source/candidate identity, original response and aligned per-row origins,
first overwrite stage, fixed native coverage and AI-gap behavior, cache/refresh/
switch-target/return-to-track observations; pause/idle animation and Logo frames;
App commit and package identities. Historical screenshot guesses are not exact
recording evidence. A successful build or artifact hash is not proof of these
behavioral results.

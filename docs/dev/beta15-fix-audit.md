# Beta15 implementation and verification record

This is the code, build and verification record for the fifteen-item October6
owner request and three directly related supplements. All eighteen code items
are complete; actual verification and remaining limits are recorded separately.
Delivery status: all nine new Beta15 assets are built, validated and published
as Release404561576 at2026-10-06T10:07:38Z. The website index awaits main-branch
sync; the GitHub release and downloads are public.
Reviewed published Beta14 App source: `bf4b97a893476b27d9007aa7ea0a511ae8ac90fe`.
Beta15 uses the current shared checkout and retains existing downloader, settings,
models, components, user library and normal cache files. Earlier release assets
and historical approval/evidence files are not replaced.

## Item status

| Item | Code and confirmed cause | Verification / remaining evidence |
| --- | --- | --- |
| 1 Native translations overwritten by AI | The captured `her` source contained 48 valid native rows before AI; 41 were overwritten in the baseline AI document. Admission misclassified short untagged native Chinese and frozen AI output could overwrite `Secondary`. `LyricsLanguagePolicy`, `LyricsTranslationOutput`, `PlainHyLyricsBackend`, `AiLyricsService` and `MediaExperienceService` now protect native target text twice and reconcile against the current source version and stable timed occurrence. | First candidate `f603e403`: all 48 native hashes/IDs retained, five actual gaps supplemented; `falling` retained all 24 native rows without new AI admission, including A→B→A and manual refresh. Final `734e0a96`: `My City` retained all 45 native rows plus one AI gap; DAISIES retained all 29 native rows without AI. Native text, stable occurrence/time and provenance were compared in the final trace. |
| 2 Chinese/English missed rows | Host policy v12 inherits only continuous local Han verses from at least two distinct original Chinese anchors; explicit tags, foreign text, time/blank boundaries stop inheritance. Structured bilingual credits and accepted bilingual title headers are filtered before script splitting. The captured Chinese recording had ten ordinary rows excluded by v11 admission; the other ten insufficient-evidence rows were metadata, not missed lyrics. Independent fixture annotations remain; no classifier, test-sentence wordlist or extra model prompt is added. | Final actual Chinese `风的来信`: System/zh-CN skips all 38 sung rows as same-language and 14 metadata rows as credits. Temporarily choosing English produced 38 complete AI rows with zero failed/pending rows; all 14 metadata rows stayed excluded. Production CUDA worker/GPU memory evidence accompanies the actual UI and trace. This covers the observed recording, not every mixed-language song. |
| 3 Performer and recording identity | `ArtistCandidates` no longer uses narrower AlbumArtist to rescue present performer conflicts. Ordinary album words `live/remix/acoustic` no longer veto a correctly identified track. Matcher v5 keeps title featured performers in required identity, narrowly corroborates the evidenced `My City` release entity, and QQ revision3 requires same-ID early vocal-credit proof before publishing a candidate missing a featured performer. Explicit versions/languages, full credits and duration remain mandatory. | Two Album, four Apple Music credit and five QQ credit checks passed. Final actual `My City` and Chinese `风的来信` reached identity-bound NetEase candidates. Actual English `风的来信` rejected the Chinese QQ vocal-credit candidate and honestly returned NotFound for this attempt; prior `golden hour`/`初光` and Chinese/Japanese safeguards remain. |
| 4 Sliders | Shared and independent slider readouts have fixed columns; settings ownership distinguishes queued, saving and active pointer interaction; completion/leave flushes the last edit. | On the first candidate, actual cache 0/1/10 GB, negative/zero/positive offset and finite/unlimited width dragging kept the same rail endpoints (87→665 at the observed size/DPI). Preferences were restored. Holding past debounce then continuing, focus/page transition and final candidate remain unverified. |
| 5 Lyric width wording | `MusicPage` uses independent `LyricsWidthUnlimited` bilingual resources rather than download speed text. Existing 80–600/default260/step10 plus unlimited, natural short-row width and monitor limit are retained. | First candidate finite/unlimited controls were observed; final App compiled from `734e0a96`. No repeated bilingual App build was run. |
| 6 Paused music and timer | Static cause confirmed: a single playing/presence value also removed content. The coordinator now receives content availability and playing separately; same-track paused content, cover, lyrics and established audio-bar presentation remain through the saved hide deadline. Repeated pause retains the deadline, resident content remains, resume retires it. | First candidate retained music after pause; the actual trace recorded a 2007 ms wait for the saved two-second preference before dismissal. Initial five coordinator checks and the later nine-case run passed; the latter includes those original five. |
| 7 Idle Logo flash | Static cause confirmed: `EmptyWake` made the Logo unconditionally visible during transition, ignoring the idle preference. `OverlayWindow` now applies the saved idle preference to EmptyWake and Idle while retaining the legitimate FileLogo variant. | First candidate and final actual empty capsule→expanded empty panel→capsule kept effective idle/file Logo visibility false. Final file revisions advanced independently while the idle capsule remained visible. Rapid clicks and click during hide-wait remain unobserved. |
| 8 Auto-hide animation | Static cause confirmed: expiry went directly to Hidden, reaching immediate native hide before the existing spring animation; near-transparent motion also cleared the native region before settling. Expiry/manual dismissal now enters Dismissing. Existing motion completes before generation-bound native hiding; new valid visibility reverses hiding and stale completions are rejected. No second delay is added. | First candidate pause/empty capsule rendered wait→spring opacity→settled0→region release. Final actual file state completes separately under Resident; with Resident off the saved2000ms wait precedes opacity0.4938/0.1653/0.0059→settled0→region release/Hidden. Nine Core checks include independent file completion. Physical reversal/reduced-motion remain unobserved. |
| 9 Model delivery approval fingerprint | Static omission confirmed: model delivery authorization code, embedded transport JSON and Infrastructure resource binding were absent. The code-owned scope now includes all three. JSON hashes exact embedded bytes; source hashes normalize LF. Resource names, binding and literal inventory are checked before reuse. Removed, redirected, globbed or unlisted delivery resources fail closed. | Four focused Node fingerprint checks passed. They cover host/manifest changes, URLs/sizes/hashes, BOM/newlines, missing/redirected/unlisted bindings and rejection before artifact reuse. No model execution or transport download was performed. |
| 10 ConfirmedInstrumental | Provider reporting, candidate collection, cache/fallback selection and final outcome now preserve identity-validated LRCLIB evidence; verified usable lyrics retain priority over empty-body evidence. | The parent ran three `Beta15LyricsSelectionTests` in the initial eleven-case Infrastructure run: backup/remaining failures, usable lyric priority and unsafe-empty rejection, plus cache/progressive-failure retention. These are controlled provider returns; no actual LRCLIB instrumental recording was played. |
| 11 Completed download cleanup | Confirmed code path: `MarkerCleanupDeferred` previously occupied transfer `ErrorCode` after atomic publication and the UI displayed a generic failure. Cleanup now has separate pending/error fields; Completed remains Completed, including migrated records. | Initial focused Infrastructure run covers tiny real loopback transfers/locked markers. Later persistence/retry edge guards were statically reviewed and included in the first App build, but not separately rerun. No large transfer stress test. |
| 12 Reservation ownership | Confirmed code path: history/Work removal preceded reservation release, and marker delete errors were swallowed. Removal now retains a hidden durable cleanup owner until marker release and task-owned staging cleanup complete; failures retry without deleting final files or foreign markers. | The initial Infrastructure run covered marker locks, restart, moved final file and same-name recovery. Persistent journal/directory failures can still delay cleanup; no claim of success while external access remains blocked. |
| 13 Two-second operation text | Static cause confirmed: `CompactFileTitle` ignored `_shellAcknowledgement`, although the existing guarded two-second lifecycle still set/cleared it. The getter now prefers valid acknowledgement text, then restores the current filename/count. Existing independent timer and ownership guard remain. | Source reviewed; actual consecutive operations and title restoration were not observed. |
| 14 Immediate cache stop | The saved policy is synchronously applied before the settings success boundary returns, shared by source/AI stores. Background trimming reads the latest policy and cannot reapply a stale quota. Disabling persistence retires cache generations, not live inference. | The initial Infrastructure cache test covers real shared-store/in-flight writes. The parent also passed the one App settings test covering persisted zero, synchronous source/AI fencing, Windows locked-file save failure retaining zero and Load re-enable. First-candidate 0/1/10 GB controls were actually observed; old-cache/model files are retained. |
| 15 Build and real verification | Necessary final compilation, focused actual playback and packaging are complete. Actual song and animation observation remains distinct from code inspection and controlled host checks. | Final portable/installer/MSIX compilation and source/runtime publication, approval gate and strict update-manifest checks succeeded. Actual final MyCity/DAISIES/ChineseWind and island results are below. All nine assets are publicly uploaded; no broad/full regression or installation lifecycle is claimed. |

The directly related supplements are also complete in code:

| Supplement | Status | Confirmed defect and change | Verification |
| --- | --- | --- | --- |
| Private worker deadline | Completed in this round | A private request deadline could discard already successful progress. Queue/request timeouts now become typed failures after worker cleanup; pending rows fail while valid native/completed rows remain, without a partial-song cache success. Production remains60s/request. | Six focused Infrastructure protocol-fixture checks passed; the final real CUDA38-row request completed without fallback. Deadline fixture returns are not model inference. |
| Independent file dismissal | Completed in this round | Empty file state could remain Dismissing after the overlay had returned to Compact, leaving later completion/hide ownership stuck. File revision completion is independent and stale settled callbacks cannot hide a newer display. | Nine Core checks passed, including the original five; final actual Resident/on and saved-delay/off empty-panel transitions completed file Hidden before overlay animation/region release. |
| Ordinary album wording | Completed in this round | Generic live/remix/acoustic words in an ordinary album name falsely implied a hard track-version conflict. Title/version, full performers/language and duration remain strict, with reasonable album corroboration. | Two focused Core checks passed, including ordinary `Long Live Rock ’n’ Roll` wording and genuine conflict rejection. |

## Checks recorded so far

- `node --test --test-name-pattern="^model delivery" scripts/test-ai-release-approval.test.mjs`: four passed. This isolates the model-delivery fingerprint checks; it is not the whole release-gate suite.
- `dotnet test tests/DropSpace.Core.Tests/DropSpace.Core.Tests.csproj --no-restore --filter FullyQualifiedName~Beta15IslandLifecycleTests --verbosity minimal`: five passed; Core and Core.Tests compiled. This checks coordinator state/timer generations, not WinUI animation rendering.
- The parent ran eleven directly focused Infrastructure host checks: five translation protection, three lyrics selection, two download cleanup and one cache-policy test. Download checks use tiny loopback transfers and real Windows marker locks; the cache check uses the shared store and an in-flight filesystem write. The generated v11 host computation retains its original identity; the later v12 snapshot is separate. These host checks do not establish native model/CUDA success.
- The parent separately passed the one `Beta15CacheSettingsTests` App transaction test: saved zero is applied before return; the held Windows settings file causes an actual save failure without re-enabling the cache; loading a successfully saved nonzero setting re-enables access. This is an isolated actual settings store test, not UI drag or install lifecycle validation.

Subsequent necessary checks reported by the responsible investigators:

- `dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~Beta15PlainHyDeadlineTests --verbosity minimal`: six passed. These use a managed child protocol fixture, not weights or CUDA. Actual private worker deadlines are typed failures; completed/native rows are retained, only pending rows fail, and no partial-song cache success is published. Production remains 60 seconds per request.
- `dotnet test tests/DropSpace.Core.Tests/DropSpace.Core.Tests.csproj --filter "FullyQualifiedName~Beta15IslandLifecycleTests" --no-restore --verbosity minimal`: nine passed (five existing plus four added). This verifies separate file completion and hide ownership; it does not re-observe WinUI rendering.
- `dotnet test tests/DropSpace.Core.Tests/DropSpace.Core.Tests.csproj --no-restore --filter FullyQualifiedName~Beta15AlbumMatchingTests --verbosity minimal`: two passed. Ordinary album names are accepted while conflicting versions/languages/performers/durations remain rejected.
- The recording investigator reported four `Beta15AppleMusicCreditTests` and five `Beta15QqRecordingCreditTests` cases passed, using the actual captured metadata/response shapes with strict negative cases. These do not operate Apple Music or prove final UI output.
- The context investigator reported seven `Beta15ContinuousLanguageContextTests` passed. The parent ran five translation-boundary cases before the last untimed-boundary guard, then reran only the host snapshot method after that directly related adjustment (one passed). Final v12 JSON binds the actual current policy hash and unchanged independent fixture; neither flags model inference nor semantic approval.

An earlier context diagnostic combined the new class with
`AuditLyricsAdmissionTests`: 33 of58 cases passed and25 assertions failed.
Three initial assertions in the new class were corrected, then that class's
seven cases passed. The remaining older assertions include expectations before
the v11 policy; they were not changed or rerun and are not counted as passing.
The parent deliberately did not expand this into another old/full regression
run. Their present expectations remain unresolved; this audit does not represent
the overall suite as green.

Modified older assertions in `IslandPresencePolicyTests`, `Beta13RegressionTests`
and `Beta14FocusedTests` reflect the two-phase Dismissing/Hidden contract. These
older classes were not run by the island/fingerprint investigator; no pass is
claimed for them. The first candidate observations and the later final App
compilation are recorded separately below; one cannot validate the other.

Three directly related supplements are complete in source: private deadline
failure retains valid progress (six Infrastructure checks), file dismissal
finishes under independent ownership (nine Core checks, including the initial
five), and ordinary album words do not fabricate recording-version conflicts
(two Core checks). These checks do not establish full-song model coverage,
physical WinUI frame behavior or installer lifecycle.

## Release and approval preparation

The fresh Beta15 owner record and source scope are preparation, not proof that
the remaining live checks have completed. Historical model captures retain their
original budgets, failures and qualification limits. New source edits after
preparation make the review stale until explicitly inspected and rebound.

Preparation completed at the recorded owner-decision boundary on October 6.
`sourceReview.reviewedInputs` in
`scripts/ai-model-qa/evidence/v0.3.1-beta.15-owner-decision.json` records the 41
changed/new production inputs and actual preparation-input hashes. Each was
rechecked against `readScope` before invoking the preparation helper, including
the latest settings-publication, directory-edit and opt-in animation trace code.
The new host-only admission computation was explicitly reviewed; independent
fixture, shipping model/native byte identities, prompt and protocol stayed the
same. The helper bound its prospective Beta15 gate pin into the new scope and
created a fresh `v0.3.1-beta.15-owner-accepted-review.json`; old review files were
not rewritten. A necessary metadata gate invocation succeeded afterward and
explicitly reported model validation unverified and no semantic approval. It
performed no build, model execution, functional test or publication.

After the directly related deadline, file completion, recording-credit and v12
admission corrections, a separate
`scripts/ai-model-qa/evidence/v0.3.1-beta.15-final-owner-decision.json` records the
eleven changed/new inputs and their actual hashes. Its reviewed source digest is
`51fb82262bc1e7198d77ef5e952e1e3329788f98cf21936024897f54806417d8`.
The parent used `--rebind-current` to create
`v0.3.1-beta.15-final-owner-accepted-review.json`, and the necessary metadata gate
against `artifacts/ai-runtime/win-x64/runtime-manifest.json` exited successfully.
The initial owner decision/review and earlier host evidence were preserved.
The new v12 snapshot is an actual host-policy computation with the independent
fixture unchanged; model execution and semantic approval remain false in that
record. Rebinding does not turn first-candidate playback into final-source proof.

Required Beta15 release-path adjustments:

- Exact Beta15 allowance for reviewed CUDA metadata staging and compiler-input-only installer inspection. The original native payload/manifest identities stay pinned; actual App source SHA and the new asset name are rebound for Beta15.
- Exact Beta15 CI/website test waiver for unrelated broad checks; mandatory compilation/packaging and byte/metadata binding still apply. Existing Beta14 asset and App commit pins remain untouched.
- A fresh staging directory is required by `stage-reviewed-cuda-metadata.py`. Preserve prior metadata before staging; never relabel an existing Beta14 App package as Beta15.
- Release workflow promotion must use a fresh, source-identical Beta15 bundle. Its ordinary no-promotion path otherwise runs broad regression steps; this is not satisfied by the existing Beta14 pinned reuse route.
- `Inspect-OwnerWaivedPackages.ps1` now admits exact Beta15 and records the installer compiler's input portable bytes without executing installation or uninstall. Other unlisted releases still fail closed; the isolated-CI requirement remains. This script is a newly reviewed delivery input, not an installation test.
- The published release uses a new Beta15 installer, PE/MSIX version, SHA256 inventory, update manifest and runtime publication binding. Actual final fields and uploaded identities are recorded below.

## Actual playback and rendered evidence: first candidate

The observed process was `artifacts/portable/win-x64/DropSpace.exe`, version
`0.3.1-beta.15`, App source `f603e403ad57d81cd09ea35b7c44bc5c58c186b2`.
The first App compilation/publish succeeded. The subsequently corrected final
App also compiled, as recorded separately below. Never relabel these
first-candidate observations as the final commit's actual playback.

| Recording | Captured identity | Observed result |
| --- | --- | --- |
| JVKE, `her`, Apple Music `her - Single` | SMTC171s, NetEase ID2621307695, catalogue171.757s | Baseline: 55 original/48 native, 41 native rows overwritten. First Beta15 candidate: all48 exact native text hashes and stable IDs preserved; five missing rows translated, no failed rows. |
| JVKE, `this is what falling in love feels like`, Apple Music single | SMTC120s, NetEase ID1875935559, catalogue120.308s (`this is what ____ feels like (Vol. 1-4)`) | 28 original/24 native, AI admission pending0. A→`her`→A and manual refresh retained the same24 native rows with no AI overwrite. |
| `My City (feat. G Herbo)` | Apple Music149s; NetEase2048760838 | Real investigation found publisher entity/album text in Artist/AlbumArtist, with AlbumTitle empty. Its final `734e0a96` actual result is recorded below; final playback now reaches this same recording instead of rejecting its corroborated full performer identity. |
| `风的来信 (feat. 孙晔) [中文版]` | Exact QQ candidate `001gNFEp4gee1w` response captured by the parent | 52 display rows: 38 ordinary lyrics and 14 metadata rows (one title and 13 credits). On the first candidate, 28 ordinary rows were translated and ten were excluded by admission. Of the 20 insufficient-evidence rows, the other ten were metadata; four additional credits were already excluded. Final v12 host admission covers all 38 sung rows and excludes all 14 metadata rows; final actual playback from NetEase is recorded below. |
| Justin Bieber, `DAISIES`, Apple Music `SWAG` | Stable captured SMTC176s; NetEase2724714488 | System/zh-CN source trace shows31 original/29 provider translation rows, and the real UI displays native Chinese. The parent confirmed this is the screenshot's recording; final-source replay separately retained all29 native rows. |

For `her`, the same NetEase HTTP200/business200 response contains 48 valid timed
translations: one metadata and four empty boundary rows account for the other
five raw entries. All48 survive parsing at the exact timestamp; no lost valid
row at the current250ms alignment boundary is demonstrated. No YRC/ytlrc fields
exist in this response, so the known partial-YRC/full-LRC branch does not explain
this case. Its possible effect on other recordings remains unverified. This is
evidence that DropSpace AI handling overwrote available source translations,
not evidence of a NetEase translation outage.

The parent observed the production CUDA worker (PID10408), real ready and three
completed responses with `cpuFallback=false`, alongside that worker in NVIDIA's
compute-process list. This is actual App-chain inference evidence using installed
model/component bytes; a display label or GPU detection alone was not used as
the success criterion. It is not broad hardware or semantic model qualification.

Local investigation evidence is retained under `.codex/beta15/evidence/`:
`development-app-identity.json`, `her-overwrite-baseline.json`,
`her-preservation-result.json`, `her-alignment-review.json`, the same-ID raw
responses/HTTP metadata, `beta15-real-playback.ndjson`, before/after `her` and
`falling` screenshots, A→B→A/refresh screenshots, slider screenshots and paused
music screenshots. Raw copyrighted lyrics/account material are local diagnostic
input, not release assets or production log additions. Public audit values use
counts, identities and hashes.

Additional local screenshots/logs include `daisies-beta15.png`,
`empty-island-expanded.png` and `island-actual-animation.log`. During the empty
island observation, Music/Resident were changed temporarily; the parent restored
Music enabled, System language, AI enabled, Resident false, idle Logo off, hide
2000ms, offset0, width300 and cache1GB. The captured stable DAISIES duration is
176 seconds; a preliminary verbal 2:36 was inconsistent and is not used as its
recording identity. DAISIES had 31 original/29 native rows and no admitted AI
rows in that observed source document.

## Final App build record

The parent completed the necessary final portable App compilation from
`734e0a96366fcffae4d3e8361745e4ba549ebf06` and started PID9608 for the focused
affected-song replay. The executable reports PE version `0.3.1.15`, has
356,373,112 bytes and SHA256
`a62bded3520f6e7940cbb06d023bb85bce2c593c61b6ab305a6546e55f378517`.
The actual final replay log is `.codex/beta15/evidence/beta15-final-playback.ndjson`.
These facts establish compilation and executable identity. The standard installer
compiler also succeeded in94.578s without executing installation or uninstall:
`DropSpaceSetup.exe`, 82,339,856 bytes, SHA256
`e18a5b61082da0ddbb24a83a931d16d608b9dcac89bc2d622ace80545c950fab`.
`DropSpace-x64.msix` also compiled: 63,522,044 bytes, SHA256
`2b07c7b0df36b696efa317e56887d79a2626ea6cdf2cf37ca7c4107fed7c6e64`.
The compiler reported zero errors and one known symbols warning; symbols are
not produced in this delivery. Runtime publication binding, the necessary
approval gate and strict update-manifest validation all exited successfully.
No installer/MSIX installation or uninstall was executed. All nine assets are
publicly uploaded; the directly observed final playback is below.

## Publication record

[Beta15 release](https://github.com/airanluo-dot/DropSpace/releases/tag/v0.3.1-beta.15),
ID404561576, is a public prerelease (`draft=false`, `prerelease=true`) published
at2026-10-06T10:07:38Z with target App source
`734e0a96366fcffae4d3e8361745e4ba549ebf06`. The actual public API record in
`.codex/beta15/evidence/public-beta15-release.json` reports all nine assets in
`uploaded` state. Each name, byte count and SHA256 digest matches the final local
asset inventory. The parent verified that all nine Beta14 asset IDs, digests,
sizes and updated timestamps are unchanged.

The published payload consists of the three App packages, the reviewed CUDA ZIP,
`cuda-runtime-download.json`, `cuda-runtime-manifest.json`,
`runtime-publication.json`, `update-manifest.json` and `SHA256SUMS.txt`. The three
App package hashes are recorded above; the complete inventory is attached to
the public release.

The exact Beta15 CI route binds this public release ID, source commit and all
nine asset identities, compares the whole current App tree against the actual
build source with only six explicit CI/documentation exceptions, verifies the
downloaded bytes and runtime binding, then skips repeated compilation/tests.
Exact Beta15 cannot bypass that App-input comparison through maintenance-only
classification. The historical Beta14 block remains byte-for-byte unchanged.
CI verification records artifact identity; it does not claim a new functional
test or native model pass.

The tag-triggered Pages run37447693239 built/synced the website but deployment
was rejected by the existing `github-pages` environment protection: the Beta15
tag is not an allowed deployment ref. Protection was not relaxed. The parent
will dispatch deployment on main after this documentation/CI merge; website
publication is not yet claimed by this record.

## Actual final-source playback

These observations belong to PID9608, App source `734e0a96`, rather than the
first candidate. The source and final row records were compared by display
occurrence ID, stable ID, original identity, start/end timestamp, secondary text,
translation language and provenance. The comparison emits counts, not lyric text.

| Request / recording | Final source document | Observed final result |
| --- | --- | --- |
| `9608-2`, Justin Bieber `DAISIES`, `SWAG`, 176s, System/zh-CN | NetEase2724714488: 31 original, 29 native rows | All29 native translations retained exactly, zero AI rows; two credits excluded. |
| `9608-5`, `My City (feat. G Herbo)`, `FAST X (Original Motion Picture Soundtrack)`, 149s, System/zh-CN | NetEase2048760838: 48 original, 45 native rows | All45 native translations retained exactly plus one actual AI gap; two credits excluded, zero failed/pending rows. |
| `9608-8`, `风的来信 (feat. 孙晔) [中文版]`, Apple Music197s, System/zh-CN | NetEase3442230713: 52 original, no target translation | All38 sung rows remain original as same-language; all14 metadata rows are excluded. No AI is started for Chinese→Chinese. |
| `9608-9`, the same Chinese recording, temporary English target | Same NetEase3442230713 source: 38 ordinary plus14 metadata rows | All38 ordinary rows carry nonempty LocalAi/en-US text and `local-ai-complete`; zero failed/pending rows. All14 metadata rows remain excluded. Actual UI captured in `wind-final-cn-to-en.png`. |
| `9608-13`, `风的来信 (feat. Griffin Burns) [英文版]`, Apple Music197s, System/zh-CN | Chinese QQ candidate001gNFEp4gee1w was an identity probe only | Same-ID vocal proof identifies 孙晔/Gary rather than Griffin Burns; `candidate-rejected` records `featured-performer-not-confirmed`. No safe English lyric body was obtained in this attempt, so the source result and UI remain NotFound. The Chinese recording is not substituted. |

For the final actual translation, the parent observed production
`plain-lyrics-worker-cuda.exe` PID28200 completing the real 38-row App request
with `cpuFallback=False`. `final-cuda-gpu-proof.json` binds that worker to App
PID9608/source734e and records Windows GPUProcessMemory dedicated usage
2,629,894,144 bytes plus shared usage360,710,144 bytes. This combines successful
real output with the CUDA worker and GPU allocation; merely starting a process
or setting a CUDA label is not the success criterion. No general semantic,
full-song/hardware model qualification is granted by this small verification.

Final `.codex/beta15/evidence/final-island-animation.log` also records actual
empty-panel collapse with Resident enabled: file Dismissing revision3 at
09:54:14.3599566Z completes as file Hidden revision4 at09:54:14.9667424Z, while
the overlay stays Compact and both effective Logo values stay false. After
Resident is disabled, a second collapse completes file Hidden revision7 before
the saved two-second overlay deadline. At09:55:03.1193180Z the overlay enters
Dismissing; actual frame opacity0.4938/0.1653/0.0059 reaches settled0 at
09:55:03.7403517Z, then the native region is released and state becomes Hidden.
The file callback does not restart the timer or suppress the original island
animation. The final log separately shows the paused music dismissal rendering
to settled0 before region release.

The parent restored System language, Resident false and Music enabled, and
paused Apple Music at the end. User library, installed models/components and
normal caches were retained.

Complete mixed/short-row coverage across arbitrary songs, a final-source replay
of the first candidate's `her`/`falling` scenarios, held-drag beyond debounce,
click while hide-waiting, animation reversal and reduced-motion physical
observations, actual LRCLIB instrumental playback, installer execution and
complete install/upgrade lifecycle remain unverified. The English `风的来信`
recording has a real upstream/matching gap in this attempt; no promise of an
unavailable safe source body is made. A successful build or byte hash cannot
substitute for these behaviors.

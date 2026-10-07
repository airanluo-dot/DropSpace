# Round 3 — R3-LYR-01: completed no-match lost by provider failure aggregation

This is a newly confirmed source classification defect discovered while following the user's Music lyrics-card report. It supplements the third-round final diff stage; it does not recount a previous review finding or change the immutable 425-file / 85,220-line full-read baseline. The earlier localization request remains valid, but copy alone did not repair this source aggregation.

## Confirmed evidence

At immutable Round 3 source `f22bcc4bdd5c2d477a7880fe02c87f6035392ab9`, `Infrastructure/Lyrics/LyricsService.cs:432–442` reports a completed provider query as `NoMatch` and returns `Failed=false` after validation when no usable recording-matched body remains. This includes successful empty catalogues, candidate identity/score rejection and placeholder-only bodies. Provider exceptions, rejected/malformed responses and timeouts return failure instead; confirmed instrumental evidence remains usable/Found.

The query aggregator at original `LyricsService.cs:213–215` nevertheless marks every empty result Failed whenever `primary.Failed || fallbackFailed`. Fallback aggregation at original `:558` similarly ORs failures from independent providers without preserving any successful completion evidence. Thus a failing primary plus a successfully completed empty backup, or a completed empty primary plus an unrelated failing fallback, becomes `Failed` rather than `NotFound`. It can also retain `BodyQuality.RequestFailed` from the failing primary even though another search completed normally. The outer deadline/error returns at original `:218,220` unconditionally use Failed for an empty result, losing a preceding completed no-match when later fallback work times out.

The Infrastructure owner independently corroborated that NetEase empty/rejected candidate sets and LRCLIB valid empty/intentional no-match responses are ordinary completed results, distinct from malformed API responses and request failures. `FastTextLanguageIdentifier.PrepareAsync:55–57` returns directly for an empty/nonusable body; no native language worker is required to establish these no-match outcomes.

The VM mapping itself was correct: NotFound selects `LyricsNotFound`, Failed selects `LyricsFailed`, and the Music card displays that text. The defect is upstream loss of completed-search evidence. No request logs from the user's screenshot were supplied, so this report does not assert which provider failed or prove the exact network sequence for that song.

## Minimal correction

A query-owned atomic completed-provider count is retained with the existing `CandidateSearch` lifetime. Every primary, backup and parallel remaining-provider invocation receives its completion callback. `QueryProviderAsync` records completion only after the actual provider result finishes, both presentation/source token checks pass, and `Failed=false`; progress/preview callbacks, late results, cancelled requests, malformed/rejected responses and transport failures do not establish completed-search evidence.

A shared final-result builder now handles normal, internal deadline and recoverable-error exits. Usable/confirmed-instrumental evidence remains Found. With no usable evidence, at least one completed provider produces NotFound; zero completed providers produces Failed. Failed documents are marked RequestFailed; a NotFound result clears inherited RequestFailed to NoLyrics while retaining a meaningful placeholder-only classification. External user/song cancellation still propagates through the original catch filter and token checks.

Primary/fallback failure flags continue to govern `TranslationLookupIncomplete` and preferred-source cache retry policy. Existing provider diagnostics and request/slot/cancellation retirement remain in place. The source-search trace now records actual completed-provider count, final status and translation incompleteness. No provider failure was renamed or hidden.

The existing localized no-match values therefore apply to a real completed no-match search even when an independent provider failed: Chinese **无匹配歌词**, English **No matching lyrics**. All requested sources failing still use the unchanged refresh-and-retry wording.

## Focused regression source and validation plan

Added one relevant parameterized method using the existing provider harness:

`DropSpace.Infrastructure.Tests.LyricsRecoveryRegressionTests.CompletedNoMatchIsDistinctFromAllProviderFailures`

Its two DataRows exercise the real service with a failing primary and either a completed-empty backup or a failing backup. They assert NotFound/NoLyrics versus Failed/RequestFailed, empty rows, exactly two provider calls, retained translation-incomplete evidence, and unchanged per-provider TransportFailure/NoMatch diagnostics. The fake providers make no HTTP or native calls and reuse the existing three-second waiter budget. No other tests or helper fixture classes were added.

Exact filter: `FullyQualifiedName=DropSpace.Infrastructure.Tests.LyricsRecoveryRegressionTests.CompletedNoMatchIsDistinctFromAllProviderFailures`; project: `tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj`; actual selected expansion: **2 cases**. Root authorized the final producer plan of 8 Core + 2 Infrastructure + 1 isolated installer scenario = 11, below 21. The release owner binds separate raw TRX evidence; no execution occurred in this review phase.

## Static checks and limits

The modified callback signatures and every primary/backup/parallel fallback call were source-checked. The Infrastructure owner independently reviewed the complete production/test diff and corroborated tracker ownership, completion timing, body-quality consistency, preserved cancellation/slot ownership and both DataRow paths without running them. The final diff preserves existing translation/cache failure conditions, candidate selection and cancellation ownership. `git diff --check` reports no whitespace errors. **Executed cases: 0.** No local build, tests, fixture execution, app/native/GPU execution, commit or remote mutation. The two-case method does not execute every timeout, malformed body, placeholder or real player/provider path; those branches were checked statically. Final compilation and actual selected results remain pending producer evidence.

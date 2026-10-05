# Independent lyrics lifecycle audit

## Reviewed checkout and result

Cloud-only review on 2026-10-05, using Linux x64 and the repository-pinned .NET SDK 10.0.401.
The remote branch was fetched explicitly because this checkout originally fetched only `main`.
Initial source baseline was `c9611fee8b536216e4bbc276c5dc6c30c7af07d1`; it was then advanced
to the verified remote HEAD `983deab7aa3b80c81d5ef1d8b45b54cfb8b8f01e`. The intervening
commit changes only the native-crash handoff document, so the reviewed lyrics production
sources are identical across those baselines. Both `a9b550b` and `c168fc2` are included.

No new reproducible production defect was found in the requested lyrics boundaries.
This branch adds regression tests and this evidence record; it changes no production code,
translation model identities, frozen Hy prompts, default branch, or release surface.
AI identity implementation and model qualification remain with the primary task.

## Boundaries examined

- `LyricsService.CandidateSearch.Report` retains weak candidates in both AI modes but uses
  strict `Validate` results for previews and native fallback. A weak first report releases
  supplemental collection without publishing its unconfirmed original. A lone weak identity
  remains eligible for assisted selection.
- Source choice uses native target translation, then preferred/backup/fixed remaining-source
  rank, then usable word timing within a source. A fast lower source cannot truncate a
  higher source's pending request. Fresh and cached AI decisions cannot downgrade the trusted
  source under that public priority.
- A trusted source-cache hit reports its preview before starting fresh provider work in AI
  modes, then continues collecting the permitted sources. The cache does not stand in for
  a complete candidate snapshot. Foreign original-only results and transient preferred-source
  failures do not hide later native translations in a reusable source entry.
- Provider callbacks and completion after cancellation cannot add another preview, persist
  the cancelled original, or replace the current source cache. An uncooperative cancelled
  transport can still finish while its presentation request has retired.
- `LyricsCandidateSelector` checks cancellation, the owner predicate, and independent cache
  generation before accepting/caching inference. Cancellation, timeout, clear, and owner
  retirement were exercised with controlled late valid output. Success returned earlier
  also retains a publication fence, including reused decisions.
- Actual `AiLyricsService.SelectCandidateAsync` composes its retirement fence with the selector
  fence. Explicit invalidation, cache clear, model maintenance, and cancellation retire queued
  successes. Model/cache maintenance also retires the decision cache; cancelling publication
  after a valid success does not independently discard that exact snapshot's reusable decision.
- Matching provider translations bypass translation cache preflight, model resolution and
  inference, including disk-cache previews, old provider payload/cache paths and partial
  translations whose blank rows must remain blank. Original text and provider provenance
  survive. Same-target Chinese originals discard stale local-AI rows before backend access.
- `MediaExperienceService.LoadLyricsAsync` was read through the preview, selected-document,
  partial-translation and final-publication dispatcher paths. Each queued commit checks the
  media generation, current track, settings and parent cancellation after dispatch; selected
  and translated AI documents also check their own publication fences. This UI-level
  conclusion is source review, not execution of the WinUI dispatcher.

## Added reproducible regression coverage

`tests/DropSpace.Infrastructure.Tests/LyricsSelectionLifecycleRegressionTests.cs` adds four
methods / eight data-row cases:

1. Weak primary starts supplemental collection before completing, while no weak preview is
   published; a later trusted backup becomes the preview and native fallback in both AI modes.
2. Cancelled progressive old track reports late while a replacement succeeds; no late preview,
   cancelled-original cache entry or overwrite of the replacement cache is allowed, in both modes.
3. A valid inference answer released after clear, owner retirement or cancellation cannot become
   a success-cache entry; a fresh request must really infer again. Cancellation after a returned
   reusable success also invalidates its publication fence.
4. An uncooperative inference released after its deadline cannot turn the timed-out result into
   a reusable success.

`tests/DropSpace.App.Tests/AiLyricsSelectionLifecycleTests.cs` adds three methods / seven cases:

1. Selected and reused actual-service results recheck invalidation, clear, model maintenance
   and cancellation until UI commit.
2. Actual-service retirement while inference is paused rejects its later valid answer and
   prevents cache repopulation.
3. A new `LyricsCache`/`LyricsService` instance publishes its disk-cache translation before the
   fresh provider call, retains a weak supplemental candidate, preserves original/secondary
   text, and performs zero translation cache, resolver or backend calls in both AI modes.

The tests use barriers and deliberately uncooperative fixture work. Their only artificial delay
is the selector-timeout case. They are host/lifecycle evidence, not model semantic-quality evidence.

## Checks actually executed

The following five batches cover **46 distinct final passing cases**, including 15 newly added
cases. Data rows count as separate cases; this is not the previous task's check count and is
not a full test-suite claim.

| Batch | Cases | Final result |
| --- | ---: | --- |
| Existing candidate collection, pipeline priority, retirement and performer-evidence tests | 7 | Passed |
| Added Infrastructure lifecycle tests | 8 | Passed |
| Existing actual-App-service admission/progress boundaries linked into a portable test assembly | 15 | Passed |
| Added actual-App-service selector/cache lifecycle tests in that assembly | 7 | Passed |
| Selected source-priority, persistent-cache and translation-coordinator semantics | 9 | Passed |

The first new Infrastructure run passed six cases and failed two at a fixture precondition:
the assertion called `CandidateScore` without `CollectSelectionCandidates = true`. The fixture
was corrected to exercise actual weak admission, and all eight cases passed. The first compilation
of the new App tests referenced a nonexistent cache path property; the fixture now retains its
actual directory explicitly and the seven cases passed. Neither failure was a production bug.
After strengthening the cancelled-original cache and preview-before-provider assertions, only
the four changed data-row cases were rerun; all four passed. They are not counted twice above.

Infrastructure reproduction:

```sh
dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj \
  --filter 'FullyQualifiedName~LyricsSelectionLifecycleRegressionTests'
```

The existing selection batch filter was:

```text
FullyQualifiedName~LyricsCandidateCollectionTests|FullyQualifiedName~LyricsSelectionPipelineRegressionTests|FullyQualifiedName~LyricsSelectionRetirementRaceTests|FullyQualifiedName~LyricsSelectionIdentityEvidenceTests
```

The nine source/cache/translation cases were selected individually:

```text
ConfiguredBackupTranslationPrecedesFasterRemainingTranslation
PreferredTranslationWinsAfterSupplementalTranslationArrivesFirst
OriginalOnlyTargetAwareResultDoesNotHideLaterNativeTranslation
TransientPreferredFailureDoesNotPersistLowerPriorityTranslation
RepeatPlaybackAndRestartUsePersistentSourceWithoutProviderSearch
ProviderFinishingAfterClearCannotRepopulateDisk
CacheHitDoesNotRunInferenceAndLanguageHasSeparateKey
CancelledResultCannotBeCachedOrReturned
PartialMatchingProviderSongIsKeptWithoutAiGapFilling
```

The portable App harness compiles the **actual unmodified** `AiLyricsService.cs` and links the
existing `AiLyricsAdmissionRegressionTests.cs`, `AiLyricsProgressRegressionTests.cs`, and added
`AiLyricsSelectionLifecycleTests.cs`, with a project reference to Infrastructure. It uses
`Microsoft.NET.Test.Sdk` 18.8.1 and MSTest 4.3.3, matching repository package versions.
There are no App-service stubs. Existing App cases were individually filtered to provider/old-cache
bypass (four rows), Chinese/stale-AI bypass (two rows), queued partial retirement (five rows),
external song fence (one), final maintenance fence (two), and stale provider bypass (one).

The linked project and all TRX records, including the initial fixture failure, are retained in
the cloud evidence bundle `lyrics-independent-audit-evidence.zip`. The bundle also includes a
manifest of source/test hashes, checkout SHA and SDK version. No full WinUI build, Windows native
test, real provider traffic, GPU/16-GB qualification or model diagnostic was executed here.

## Remaining limits and handoff

The selector and resident helper still have the documented 500-ms caps. A separate 12-second
snapshot ceiling does not make real selection run for 12 seconds. This existing profile limitation
is neither repaired nor used to block this independent lifecycle audit.

The previously documented asynchronous parent-to-child cancellation interval remains: an old
track may memoize a successful decision under its exact old snapshot before child cancellation
arrives. The media parent fence prevents that old decision from committing to the new track UI.
This audit does not claim to eliminate that interval or prove every concurrent interleaving.

Cherry-pick the audit commit to integrate the two new test files and this record. The primary
implementation branch was read/fetched only, and no default-branch write or release was performed.

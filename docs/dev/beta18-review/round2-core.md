# Beta 18 round 2 — Core full-source review

Reviewed immutable source commit `729d5b179f34ab2f852e7975f129431f77e8dc7a` from `/workspace/scratch/beta18-round2`. This is a fresh second-round review after the round-one fixes and PRs 107–109, including the Island appearance/priority work. No round-one review result was used as evidence. One new source-backed correctness finding is recorded below.

## Scope and actual coverage

The exact partition is [round2-core-scope.txt](round2-core-scope.txt): **117 files, 14,803 lines**, including all Core abstractions, models, policies, algorithms, project/lock files, and the OpenCC data/license/readme. Every listed file was physically read in full. Full bodies were read in bounded chunks, including unchanged code; searches and targeted caller reads supplemented that coverage. Truncated portions of large read outputs were reread in smaller chunks before marking their files complete.

The 5,062-line `Lyrics/Data/TSCharacters.txt` dictionary was read in five contiguous chunks: 1–1000, 1001–2000, 2001–3000, 3001–4000, and 4001–5062. Its paired license and attribution/readme were also read. Reading the dictionary establishes actual textual coverage, not independent linguistic validation of every mapping.

| Core area | Files fully read | Lines |
| --- | ---: | ---: |
| Abstractions | 11 | 431 |
| Actions | 4 | 353 |
| Collections | 2 | 300 |
| Compatibility | 1 | 95 |
| Content | 2 | 126 |
| Diagnostics | 1 | 6 |
| Displays | 1 | 38 |
| Downloads | 1 | 60 |
| DragDrop | 4 | 481 |
| Project and dependency lock | 2 | 16 |
| Island | 5 | 269 |
| Lyrics, including embedded data | 26 | 7,530 |
| Media | 7 | 351 |
| Models and settings | 11 | 1,101 |
| Overlay | 10 | 1,326 |
| Policies | 8 | 484 |
| Preview | 1 | 165 |
| Shell | 1 | 139 |
| SystemActivities | 1 | 11 |
| Transfer | 7 | 966 |
| Undo | 1 | 16 |
| Updates | 6 | 291 |
| Widgets | 4 | 248 |
| **Total** | **117** | **14,803** |

Source identity was checked against [round2-source-manifest.json](round2-source-manifest.json). All 117 Core file hashes matched the manifest. Counts include blank lines and use physical `splitlines()` on each source file.

- Scope SHA-256: `6acf3f73214fcb2a22273a9b7371af1b20808291a233663c6f38a1722d140f78`.
- Ordered Core source SHA-256: `c8a50477c6409f33b3bd2aad26afa0808ec2680dfeb82e1a4bd0c047770d1bc4`. The stream is, in scope-file order, UTF-8 relative path, NUL, raw file bytes, NUL.
- Source manifest SHA-256: `f3be1f8a232db46d8eff7d557661d3316a3da658948f4520703abe15ff137ac8`.
- OpenCC dictionary SHA-256: `9ff46a7d30e5765375eb13d33f2b03a34d298913caf2b120380679f33ae1642d`, matching its embedded readme.

## New finding

### R2-CORE-01 — P2 — Mixed provider languages count as complete target coverage

**Location:** `src/DropSpace.Core/Lyrics/LyricsTranslationPolicy.cs:60–62`.

`HasCompleteTargetCoverage` first establishes that the document contains a matching target-language provider translation, then requires only a non-null provider translation on every sung row. It does not require those row translations to match the requested target language. A usable non-target original with one explicitly tagged Chinese translation and another explicitly tagged English translation therefore counts as complete Chinese coverage. The English row's secondary text is hidden in Chinese mode by `LyricsDisplayPolicy.Secondary` at lines 61–64, so the metric disagrees with what the user can see.

This representation reaches production through AMLL: `Infrastructure/Lyrics/AmllLyricsProvider.cs:78–88` parses downloaded TTML and prepares its language admission. `LyricsParser.cs:232–238` preserves each paragraph's explicit translation language independently. `LyricsLanguagePolicy.cs:85–91` trusts a content-consistent target-language subset, while `Prepare` at lines 167–171 retains the other rows' explicit languages. The Infrastructure reviewer independently confirmed this caller/parser path.

The incorrect completeness result is stored as `LyricsSelectionCandidate.TargetSatisfied` by `LyricsCandidateRules.Describe` at lines 71–75 and outranks source order in `ComparePriority` at lines 52–56. An incomplete mixed-language document can consequently tie a fully target-translated document on completeness, then win by preferred source. Shipping `LyricsService.cs:334`, `501`, and `562–570` also uses the metric to signal preferred completion, cancel supplemental work, or start the fallback quality window. These are source-established consequences; no runtime reproduction was executed.

**Minimal fix:** retain the current `OriginalTarget` shortcut, but make the provider branch require every sung row's preserved provider payload to match the requested target language, using the shared language equivalence rules and the adapter's existing inferred language tags. A mismatching explicit language must not count toward complete coverage. Preserve `HasMatchingProviderTranslation` and the intended rule that any valid partial native target translation closes the AI/native lookup gate. This finding concerns completeness ranking and completion signals, not a request to relax that gate.

**Disposition:** implemented in the working tree after completing this immutable-source review. The provider completeness predicate now requires each sung row's preserved provider payload to satisfy `LyricsLanguagePolicy.SameSourceLanguage` for the requested target. The `OriginalTarget` shortcut, partial-native lookup/AI admission gate, language inference, and display policy are unchanged. The complete edited policy was reread; no test, probe, fixture, or build was run. The planned third-round review must inspect the resulting source afresh.

## Requested behavior and remaining Core review

The new Island defaults and migration are consistent with the requested behavior in source. Fresh `AppSettings.Theme` and `IslandAppearanceSettings.Theme` default to `System`. `IslandAppearanceMigration.Apply` examines the raw JSON case-insensitively: an existing appearance `Theme` field preserves the explicit Island choice; a missing field seeds it from the valid main-window theme, falling back to `System`. The production `JsonSettingsService.LoadAsync` calls the helper before validation and saves when migration changes the record. `LoadRawAsync` also applies it. Persisting the field makes subsequent loads preserve the migrated choice. This was checked by source inspection, without executing migration fixtures.

The Island content policy chooses Music while media is playing under Music priority, chooses files under Temporary Space priority, and falls back to files when media is paused and files exist. The experience coordinator retains `_pageSelected` across passive media identity/playing updates. It releases the override when the selected content disappears or the user explicitly changes content priority. Hide generations, stale dismissal guards, notification/volume expiry, fullscreen allowance, and manual-open precedence were read together with the complete presence and geometry policies.

The ASCII search fast path was compared in source with the complete `SearchNormalizer` implementation. ASCII has no decomposition or combining-mark work; both paths use the same ASCII whitespace set and invariant A–Z lowercasing. Delaying a collapsed whitespace until the next non-whitespace character preserves full normalization's trim before taking its exact prefix, including a prefix ending at a collapsed separator. The fast path checks the entire input for non-ASCII before selecting this route, allocates/cases only the retained prefix, and leaves Unicode normalization and prefix behavior on the existing fallback. No runtime parity probe was performed.

The remaining full-file pass examined serial projection refresh ownership and cancellation; drag evidence, session lifetime and bounded signals; action selection; lyric identity, parser alignment, protocol/output bounds, admission/cache revisions, circuit lifetime, display/timing/glow; media clock, recovery and spectrum freshness; settings normalization/merging/migrations; overlay motion interruption, placement, frame pacing and region signatures; clipboard serialization/loop prevention/queue bounds; payload containment, retention and redaction; transfer manifest/chunk limits; release parsing/selection and installer arguments; widgets, previews, shell intake and service contracts. No additional evidence-backed finding was established in this partition.

Two possible concerns were checked against callers and excluded: native region-signature caching is reset by `App/Services/OverlayNativeRegionController` on apply failure, and transfer trailing-dot/space alias names did not establish a meaningful shipping trigger beyond recoverable staging failure. The transfer receive path uses `File.Move(..., overwrite: false)` and retained staging cleanup; the Infrastructure reviewer confirmed that Windows-authored sender names also restrict this case. No transfer policy change is proposed from that speculation.

## Checks and limits

Checks actually performed were full textual source/data reads, source/caller tracing, physical file/line counts, SHA-256 checks, and comparison with the immutable source manifest. Focused caller reads outside this partition were used only to validate conclusions and are not included in the 117-file coverage count. After the review, the authorized minimal production fix above was applied and its full policy body was reread. No tests, probes, fixtures, builds, native/model executions, or remote operations were performed. This static review does not certify Windows runtime scheduling, native graphics/media behavior, model accuracy, or resource usage under execution. The fixed source is ready for the planned fresh full-source third round.

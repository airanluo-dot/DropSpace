# Beta 5 correctness review

Baseline: `60a4ed640e33a04653eb3979d2a5d1582c92d9b7` (published beta4).

## Scope and method

The repository inventory covers the App/Core/Infrastructure sources, native helper,
website, sharing worker and release scripts. Manual tracing concentrated on media and
lyrics, AI admission/cancellation, settings recovery, clipboard/undo/projection, database
pagination and payload cleanup, previews, update ownership, widget/notification lifetimes,
and island frame ownership. Compilation/analyzers and existing suites cover additional paths.
This is not a claim that every line received independent manual review, every Windows
interaction was executed, or all possible bugs have been found. No code was changed merely
because a pattern looked suspicious; the fixes below have failing regression reproductions.

## Confirmed corrections

1. `Lyrics:null` was dereferenced before normalization during settings migration.
2. LRCLIB accepted only its highest-scoring row before checking its identifier; an invalid
   first ID hid valid rows. AMLL and Kugou stopped at a failed first lyric candidate.
3. Candidate recovery omitted XML/format parse failures, abandoning later valid recordings.
4. A pause with an unchanged native observation rewound the clock (13 seconds back to 10).
5. AI maintenance executed cancellation callbacks synchronously on its caller while holding
   the owner lock. A blocked callback delayed returning the maintenance Task. Async requests
   now retain callback settlement and active-work ownership before maintenance proceeds.
6. A 159-code-unit title cut could split an emoji; first-line extraction also copied an
   arbitrarily large remainder unnecessarily. Span slicing fixes both without changing storage.
7. Website feature copy and two old source-contract tests still required obsolete Beta labels.

## Checks before Windows CI

- Final Core run: 620 passed.
- Final Infrastructure run: 707 passed, 26 skipped, 7 Windows DPAPI platform failures on Linux.
- Focused provider/recovery: 62 passed. AI lifetime/resident/backend: 65 passed.
- Website build/contracts: 37 passed. Sharing worker: 23 passed (unchanged source).
- UI-label contracts: 10 passed. Private helper protocol: 55 passed.
- Native GPU admission policy fixture compiled with warnings-as-errors and passed; this is
  not a GPU performance measurement or full native engine build.
- Initial repository script sweep: 484 total, 469 passed. Thirteen needed unavailable pwsh;
  the other two were obsolete Beta assertions, now corrected and rechecked.

Final exact-head Windows CI and publication results must be consulted separately. Do not
interpret these local results as completed installer, WinUI, DPAPI or real-device validation.

# NetEase provider translation-loss investigation

User examples: Episode 33, Overdose, Horizon Dreamer. The user observes that the NetEase app displays Chinese translations while DropSpace frequently displays only originals and unnecessarily invokes local AI. Exact catalogue IDs and successful live payload captures for these examples are not yet available. Do not equate the synthetic reproductions below with proof of every reported song's root cause.

## Confirmed defects and changes

1. The provider prefers YRC word timing, then tries ytlrc or tlyric against that timeline. Original and translation rows more than 250 ms apart are deliberately not paired. When an independently correct LRC/tlyric pair existed, the provider previously discarded it unless the YRC originals were entirely empty. Two transport regressions failed on the prior implementation (missing ytlrc, and nonempty but unusable ytlrc), both returning null translations despite valid LRC/tlyric.
2. When YRC retains no provider translation, the provider now tries the same catalogue entry's independently paired LRC/tlyric. It does not relax timestamp tolerance, invent cross-source timing, or implement the cancelled asynchronous dual-timeline feature. A valid YRC/ytlrc pair stays intact, including word timing and intentionally partial translations.
3. A previously cached original-only NetEase document can otherwise keep hiding the recovered translations after updating the parser. A persisted provider-data revision triggers one fresh read for those legacy records. Successful current reads are stamped at the cache boundary; genuinely untranslated tracks remain cacheable. Valid old translations and other providers keep their existing reuse behavior.
4. An App service regression passes the recovered real provider-payload schema through lyric loading and verifies zero AI cache/resolver/inference/progress calls when the target-language source translation exists.

## Verification

- Red baseline: both new timing transport cases failed with expected Chinese translation versus actual null.
- Focused transport suite: 20 passed, including preservation of valid word-timed pairs and one-time cache migration with/without actual translations.
- Core: 522 passed.
- Parent actual-service/raster harness: 116 passed (92 service/admission + 24 raster).
- Node static checks: 361 passed.
- Full Infrastructure on Linux: 627 passed, 8 failed, 26 skipped. The retained failures are the existing Windows DPAPI cases and receive-route permission case; they are not successful validation. During implementation, six cache-reuse regressions failed and were repaired by stamping successful fresh reads at the cache boundary; the full rerun returned to the eight environment failures.
- New exact-commit Windows CI remains required. The previous b9ade0c visual candidate does not validate these changes.

## Investigation still open

- Match exact versions/catalogue IDs for the user's three examples and inspect actual provider response fields and timing.
- Check whether additional payload variants, matching or language/display filtering contribute to the high reported failure rate.
- Verify source translations survive persistent-cache restart and displayed target-language settings in the Windows app.
- Do not use these code-level tests as evidence of real-model semantic quality or blanket release approval.

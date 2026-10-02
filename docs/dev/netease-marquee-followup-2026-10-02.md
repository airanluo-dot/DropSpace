# NetEase intact-pair and compact marquee follow-up

Parent of this candidate: `badbdf8e461d6ba1c4bb59fe675ff80cccb913af`.
No release, model prompt change, asynchronous lyric alignment, or user-device action.

## Reproduced and repaired

- Cross-pairing YRC with `tlyric` could retain one coincident sung row or credit,
  hide the other LRC translations, and prevent the previous zero-translation fallback.
- With no YRC, a stale `ytlrc` could override a complete valid LRC/`tlyric` pair.
- A YRC translation containing only credits could block the complete sung LRC pair.
- Old partially translated NetEase caches were reused after the first repair. All older
  NetEase data revisions now refetch once; newly genuinely untranslated entries remain
  cacheable, and unrelated providers are unchanged.
- Official cloud search returned HTTP/API success with `result` as an opaque string.
  That shape now reports provider failure rather than a definitive catalog miss.
- Compact word-timed originals jumped to the tail when word highlighting was disabled.
- Compact LRC scrolling ignored configured lyric delay, unlike timeline selection.

Provider selection now parses only YRC/`ytlrc` and LRC/`tlyric` as intact authored pairs.
A genuine partial sung YRC translation retains priority. Credit-only YRC does not suppress
sung LRC translations. No tolerance expansion, cross-provider merging, or independent
translation timeline was introduced.

The original marquee uses a Core policy with session-relative playback time, the same
clamped lyric delay, and a word-follow branch only when word highlighting is enabled.
The line-based scroll remains bounded by measured overflow. Translation scrolling is unchanged.

## Actual-song check and limits

The official Episode 33 lyric payload for song ID `1311427648` (She Her Her Hers) contains
all four provider fields. Both the previous candidate and repaired candidate preserve all
14 sung translations once the correct ID is reached; the repaired candidate keeps word
timing. This does not reproduce the user's entire live matching/cache failure.
The cloud search response could not supply a usable catalog, and the public Horizon
album page requested login. Horizon Dreamer and ambiguous-title Overdose are not claimed
verified. No account login or access-control bypass was attempted.

Separate remaining policy limits: first nonempty original candidate wins even if an
equal-score candidate has translations; all-short/all-mixed unclassified translations can
be displayed but still reach AI admission. These are not silently reclassified or bundled
as verified causes of the user's three songs. Chinese `tlyric` provenance is not explicit
in the captured payload; it must not be assumed for every response.

## Verification

- Core: 527 passed, zero failures/skips.
- Provider transport: 27 passed, zero failures/skips, including pair preservation,
  genuine partial YRC, old revision 0/1 cache refresh, and opaque catalog failure.
- Linked App services/raster harness: 116 passed, zero failures/skips. This is Linux
  service/render logic evidence, not physical Windows playback or glyph-layout acceptance.
- Node static tests: 361 passed, zero failures/skips.
- Independent source-linked pairing probes: all four old failures recover both sung
  translations; genuine Episode 33 still retains 14 translated sung rows.
- Full Infrastructure on Linux: 634 passed, 8 failed, 26 skipped. The retained failures
  are seven Windows DPAPI platform failures and the existing receive-route fixture
  returning HTTP 400 instead of 200 (previously associated with the read-only destination);
  they are not counted as passes. Exact Windows revalidation remains required.
- Exact-candidate Windows CI and full release validation are required after push.

The optional 7B model still lacks genuine Windows baseline/AVX2 capture and semantic
approval. No tests or successful model loading can substitute for that evidence.

# Beta 26 lyrics audit — 2026-09-21

Scope: Core lyrics parser/matcher and Infrastructure HTTP/providers/registry/service/cache. Media session coordination and native UI evidence are recorded separately. This report describes locally verified source changes, not publication.

## Confirmed defects and fixes

| Severity | Root cause | Fix / evidence |
| --- | --- | --- |
| S1 | LRCLIB `/api/get` receives duration `0` for incomplete SMTC metadata and returns HTTP 400; Beta 25 only recovered from 404, so usable search lyrics were never attempted. | Use search when exact metadata is incomplete; 400/404 and rejected exact identities continue to search. Real baseline 400 became HTTP 200/search and 42 matched lines. |
| S1 | Kugou song-search `Duration` is seconds (real response 269), but code divided it by 1,000. Any reliable 269-second playback duration rejected the correct candidate. | Keep song-search seconds; lyric-search duration remains milliseconds. Fixture and real 269-second query both pass. |
| S1 | Kugou lyric candidates omit album even after the song-search hash has established the correct album; strict validation then rejected any SMTC query with an album. | Carry the independently validated song album only into the corresponding hash lookup; candidate title/artist/duration still pass the same matcher. Real 269-second `晴天 / 周杰伦 / 叶惠美` returns 63 lines with album identity. |
| S2 | LRCLIB exact responses missing metadata could borrow the requested metadata and manufacture a successful identity. A mismatching exact result also prevented search; an empty top search result masked usable later candidates. | Validate only returned metadata, search after mismatch, and exclude candidates without lyric content before ranking. Dedicated fixtures cover each case. |
| S2 | NetEase searched title alone with a ten-result bound, so popular covers could crowd out the requested artist. | Include requested artist in the bounded search. Identity thresholds and version/album/duration rejection stay unchanged. NetEase returns the original multi-artist `Color Your Night` (62 lines). `晴天` still has no valid original in its returned catalog and correctly falls back. |
| S2 | Parser replaced declared YRC/TTML ends with the next line start, highlighting previous lyrics through instrumental gaps. | Preserve declared ends and infer next timestamps only for LRC without explicit ends; YRC/TTML regression asserts no line at second 5 between a 1–2-second line and a 10-second line. |

Transport hardening: production deliberately sets `AllowAutoRedirect=false`. `LyricsHttpClient` now handles at most three same-origin HTTPS redirects inside its existing total request deadline, preserves required headers, rejects other hosts/downgrades/custom ports/user-info before sending, and disposes intermediate responses. **No redirect was returned by the real endpoints in this smoke; redirects are not claimed as the observed root cause.** Deterministic fixtures verify JSONP and redirect behavior.

## Real network and service evidence

Executed on this Windows host at 2026-09-21 04:52 UTC. Harness references the actual Infrastructure project and instantiates the production configuration: `HttpClientHandler.AllowAutoRedirect=false`, infinite outer HttpClient timeout, `LyricsHttpClient`, complete `LyricsProviderRegistry`, and `LyricsService` with default NetEase preference. No mock provider or synthetic online lyric payload was used. Only metadata, status and counts were retained; full lyrics are not logged.

Raw local evidence: `artifacts/beta26/lyrics-smoke/baseline.txt`, `fixed-initial.txt`, `final.txt`; harness `Program.cs` and `Smoke.csproj` in that directory. Command: `dotnet run --project artifacts/beta26/lyrics-smoke/Smoke.csproj --no-restore`.

| Provider | `晴天 / 周杰伦`, duration 0 | Same track, `叶惠美`, 269 seconds | `Color Your Night / Lotus Juice`, 227.24 seconds |
| --- | --- | --- | --- |
| NetEase | HTTP 200, no valid original; correct empty | HTTP 200, correct empty | HTTP 200 search + lyric; 62 lines; ID 2123807718 |
| QQ Music | HTTP 200 search + lyric; 63 lines | 63 lines, album and duration validated | 66 lines; ID 003qDqNY23O7Zx |
| Kugou | HTTP 200 keyword/song/hash/download; 63 lines | 63 lines, album and 269.792-second duration validated | 66 lines; ID 310194962 |
| LRCLIB | HTTP 200 search; 42 lines (baseline was 400 exact) | HTTP 200 exact; 42 lines; ID 17788 | 58 lines; ID 9873162 |
| AMLL | HTTP 200 search + get; 62 lines | 62 lines, album validated | 67 lines; ID 1191242464338774 |

All five hosts were reachable using the real production transport. JSON, QQ JSONP-capable decoding, Kugou Base64, and AMLL TTML returned usable documents. No DNS, TLS, proxy, or endpoint outage was observed for these requests. Some AMLL/LRCLIB requests took approximately 5.5–5.9 seconds; provider deadlines and cancellation remain enabled.

Full service (rather than provider-only) results: unknown-duration Chinese track → `Found/Kugou`, known-duration Chinese track → `Found/QQ Music`, multi-artist track → `Found/NetEase`; each retained its distinct supplied track identity. This validates live default-provider miss → alternative-provider success with production score validation and cancellation/draining. Each provider remains subject to catalog gaps; no matching thresholds were relaxed.

Local mode loaded a real temporary LRC file with matching title/artist, returned two lines, and issued **zero** network requests. The temporary fixture was removed after the run.

## Regression results and audit boundaries

- `dotnet test tests/DropSpace.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~Lyrics'`: **18 passed, 0 failed, 0 skipped**. Covers transport redirects and JSONP, LRCLIB identity/search recovery, Kugou units/album, NetEase artist query, failed-result recovery without caching, local-only mode, fallback isolation, stale identities, cancellation/draining and clear-cache versus in-flight completion.
- `dotnet test tests/DropSpace.Core.Tests --no-restore --filter 'FullyQualifiedName~Lyrics'`: **17 passed, 0 failed, 0 skipped**. Includes new explicit-gap expiry plus existing title/artist/album/duration/version rejection, parser bounds, TTML and display policy.
- Bug-family review covered every online provider's duration units, metadata provenance, candidate identity, response ownership, fallback failure isolation, cache invalidation and local-file handling. Known album conflicts and unreliable title-only queries remain rejected.
- `NOT VERIFIED —` real Apple Music/NetEase/QQ playback → native UI lyrics/artwork/highlight and physical next/previous/rapid-track controls: this host had no active music player; native SMTC probe found only a Bilibili video with empty artist/album and zero duration. Provider/service replay is not a substitute for that acceptance test.
- `NOT VERIFIED —` every catalog song, geographic network path, upstream future schema or redirect: online providers are external dependencies; this report records the precise live samples above.

Skill synchronization is owned by the coordinating release audit. Required reference updates: incomplete metadata uses LRCLIB search; Kugou units and hash-bound album provenance; bounded same-origin lyrics redirects; explicit timed-line ends and this reproducible network evidence boundary.

## Live-player follow-up: translation alignment

The coordinator subsequently opened a real NetEase player and observed opening
credits with a chorus translation for `Sunroof / Nicky Youre`. A fresh response
for candidate **1893514633** at 05:14 UTC confirmed two zero-duration YRC credit
lines at 0 ms followed by a sung line at 0–3630 ms. The first YTLRC translation
also starts at 0 ms. The parser independently selected the nearest translation
for every original, reusing that sung translation on both credit rows. The LRC
variant has credits at 0/131 ms and the sung/translated line at 262 ms, exposing
the same reuse within the 250 ms matching tolerance. This is a confirmed **S2**
parser alignment defect, not a provider outage or an unrelated song identity.

External translations now bind once to the closest original; equal-time original
rows resolve to the last row, matching NetEase's credits-before-singing order.
Closer matches take precedence over a nearby competing translation. Original
credit rows remain available without a fabricated translation. Two regression
fixtures model both actual timestamp patterns without copying song lyrics.

`dotnet test tests/DropSpace.Core.Tests --no-restore --filter
FullyQualifiedName~Lyrics`: **19 passed, 0 failed, 0 skipped**. A fresh production
NetEase provider query (`Sunroof`, `Nicky Youre`, empty album, zero duration)
returned 46 rows; the first two credits had no secondary text and the sung rows
at 0/3690/7380/11340 ms retained their translations. Evidence (timestamps,
categories and booleans only): `artifacts/beta26/sunroof-alignment/results.txt`.
The native-island Skill reference and installed mirror include this invariant.

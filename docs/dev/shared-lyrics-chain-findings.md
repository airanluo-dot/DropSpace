# Shared Beta9/Beta10 lyric chain: current findings

The user's later comparison no longer attributes the failures specifically to
Beta10. Treat that earlier regression hypothesis as superseded. The three
source-backed fixes in `b6ae1b8` remain relevant defects, but are not proof of the
reported real-song failure. No laptop or live lyric API requests were used.

## Evidence boundaries and status

| Finding | Evidence | Status |
| --- | --- | --- |
| Beta9 shared rejection can trigger another same-URL request per waiter | Completion signal has no response; unsuccessful payload is not stored and waiters loop | Already repaired in released Beta10; not a new Beta11 cause claim |
| Complete no-target/same-target original gets only one-second HTTP reuse | PR88 P2 discussion and released `ReuseLifetime`; test on exact Beta10 main produces `2,2,2` versus expected `1,1,2` | Isolated fix `2398e18`, draft PR89; same test turns green, no unrelated changes |
| Original cache identity migration misses usable v4 originals | Exact old keys preserved on disk but not read by Beta10 | Narrow repair in `b6ae1b8`; fake-cache checks passed |
| Observed A/B/A cancellation can disappear after coalescing | Observer cancels while coordinator compares identical final snapshots; retirement notification does not set reload intent | Reload counter repair in `b6ae1b8`; native App path remains unexecuted here |
| Apple Music plain artist credit cannot match verified bilingual/prefixed artist credit | Direct execution of Core scoring and official artist identities | Narrow whole-credit alias repair; one Core and one synthetic NetEase pipeline check passed |
| Two never-retiring provider calls block subsequent songs from that provider | Actual service + controlled fake provider, all songs have distinct identities | Reproduced condition, NOT an established live transport defect; admission diagnostics distinguish it without relinquishing live ownership |
| UI Failed label alone cannot distinguish rejection, malformed response, timeout or blocked admission | Aggregate source failure logic plus localization mapping | Admission diagnostic added; no assertion that every failure is API405 |
| Three-failure circuit pauses translation across songs | `LyricsInferenceCircuit` and `AiLyricsService.TryBegin` | AI fallback only; source/native translations bypass this circuit and preserve originals |
| Thirty-minute global native-provider switch | Timer/counter/cache/gate read-through | None found; this does not exclude upstream behaviour or an unobserved stuck invocation |

## Apple Music 《唯一》: observed versus inferred metadata

The supplied screenshot shows player Apple Music, title `唯一`, artist `邓紫棋`,
album `T.I.M.E. - EP`, elapsed 0:17, and the `LyricsFailed` string. The remaining
clock is obscured, version is not displayed, album artist and exact duration are
not known. The query must not invent a native duration from the screenshot.

Apple's public playlist lists this song, performer and album, with catalogue
length 4:13:
[Apple Music](https://music.apple.com/cn/playlist/%E9%82%93%E7%B4%AB%E6%A3%8B-%E6%83%85%E6%AD%8C%E7%B2%BE%E9%80%89/pl.625750a7c4bb48a9b0934792ee8592b3).
That supports the release identity, not the actual SMTC duration.
The artist's own [simplified release page](https://cn.iamgem.com/time/) lists
`唯一` in `T.I.M.E.` and identifies the artist as G.E.M. 邓紫棋; its
[traditional version](https://www.iamgem.com/time/) identifies G.E.M. 鄧紫棋.
The official QQ share link could not be retrieved by the public-page tool; no
live QQ/NetEase search response is claimed.

An offline executable uses the actual Core assembly with the screenshot's title,
artist and album and deliberately unknown query duration. Before the alias fix:

| Hypothetical catalogue credit | Score |
| --- | --- |
| 邓紫棋 | 10.6666666667 |
| G.E.M.邓紫棋 / G.E.M. 邓紫棋 | 0 |
| 鄧紫棋 / G.E.M.鄧紫棋 | 0 |
| G.E.M. alone | 0 |
| 告五人 | 0 |

Providing an additional matching `AlbumArtist` would rescue the full credit, but
that field was not visible and cannot be assumed. `T.I.M.E. - EP` versus
`T.I.M.E.` already has compatible album evidence; a differing album alone still
scores 10 when the artist is strong. The album suffix is not the hard rejection.
`唯一` versus `惟一`, actual Live versions and incompatible known durations remain
separate safeguards; changing artist identity must not remove them.

The repair maps only normalized, complete credits `邓紫棋`, `鄧紫棋`,
`G.E.M.邓紫棋`, `G.E.M.鄧紫棋` to the verified identity. It does not remove arbitrary
Latin prefixes, match artist substrings, infer every simplified/traditional name,
or accept ambiguous bare `G.E.M.`. Original provider metadata is retained in the
bound document. The same central matcher is used by provider candidate selection,
service validation and sibling media-session compatibility.

Two focused checks passed, with no suite repetition:

* Whole-credit aliases accept the same title/release; wrong artist, extra-name
  suffix, AC versus AC/DC, Live version and known wrong duration still reject.
* A synthetic NetEase catalogue with the screenshot metadata and canonical full
  artist reaches its lyric payload and returns synthetic Chinese original text
  using exactly one search + one lyric call. This is NOT the real 《唯一》 payload.

## Half an hour working, then several songs without translations

This report does not establish an exact timeout, a song-count threshold or that
all those recordings must have usable native translations. The shared-state
review covers the following paths:

1. `LyricsService` owns one semaphore of two actual invocations per provider.
   Admission/provider deadlines are eight seconds; the outer request is bounded
   to 24 seconds and translation selection gets one three-second budget after
   the first validated original. None is a half-hour timer.
2. Cancellation detaches presentation promptly but actual invocations retain
   their slots through provider exit and cancellation callback cleanup. The
   release is in `InvokeProviderAsync`'s finally, including rejected, malformed
   and cancelled provider results. There is no normal rejection path that skips
   this finally. There is no global native rejection/failure counter or circuit.
3. A transport that truly ignores cancellation can nevertheless retain a slot
   indefinitely. Two such invocations from different songs block all later
   admission to the same provider. Clear-response-cache and soft media restart
   do not recreate this service/gate or forcibly discard retained ownership.
   A process restart creates new owners, but no repeat user experiment is asked
   for here. Force-releasing still-running invocations would break the resource
   bound and is not a justified repair without a demonstrated stuck transport.
4. The controlled executable supplies a 40 ms provider deadline via the existing
   internal constructor, then uses two intentionally noncooperative fake calls.
   A/B finish presentation as Failed with available slots 1/0. C is Failed with
   call count still 2 and slots still 0: there was no new provider/HTTP invocation.
   Diagnostics are Query/Timeout, Query/Timeout, Admission/Timeout. Releasing the
   actual fake owners restores two slots; D is Found with call count 3 and two
   available slots. This demonstrates the conditional lifetime mechanism and
   release correctness, not production HttpClient getting stuck.
5. Production HTTP passes cancellation to SendAsync, body stream acquisition and
   each read, with an independent eight-second request deadline. Exceptions and
   response/body disposal retire cooperative calls. No live evidence here proves
   these operations stopped cooperating on the user's machine.
6. `RetirableMediaWork` similarly caps two retained source workers, removes
   completed retirements, and notifies capacity after callback cleanup. Two
   genuinely stuck retirement callbacks can delay replacement; its capacity
   wait normally leaves the view Loading rather than the screenshot's Failed.
   Source fetch retirement is detached from lingering AI-native cleanup, so
   normal AI retirement alone does not consume both source slots.
7. Beta9 response reuse is ten seconds/16 URLs, not a thirty-minute state. LRU
   eviction and byte bounds reduce reuse but cannot turn a different URL into a
   saved error. API405/malformed responses are not persistent response entries.
   Beta10 shares their result only with already-waiting callers; a later caller
   retries normally. Owner cancellation and clear fence late writes by entry
   reference. Neither cache maintains a provider-wide failed state.
8. Persistent source keys bind track evidence, strategy and target. They cannot
   poison arbitrary later songs with one track's empty/error document. Original
   misses and translated lower-priority decisions have separate earlier fixes.
   AI no-useful memoization is bounded/per-full-identity and cannot disable native
   translation lookup. Quota/LRU work does not operate as a provider breaker.
9. The one-minute/64-event diagnostic limit suppresses logs, not requests. The only
   three-failure pause is AI inference. Matching provider translations return
   before that circuit/inference path. Display-language filtering and secondary
   visibility settings are deterministic per current state, not elapsed playback
   minutes; source original text is not removed by the LocalAi-only cleanup.

## Remaining evidence gap

The live episode has no contemporaneous per-provider admission/HTTP/API/query
trace or actual search/lyric payload in this task. Therefore neither saturation,
artist rejection nor upstream status can be assigned as its complete runtime
cause. The new Admission stage contains only existing provider/outcome/timing
fields: no track names, identifiers, URLs, lyric text or response messages.
It separates 'no invocation started' from a real HTTP-stage failure without
changing privacy text or sending data externally.

Known source-backed repairs are ready to integrate with PR89. Native Windows
execution and the real-song/time-pattern attribution remain unverified. Treat
'no further defect found in the reviewed paths' as a review result, not a claim of
zero bugs or removal of all upstream rate limiting. Release preparation and
publication of the newly authorized Beta11 remain the parent task's responsibility.

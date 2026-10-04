# Beta10 native translation recovery investigation

Base: main `9fc34f7e7505fac46bd9f15b08bdd567963a51f8` (Beta9).
Work performed exclusively in the delegated Linux cloud workspace. No laptop,
real lyric-provider probes, cookies, IP rotation, mirrors, prompt changes, privacy
changes, release-version edits, or release override changes were used.

## Confirmed implementation defects and fixes

These defects are established by source inspection and focused fake-transport
checks. Their individual contribution to the user's real failures remains
unmeasured; a song-count threshold cannot be inferred from the report.

| Cause | Beta9 code evidence | Repair |
| --- | --- | --- |
| Sharing fails on rejection/non-cacheable responses | `NetEaseResponseCache.GetAsync` waits for a completion signal then loops; only reusable successes populate `Payload`. Each live waiter can send the rejected URL again. | Share the actual response or captured transport error with existing waiters. Rejections still expire with that invocation and are never cached for independent later calls. Cancellation still permits a new owner to replace the retired generation. |
| Successful responses may fail reuse validation | `Reusable` requires every present optional `yrc/tlyric/ytlrc` field to contain a string lyric. A valid LRC plus absent-data `null`/empty optional object fails this condition. | Accept missing/null/empty optional lyric data while retaining a valid LRC/YRC and rejecting wrong JSON types. Preserve the whole original/translation payload, never combine payloads across recordings. |
| Success reuse ends before revisits or session handoffs | Beta9 retains only 16 URLs for ten seconds: even ordinary songs use a search and lyric URL; variants add searches. Cancellation cannot retract a request already delivered to the server. | Ten-minute, 128-entry LRU of successful exact URLs, still bounded to 16 MiB. Expiry, explicit refresh, clear, and owner-generation fences remain. Every caller re-scores metadata and binds its own identity. |
| Original-only persistent success hides future translations | `QueryDetailedAsync` returns any validated cache hit. A completed source search with no translation is persisted without a retry lifetime. | Never short-circuit a target-aware lookup with an original requiring translation, and never persist that negative translation decision. Originals remain the returned presentation/AI source. |
| A transient preferred-source failure becomes a permanent source choice | A translated backup is cached even when the preferred provider failed; later reads never revisit the preferred provider. | Persist target-aware lower-priority selections only after their higher-priority searches completed without failure. Target source identity advances to `source-v5` to retire old such decisions once. Matching source translations still bypass AI even when not persisted. |
| Supplemental fan-out and unnecessary latency | After a progressive preferred original, Beta9 starts backup and all remaining sources together and awaits them even when the preferred translation arrives. | Admit backup first; its progressive original or completed unsuccessful result admits the remaining race. Its matching translation avoids or cancels remaining calls. A matching preferred translation cancels supplemental presentation work immediately. |
| Transient snapshots send irreversible network traffic immediately | `OnMediaChanged` cancels, but `LoadLyricsAsync` immediately enters providers for every admitted snapshot. | A cancellable 150 ms admission delay coalesces transient metadata/rapid skips before HTTP. Explicit refresh bypasses this delay. This is per-work admission, with no failure cooldown or disabled-provider state. |

The successful-response TTL can retain an upstream original-only payload for up
to ten minutes; refresh always refetches. This is a bounded freshness tradeoff,
not a statement that native translations will exist on every request.

## Complete chain review

* `WindowsMediaSessionService` discovers the OS-preferred source, observes bounded
  sibling renderers, and selects richer metadata only for matching tracks. During
  a fast skip, weak/rich renderers can diverge and then converge. Selection may
  change the runtime session identity and album/duration evidence. Keep those
  identity safeguards; merging sessions by title alone risks publishing a stale
  recording. Generic exact-URL reuse absorbs equivalent HTTP lookups while each
  query independently validates its richer/sparser evidence.
* `MediaExperienceService.OnMediaChanged` invalidates the publication generation
  on a real identity change. `RetirableMediaWork` keeps at most two actual work
  lifetimes. Settings reload uses `LyricsReloadPolicy`; duration improvement may
  retry a same-track miss. Source, UI, partial-AI and final-AI publication fences
  all remain. The new admission delay releases cooperative skipped work before
  it reaches HTTP; already-sent requests cannot be undone.
* `LyricsService` retains actual provider gates of two calls per provider,
  eight-second per-provider deadlines, a 24-second outer bound, and one shared
  three-second translation budget starting at the first validated original.
  Backup/remaining transitions never reset that budget. An uncooperative
  transport still owns its slot until actual retirement; it cannot make the
  current presentation wait indefinitely. Supplemental tasks are drained under
  their presentation tokens before the coordinator exits.
* NetEase and QQ allow up to three distinct lyric candidates and up to three
  title/artist search variants. This can produce up to six HTTP operations per
  provider lookup. IDs are deduplicated within a query; 405 is terminal for that
  query and does not probe more candidates. Alternate queries remain available
  to recover incomplete/publisher-decorated metadata, rather than sacrificing
  translated recordings to an arbitrary one-candidate cap.
* NetEase keeps paired YRC/ytlrc or LRC/tlyric documents; Beta9 parser revision 2
  remains correct for the previously inspected payloads. QQ parses `lyric` plus
  `trans`, validates status/shape, decodes HTML and uses the same target language
  policy. AMLL searches normalized titles, validates artist/album/duration,
  reads up to three matched TTML documents and retains explicit language tags.
  There is no code evidence that QQ or AMLL can reliably replace unavailable
  NetEase native translations in all cases.
* `LyricsHttpClient` forwards cancellation through send, response reads and body
  bounds. Same-origin HTTPS redirects and the host allowlist remain. Successful
  HTTP alone does not establish API success. Beta9's HTTP 200/API 405 evidence is
  consistent with upstream rejection, independently of payload parsing.
* `LyricsCache` atomically writes complete validated documents, generation-fences
  clear, bounds size/quota, and preserves provider provenance/revision. Cache keys
  preserve strategy, target, metadata and exact duration; sparse/rich evidence
  therefore does not blindly inherit a matched document. The response cache is
  deliberately below that boundary.
* `AiLyricsService` and `LyricsTranslationCoordinator` check matching provider
  translations before inference/cache work. Source lyrics survive timeout,
  failure and cancelled/invalid partial generation. No AI prompt, model or
  inference-admission logic changed.

## Actual verification and limits

Ten focused checks passed, with no full or repeated test suite:

1. Complete optional-null lyric payload reuse after a 45-second revisit (new).
2. Concurrent API 405 shares one request; independent retry refetches (new).
3. Cancelled owner cannot block/overwrite its replacement (existing).
4. Preferred translation wins after an earlier supplemental translation (existing).
5. Configured backup priority over remaining sources (existing).
6. Three-second budget does not restart after backup (existing).
7. Preferred translation cancels unneeded uncooperative supplementals within a
   one-second presentation bound (new).
8. Target-aware original-only result cannot hide a later native translation (new).
9. Transient preferred failure cannot persist a lower-priority translation (new).
10. Progressive backup translation cancels an already-started remaining-source
    search within a one-second presentation bound (new).

The final infrastructure build after the last source adjustment succeeded with
zero warnings/errors; Core was built as its dependency. Cloud SDK 10.0.100 was
installed under `/tmp`, with temporary writable CLI/NuGet directories. The
repository's pinned `global.json` SDK 10.0.401 was restored unchanged; these are
not claims of qualification under that newer SDK.

WinUI App/native Windows build and real-player quick-skip behaviour remain
unverified in this Linux workspace. The small App admission-delay edit has been
source-reviewed, not executed against SMTC. The real eight-song scenarios and
upstream rate-limit recovery need Windows cloud build plus user-device feedback.
No claim is made to remove all upstream rate limiting: uncached settled songs
still require legitimate HTTP requests and the service may reject them.

Only the fix branch is pushed. No draft PR was opened because that would trigger
full Windows CI tests against the explicit minimal-test instruction. Parent task
owns Beta10 metadata, necessary Windows build/release integration and publication.

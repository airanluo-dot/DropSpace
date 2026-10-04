# Beta11 second review

The user reproduced the symptom on Beta9, so this is a shared-chain investigation, not
proof of a Beta10 regression. This review used cloud code and offline fixtures only.

## Provider retirement and cancellation

- Production `HttpClient.Timeout` is infinite, not the default 100 seconds.
  `LyricsHttpClient` imposes a linked eight-second token on send, content acquisition and
  every read. Redirects are at most three and stay on the same allowed HTTPS origin.
- Requests/responses/streams/buffers/timers are disposed through scoped ownership. Transport
  and provider awaits use `ConfigureAwait(false)`; no UI scheduler or synchronous `.Result`
  waits were found in these execution paths.
- `RedactingFileLoggerProvider` uses `Channel.Writer.TryWrite`; a full queue drops a log rather
  than blocking the provider. Its file writer and shutdown drain do not own lyric gate slots.
- `ProviderCancellation.CompleteAsync` waits for actual cancellation callbacks, then disposes
  tokens. `InvokeProviderAsync` releases its gate in its finally after this cleanup. Successful,
  failed and cancelled cooperative invocations retire through the same ownership path.
- Synchronous JSON decode/parse, catalogue LINQ and lyric parsing do not poll cancellation.
  They are size/depth/item bounded, with regex timeouts; they can overrun presentation deadlines
  during CPU work. Eight seconds is a cooperative deadline, not a hard CPU-execution bound.
  No source-code path proves a permanently nonreturning production parse. Two fake providers
  deliberately ignoring cancellation can exhaust the two provider slots, but this does not
  establish that Windows HTTP or a real provider did so during the reported half-hour episode.
- Do not release a slot while its real operation still runs or replace its gate to hide
  saturation. Truly noncooperative work requires bounded process isolation and owned exit;
  a timer around an in-process task does not terminate it. This large change is not justified
  solely by the fake-gate probe. Admission-vs-query diagnostics now distinguish the cases.

## Reverse review

- Tightened legacy source-v4 migration to original-only documents. Same-language original
  eligibility alone could admit old secondary rows whose pairing was not established by the
  migration decision. Existing provenance, provider revision and recording validation remain.
  New fixture `Beta9PrimaryOriginalWithUnprovenSecondaryIsRefetched` passed once; no old checks
  were rerun. This is a conservative one-time refetch, not deletion of old files.
- P2 cache completeness is relative to the explicit target. With no target a nonempty original
  is sufficient; this does not classify Han text as Chinese or assert translation availability.
  With a target, the existing conservative language policy governs completeness.
- The one-artist mapping from 1f07f5a is superseded by the user's general-rule requirement and
  must not be treated as the final release fix. General alias and optional AI requirements are
  documented in `beta11-candidate-selection-design.md`; they are not enabled by a design file.
- NetEase, QQ and AMLL retain a progressive original before probing additional translation
  candidates. A later recoverable candidate error can still make their direct provider call
  throw, but `LyricsService` retains the earlier reported original. Do not describe this as a
  proven UI original-loss bug. The error legitimately signals incomplete translation search.

## Further source-specific findings under investigation

AMLL's official SongItem does not carry duration: the current adapter's lookup supplies zero
for ordinary responses. Its API supports artist filtering and pagination; current title-only
first-page lookup can miss identity matches among many same-title entries. These are separate
from transport rejection and require bounded source-specific search adaptation.

Kugou's adapter currently requests `fmt=lrc` only and does not validate provider error shapes.
Public client implementations use the same allowed download endpoint with `fmt=krc`; that
format can contain word timings and encoded translations. This is evidence of a missed format,
not a stable official API guarantee or proof that each recording has native translation.
Any implementation needs bounded decompression, strict field validation and terminal rejection
handling; it must not retry around a provider rate-limit response.

Windows native transport retirement, actual screenshot search payloads and the reported
half-hour episode remain unverified. Windows compile/package checks and Beta11 reviewed-source
approval are still required. PR89 CI failed at stale approval before compilation; no override
was bypassed. The complete set of known changes is not yet ready for release.

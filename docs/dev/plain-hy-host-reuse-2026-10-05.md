# Same-song exact success reuse and host metrics

**【不准测试】** All execution evidence and reproduction instructions below are historical. The later user instruction prohibits further tests, benchmarks, model runs or test CI; do not run those commands. Root integration uses manual review and necessary compilation only.

Based on fetched `fix/beta10-chinese-lyrics-regression` at
`64457419ae6fc8af15ab5542a402b7b871cbf9be`. Work is isolated on
`feat/plain-hy-song-reuse-host-metrics`; no main-branch write, release, native helper
change, engine build or model download was performed.

## Request-local reuse

`PlainHyLyricsCoordinator` now owns a `PlainLyricsSegmentMemo` for each whole-song
request. Its key compares exact prompt UTF-8 bytes (Base64 representation),
normalized target language, the existing inference identity and the current
`LyricsLanguagePolicy.Version`. No source normalization or fuzzy match is added.
The separate admission version is explicit because the baseline inference identity
does not itself include `LyricsLanguagePolicy.Version`; existing persistent cache
identity is unchanged.

Only complete segment output which independently passes existing host output
admission and produces a useful translation is retained. Copied-source, incomplete,
protocol-leaking or rejected text is not retained. Each hit still goes through the
current row's complete host mapping and obtains its own ID, original text/timeline,
words and admission key. Physical segments can repeat within a display row as well
as in later chorus rows. Whole-song validation and persistence remain unchanged.

The memo is limited to 128 entries and 256 KiB of retained UTF-16 string payload
(with dictionary/key overhead also bounded by the entry cap). Entries exceeding
either limit simply run inference. The memo is explicitly cleared in the request's
`finally`, including failure/cancellation/invalidation; no tasks, native resources,
cross-song work or cross-request cache are retained. Generation, request and
cancellation fences run before and after both inference and reuse. Partial songs
remain ephemeral and are never persisted.

Default 1.8B Q8, optional 7B Q8, the official prompt, sampling and helper protocol
are unchanged. Matching provider translations still bypass inference.

## Host metric contract

An optional `MeterListener` can subscribe to `DropSpace.PlainLyrics.Host`, version
`1.0`. There is no exporter, network delivery or persisted diagnostics by default.
`dropspace.plain_lyrics.host.duration` is a `double` histogram in milliseconds,
tagged only with `stage` and `outcome`. The monotonic clock is `Stopwatch`.

| Stage | Measured boundary |
| --- | --- |
| `Song` | Coordinator entry through final return/exception, including its cache lookup and persistence; excludes service/package resolution before coordinator entry |
| `FirstUseful` | Coordinator entry through the first accepted useful row, or a validated complete-song cache hit; host admission availability, not UI presentation or first native token |
| `Infer` | A real coordinator delegate invocation through return/exception; includes runner queue/startup/cleanup as applicable; segment hits have no invocation |
| `OperationQueue` | Production resident translation waits for its per-runner operation gate |
| `RuntimeResolve` | Resident worker executable resolution; extraction can occur here |
| `NativeQueue` | Blocking acquisition of the shared native owner gate; a warm resident does not acquire it again |
| `LoadReady` | After native admission, process-start preparation through the verified ready handshake; failed startup has a failed observation |
| `Call` | Request/frame construction and pipe write/flush through validated complete response; excludes startup and subsequent error cleanup |
| `CancellationRelease` | First cancellation signal targeting this owned session through confirmed process exit, readers/memory-watch settlement, handles and shared gate release |

Outcomes are `Success`, `Failed`, `Cancelled`, `NoUseful`. Warm resident reuse is
counted, rather than inventing a zero cold-load duration. Queued cancellation owns
no child and therefore emits no cancellation-release sample. Cleanup failures or
still-pending cleanup never emit a successful release observation; the existing
fail-closed cleanup continuation remains responsible for the gate. Session load
and release observations also cover the shared selector's owned session lifecycle.

`dropspace.plain_lyrics.host.events` is a `long` counter with one fixed `event` tag:
`SegmentHit`, `SongHit`, `NeutralHit`, `ResidentReuse`, `FallbackAttempt` and
`FallbackUse`. `FallbackAttempt` counts a translation GPU failure followed by a
CPU retry after cleanup; `FallbackUse` counts validated translation calls actually
using CPU while GPU preference is enabled, including remembered fallback.

These metrics contain no lyric/prompt text, track/title/artist, request identifiers,
paths, model bytes or exception messages. Listener exceptions are isolated from
translation and resource ownership. Synchronous listener-triggered invalidation
is fenced again before returning a cached or final result. Histograms are nested boundaries and must not
be added together as independent pieces of `Song`.

## Experiments and checks

[Machine-readable evidence](evidence/plain-hy-host-reuse-2026-10-05.json) includes
the experiment source, observed metrics, test counts and complete failure messages.
All observations were collected on Debian 13 with the reused .NET 10.0.401 SDK.

- Before edits, existing relevant coordinator/progress/resident tests: 62 passed.
- New directed reuse tests: 12 passed.
- Final Release coordinator/admission/progress/resident/legacy-runner tests: 92 passed,
  none skipped. Covers exact repeat reduction, multi-segment rows, neutral/invalid
  rejection, admission rebinding, seek, language/model/runtime identity, concurrent
  requests, bounded retention, clear/switch/cancel, and hostile diagnostic listeners.
- Release Core: 631 passed, none skipped.
- Infrastructure full Release: 785 passed, 11 failed, 26 skipped (822 total).
  All 11 failures independently reproduce in an archived checkout of the unchanged
  baseline. They involve Windows-only DPAPI/DropLink operations and existing
  lyrics fallback/LRCLIB/preference tests. Full-suite output is not a green release
  gate. The final affected 92-test suite ran after the final clear/cache-hit-metric
  and synchronous-listener fence refinements; the full-suite run preceded that small refinement.

An isolated whole-song experiment uses eight display rows with three exact
different prompts and a 20 ms simulated inference delay. Actual coordinator
invocations fell from **8 to 3** (62.5% fewer), with translated outcome and preserved
timeline in both runs. Observed single-run elapsed times were 241.7175 ms before
and 137.0674 ms after. These include startup/JIT/host work and are illustrative;
they do not establish a native-model speedup or a translation quality result.

Production runner with a real Python child protocol fixture recorded CPU ready
handshake 14.5155 ms, validated call 0.1832 ms and completed-song cancellation to
confirmed resource release 186.3204 ms. The release interval includes the existing
memory watchdog settling. Another fixture observed one failed GPU startup, one
CPU retry, two CPU fallback uses and one warm-resident reuse. These are actual
host/process observations with a simulated worker, not GPU/model measurements.

Reproduce affected checks with:

```sh
dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj \
  -c Release -p:RestoreLockedMode=true \
  --filter 'FullyQualifiedName~PlainHy|FullyQualifiedName~PersistentPlainLyricsRunnerTests|FullyQualifiedName~LlamaCompletionRunnerTests'
dotnet test tests/DropSpace.Core.Tests/DropSpace.Core.Tests.csproj \
  -c Release -p:RestoreLockedMode=true
```

Set `DROPSPACE_HOST_METRICS_EVIDENCE` to a writable directory while running the
resident tests to save the two process-fixture metric captures. The isolated song
experiment source is embedded in the evidence JSON and can be built in a temporary
`net10.0` console project referencing `DropSpace.Infrastructure.csproj`; run it
against the baseline and this branch in separate checkouts. No model is needed.

## Native timing proposal for parent/KV task coordination

The native helper was not edited. A future optional `timing` object on a validated
complete response can expose numeric `version: 1`, `prefillMs`, `decodeMs`,
`promptTokens`, and `generatedTokens`. Prefill covers prompt evaluation, decode
covers generated-token evaluation; process/model startup remains the host
`LoadReady` boundary. The KV owner must define whether restore/reset time belongs
to prefill or a separate optional `kvRestoreMs`/`kvResetMs` before enabling capture.

Host parsing should require finite nonnegative numbers and bounded integer counts;
missing or rejected diagnostics mean unknown, not zero, and do not turn an otherwise
valid translation into failure. Timing data must remain separate from prompt/text,
output admission and inference identity. Actual native timing implementation and
the frozen protocol/version decision belong to parent coordination with the KV
task. Windows shipping runtime, real 1.8B/7B and GPU/native timing verification
remain outstanding; this branch provides no release certification for them.

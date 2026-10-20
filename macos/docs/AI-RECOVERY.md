# AI lyric recovery and inline Music controls

Fixed source: Windows `v0.3.2-beta.7`,
`1cd4620dd58a67b0963b73487743137813b8864a`.
This is scoped implementation/validation evidence, not complete AI or Mac parity.

## Contract and ownership

- `LyricsInferenceCircuit.cs` pauses after three consecutive failures. Success
  resets the count. A paused circuit rejects even a late success; Resume advances
  the generation and old completions cannot affect it.
- `AiLyricsService.cs:390–517` checks the complete-song cache before inference
  admission. Cache hits do not reopen the circuit. Cancellation, resource
  admission and NoUsefulTranslation do not count as inference failures.
  Missing/disabled model admission is recoverable without tripping the circuit.
- `AiLyricsService.cs:102–149` resets execution on enabled/model/backend changes
  and explicit recovery. GPU retry retains persistent cache; the next uncached
  request attempts the selected backend.
- `LyricsInferenceCircuit` is a Swift domain value owned by
  `PlainHyLyricsTranslator`. `NativeLyricsService` owns recovery through its
  existing lookup/translation task chain, drains previous work and publishes only
  for the current generation. Music views do not own detached recovery tasks.
- `PlainHyTranslationWorker.stop` now preserves Metal failure cooldown across
  ordinary song/idle retirement. Explicit GPU retry or a configuration change
  clears it. Previously setting configuration to nil on every stop accidentally
  erased the cooldown when the next request began.
- `MusicPreferences` is inline native UI backed by the existing serialized
  settings transaction. Missing/disabled/busy selected models route to the real
  Components page. Enabling AI does not silently start a model download. Model
  replacement while AI is enabled requires the replacement to be installed.
  Online-lyrics consent and destructive cache clearing have native confirmation.
  Font size has both a 0.1-step slider and a decimal numeric field.

## Executed evidence on macOS 27.0.1 / arm64

`swift test --package-path macos --filter
'plainHyCircuitCountsFailuresPreservesCacheAndRecoversExplicitly|lyricsCircuitSuccessAndRetiredGenerationFollowBeta7|plainHyCoordinatorPartialFailureAndClearFenceNeverPublishCompleteCache'`
passed 3 focused tests in 0.019s (`build/lyrics-circuit-tests.log`). They cover
failure threshold, success reset, old generation rejection, cached output while
paused, resource/package admission exclusions, recovery retaining cache, partial
failure and cache-clear cancellation fencing. Fixture workers are not native
inference evidence.

Actual native controls and workers:

1. `MusicInlineNativeSmoke`: enabling AI without a model opened the real
   Components settings section and left AI false. No download started.
   Font 16→16.5 persisted to JSON and restored to16; cache-clear cancellation
   returned without clearing. Normal Quit exited. Evidence:
   `build/MusicInlineNativeEvidence/initial-controls-receipt.json`.
2. `MusicCircuitNativeSmoke`: separate settings/database/cache and APFS-cloned
   already-downloaded model files, authored Chinese local lyrics, existing own
   silent `DropSpace Validation` song paused at10s. No provider requests or
   playback commands. Initial normal CPU inference completed.
3. Added one authored line to avoid the valid existing AI cache. Three deliberate
   SIGKILL faults targeted only new `plain-lyrics-worker` children of this
   validation app PID50696. The first two surfaced pipeFailure; the third
   displayed the real paused state and Resume button. Original lyrics remained.
   Another Refresh showed the paused state and no worker was running.
4. Resume through native UI completed five translated rows on CPU. Metal was
   then selected; cached output remained usable. One deliberate Metal child
   fault caused actual CPU fallback, complete output and a visible Retry GPU.
5. Retry GPU returned a cache hit without inference. All three existing cache
   file hashes were unchanged and no worker remained. A changed authored line
   then caused actual Metal inference; six complete translated rows appeared.
   PID51679 command line confirmed `--mode metal`.
6. Cache confirmation displayed the corrected Chinese Cancel text and was
   canceled. Normal Quit exited both app50696 and the last worker51679.
   AX states, process fault receipts, cache hashes and summary:
   `build/MusicCircuitNativeEvidence`.

Builds `music-inline-native-build.log`, `music-circuit-native-build.log` and
`music-circuit-localized-build.log` succeeded. These establish compilation only.
An initial localization-edit command failed because the system Python did not
support `zip(strict=...)`; no additions from that command were written. The
corrected import produced936 fixed IDs plus45 native IDs, each in ten languages.

## Runtime observations added after the recovery run

`PlainHyRuntimeStatus` carries immutable starting/executing, last-completion and
failure records. A request becomes executing only after its protocol frame is
written. Failed-process cleanup records the observed exit status/signal and
whether DropSpace requested termination before permitting fallback. CPU fallback
does not replace its causal Metal failure record. Ordinary retirement retains
last/failure observations; explicit execution reset clears them. Cache clearing
now retires work without implicitly resetting the circuit or GPU failure state,
matching `AiLyricsService.ClearCacheAsync`.

The app observes worker events with source/translation generation fences. Music
renders the selected backend separately from actual current/last execution,
localized reason, timestamp, observed exit or signal, host termination and CPU
fallback. Dates and byte counts use the selected interface locale. Machine
failure records also enter the macOS unified log; raw error details are private
and no lyric/prompt text is logged.

- The controlled native protocol-process test
  `runtimeStatusObservesRequestWriteExitFallbackAndExplicitReset` passed in
  `lyrics-runtime-status-tests.log` and, after refining fallback preservation,
  `lyrics-runtime-status-final-tests.log`. Extending it to a genuinely killed
  idle child passed in `lyrics-runtime-status-idle-tests.log` (1 case,0.889s).
  It checks starting→executing ordering for Metal/CPU, observed exit71, confirmed
  exit before fallback, ordinary retention, explicit reset and idle SIGKILL.
  Its protocol fixture does not prove GPU computation.
- Actual application54137 with the real1.8B worker: deliberately killed only
  Metal child54253. Native UI showed selected METAL/current CPU executing,
  retained the Metal failure with SIG9/hostStopped false, and completed seven
  authored translated rows. The unified log agreed and redacted raw detail.
  GPU retry cleared the old execution/failure records and returned cached rows.
  Normal Quit exited; the temporary extra lyric line was restored. Evidence:
  `build/MusicRuntimeStatusNativeEvidence`. Startup phase was not separately
  captured in this real-model run; idle failure was covered only by the
  controlled protocol-process test.
- `music-runtime-status-build.log`, `music-runtime-status-native-build.log` and
  `music-runtime-status-logging-build.log` passed; native evidence used the last.

## Remaining acceptance

The above does not prove natural GPU/hardware failures, real low-memory/pressure
transitions, 7B inference, all cancellation/quit/reconnect interleavings, full
runtime-fault coverage or live ten-language/VoiceOver behavior. A final small
publication-fence and paused-copy polish follows the recorded native fault run;
its final build/readback is recorded in EXECUTION. Other player adapters, QQ
handoff, enhancement capability management, physical seek/wheel interactions and
the public release remain open.

# Native fixed-prefix experiment handoff — 2026-10-05

**【不准测试】** The owner's latest instruction prohibits additional tests, model comparisons,
benchmarks, trial inference, verification reruns and test CI. No such work is authorized by this
handoff. All executed evidence below completed before that instruction. Existing test sources
are retained; unrun sources are not passes. Implementation remains optional and default-off.

## Delivery and scope

Base fetched from `fix/beta10-chinese-lyrics-regression`:
`64457419ae6fc8af15ab5542a402b7b871cbf9be`.
Independent branch: `experiment/native-prefix-kv-timings`. The initial timing commit is
`55f8ec4e5f5b95035d112c5d4bdda5f88dd1b011`; subsequent implementation/evidence commits build
on that base. No main-branch write, PR, publication, workflow dispatch or release is part of this
task. Only this task edited `tools/plain-lyrics-helper/main.cpp`.

`--timings` exposes native request stages without stdout log pollution. Optional schema 1
`timings` is attached to the existing JSON response; request shape and protocol versions remain
unchanged. The host's current `GetProperty` parsing tolerates additional response fields but does
not consume them. Future host integration must explicitly opt into the flag and read the fields.
Full field definitions and overlapping durations are in [README.md](README.md).

`--experimental-prefix-kv` restores only the exact official instruction's shared post-template
token prefix. It preserves both complete `llama_memory_clear(..., true)` calls, fresh sampler,
single-message template and full prompt tokenization. After tail removal and position validation,
it serializes only the fixed sequence KV cells, then zero-clears native buffers. This deliberately
avoids treating `llama_memory_seq_rm` as physical erasure. Snapshots stay in this worker/context;
they are never written to disk. Language/role/nonofficial-prompt transitions discard them.
Snapshot failures fall back to full prefill after zero-clear; incomplete responses do not retain
a snapshot. The unflagged path preserves legacy response fields and response/cleanup ordering.

1.8B Q8 remains default; 7B Q8 remains optional with the same exact-size/model-profile guards.
No prompt/sampling settings, HTTP architecture, production host settings, output/cache admission,
track-generation fences, cancellation ownership, release approvals or runtime trust manifests
were changed. Existing runtime manifests still bind existing shipped bytes; this experiment's
Linux binaries are diagnostic artifacts, not newly trusted Windows runtime packages.

## Already completed evidence

Evidence directory:
[`scripts/ai-model-qa/evidence/native-prefix-kv-cpu-2026-10-05`](../../scripts/ai-model-qa/evidence/native-prefix-kv-cpu-2026-10-05).

The cloud CPU was AMD EPYC 7763, five exposed vCPUs, Linux x86_64. Inference used four threads
and four batch threads with the frozen sampler/context/output limits. There was no GPU backend.
Pinned upstream llama.cpp was `7fe450e19305b828c199d602c23a8337aaa1f03b`. One Release CPU/AVX2
engine build was reused for original, timing-baseline and final workers; only helper objects were
recompiled/relinked. Build configuration used `GGML_NATIVE=OFF`, AVX/AVX2/FMA/F16C enabled,
CUDA/Vulkan disabled, examples/tests/tools disabled. The experiment did not rebuild/download
engines for each variant.

No usable GGUF/source/Linux worker was present in the selected environment. The 1.8B model was
downloaded once from the repository's pinned Tencent revision; exact size 1,908,528,192 and
SHA256 `5c3fe0b1408a5ceb0143184ef247b11b579c525f4b02b060e6c851bb76fef1a4` were verified
before inference. No 7B model was downloaded. Binaries/libraries/model remain reusable under
`/workspace/shared/native-build`, `/workspace/shared/llama-pinned` and `/workspace/shared/models`;
they are not committed to Git. Raw result files record executable/model hashes and invocation.

Four serial runs used the same 19 synthetic cases, repeated three times per resident process:

| Artifact | Completed requests | Output result |
|---|---:|---|
| `baseline.json` | 57 | Complete, correlated responses; baseline full prefill and zero-clear |
| `original.json` | 57 | All text/complete fields equal the timing baseline |
| `prefix.json` | 57 | All text/complete fields equal baseline; token accounting and transitions passed |
| `default.json` | 57 | All text/complete fields equal original; exact legacy response fields |

Baseline was measured before the prefix implementation. For all 51 baseline translation
requests, median total was 691.643 ms, prefill 352.028 ms, generated-token decode 303.281 ms,
initial zero-clear 17.474 ms, final zero-clear 17.602 ms. Cold model-ready observation was
1,310.019 ms; it includes initialization, warmup and pipe handshake, not just weight loading.

For the **same 38 cases that hit the fixed-prefix cache**, the stage medians were:

| Measurement | Baseline | Prefix experiment |
|---|---:|---:|
| Native total | 682.018 ms | 453.494 ms |
| Host pipe round trip | 682.286 ms | 453.711 ms |
| Prefill | 348.515 ms | 128.900 ms |
| Generated-token decode | 301.082 ms | 293.518 ms |
| Initial zero-clear | 17.476 ms | 17.442 ms |
| Final zero-clear | 17.605 ms | 17.525 ms |
| Prefix plan | — | 1.388 ms |
| Prefix restore | — | 0.178 ms |
| Prefix save | — | 0.205 ms |

The cached instruction was 20 tokens for Chinese and 18 for English. Median snapshot size was
1,311,752 bytes, below the 16 MiB hard cap. Inputs with common lyric text did not retain that lyric
prefix: retained-token counts stayed at the instruction boundary. Language changes had zero
reuse; nonofficial and selector requests retained zero tokens and forced the following official
request cold. Subsequent matching instructions reused fixed tokens with a freshly seeded sampler.

Across each repetition's hit cases, summed native total decreased 33.539%, 33.161% and 30.687%
(12, 13 and 13 hits). This is a measured cloud CPU benefit for these short synthetic lines.
It is not a production whole-song, Windows, GPU or 7B latency guarantee; source length and output
length will change the fraction of time saved. No default was promoted from this evidence.
Exact output agreement establishes equivalence for these fixtures, not general semantic quality.

Additional completed work before the prohibition:

- Actual CPU worker compilation and original helper linkage succeeded against the same engine.
- Existing native GPU/model admission policy test passed.
- Existing `PersistentPlainLyricsRunnerTests`: 37 passed, zero failed/skipped, 11 seconds.
  These fixture-process tests cover host ownership/cancellation/profile switching, not physical
  model/GPU behavior of this experiment.
- Seven baseline CLI rejection/admission cases passed (duplicate diagnostics, unknown/missing
  profile, unknown flag, invalid mode, CPU build rejecting Vulkan and unavailable model).
- `test_prefix_policy.cpp` passed with assertions enabled and strict warnings. Its first strict
  compile failed on unused functions in pinned upstream Jinja headers; using system include
  directories for upstream headers resolved that compile without changing upstream code or
  disabling warnings for the worker/test source.

## Explicitly unrun or uncertain

The testing prohibition arrived before the native cancellation/restart harness or real-model
snapshot failure checks ran. `check_prefix_isolation.py` and `test_prefix_state.cpp` are retained
but unrun; the latter is also uncompiled. Prefix without timing, injected restore/removal failure,
native in-flight cancellation, graceful replacement and native invalid/incomplete-response
recovery have not received an additional model run. They are not reported as verified.

Actual Windows Job/memory cleanup, physical 7B inference and Vulkan execution were not available
and were not established here. Model/mode/maintenance isolation continues to rely on the existing
host-owned process lifecycle; the snapshot cannot survive worker destruction. No new test or
model run is required for this handoff under the owner's current constraint. There is no remaining
implementation blocker; promotion or release lies with the parent coordinator.

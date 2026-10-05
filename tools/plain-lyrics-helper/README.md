# Private resident plaintext runtime

Pinned upstream: llama.cpp `7fe450e19305b828c199d602c23a8337aaa1f03b` (v0.5.0).
The host accepts only the pinned Hy-MT2 1.8B Q8_0 (default) and optional Hy-MT2 7B Q8_0
models with the existing official plaintext prompt and sampler. The helper accepts the legacy
`--model <path> --mode cpu|vulkan` command unchanged. The optional final pair
`--model-profile hy-mt2-7b-q8` selects 7B; `hy-mt2-1.8b-q8` explicitly selects the default.
Unknown profiles, unrecognized arguments, duplicate options and arbitrary model-size/budget
overrides are rejected. Diagnostic arguments described below are explicitly opt-in.

Before initializing a backend or loading weights, the worker checks the selected profile's exact
file size: 1,908,528,192 bytes for 1.8B or 7,981,928,896 bytes for 7B. The host separately verifies
the pinned hash under a retained file lease; size alone does not authenticate a model. The 7B
SHA256 is `58b3ad55dd6f6fa08c695cddc34fb5f8f708a844f78ae10508071914b0ed67c0`.
The ready handshake adds `modelProfile` with the selected ID. The existing protocol version and
`--version` runtime profile `hy-q8-plain-resident-v1` remain unchanged; these identify the wire
protocol/runtime contract, while `modelProfile` identifies the selected model and budget.

## Execution contract

The ready frame advertises `selectionProtocol: 2`. Translation requests remain protocol 1,
1800-byte input, 2048-token generation and 16384-byte output, with the existing translation
template unchanged. Independent recording-selection requests use protocol 2, bounded metadata
only, at most 8192 input bytes, 32 generated tokens and 128 output bytes. The frozen 4096-token
context is shared; each request reserves its own output bound and resets sampler/KV/history.
Protocol-2 truncation without EOG is rejected, and the host only accepts a listed candidate ID
or explicit abstention. Old helpers without this handshake capability cannot run selection.
These limits define execution, not evidence of model accuracy or subsecond latency.

- Separate baseline and AVX2 CPU executables, plus one Vulkan executable for NVIDIA and AMD
- AI stays opt-in; GPU preference defaults on once AI is enabled. CPU threads remain four
- Private inherited anonymous pipes only, bounded JSONL, host-owned random request IDs
- No listening server, user configuration, downloads, interactive console or child subprocesses
- Model weights, template and context allocation stay resident for at most 60 idle seconds
- Before/after each request, all KV/recurrent memory is zero-cleared. Sampler/seed and conversation
  history are recreated per request. By default no cross-line prompt or generation history is reused
- Frozen context 4096, max output 2048 tokens, 60-second per-line execution, existing whole-song
  budget. Output hitting the token limit without EOG is rejected, never cropped into acceptance
- Cancellation (including between lines), disable, model/mode change, maintenance and app disposal
  release the owned process. CPU fallback starts only after confirmed GPU-process exit and pipe/
  Job-handle cleanup. A teardown timeout keeps the global inference gate closed
- Windows Job committed-memory cap remains 3 GiB for default 1.8B. The host selects a separate
  12 GiB cap only for the verified 7B profile, with the same one-process limit and a matching
  working-set watchdog. These are resource limits, not a security sandbox

## GPU admission and optimization

The worker queries actual Vulkan and ggml device properties. It selects one qualifying device,
preferentially discrete, with the largest free budget; multi-GPU splitting is disabled. NVIDIA
vendor ID 0x10de and AMD 0x1002 are eligible. Unknown vendor, missing memory-budget extension,
unavailable driver, unknown memory, or insufficient budget yields CPU fallback.

Admission requires >=4 GiB reported memory and free space for the exact model bytes plus the
profile's context/compute allowance and max(1 GiB,20% total) reserved for desktop/video.
The unchanged 1.8B allowance is 1 GiB; the 7B allowance is 2 GiB. Integrated devices also require
separately measured Windows available physical RAM >=model bytes+3 GiB for 1.8B or
>=model bytes+4 GiB for 7B. A shared-memory capacity number alone is insufficient. This is
conservative admission, not a hard VRAM allocation cap. Windows/driver allocation failure still
terminates the worker and falls back safely.

Pinned ggml chooses AMD architecture/subgroup kernels and NVIDIA cooperative-matrix kernels from
reported capabilities. This integration does not force CUDA, HIP, FP16, vendor-name heuristics or
unsupported architecture overrides. Real acceleration/quality/performance must be measured on
actual hardware; CPU builds and mock policy tests do not establish that claim.

`c++ -std=c++17 -Wall -Wextra -Werror test_gpu_policy.cpp -o /tmp/dropspace-gpu-policy && /tmp/dropspace-gpu-policy`
runs the small policy-only regression test without model weights or GPU access. It covers the
whitelist, exact model-size boundaries, default-policy compatibility, both vendors, discrete
free-memory thresholds, integrated host-memory thresholds and fail-closed unknown values.

## Opt-in native diagnostics

Current task constraint (2026-10-05): **【不准测试】**. Do not run tests, model replays,
benchmarks, trial inference or test CI. The diagnostic sources/commands below are preserved for
provenance and handoff; completed evidence predates this constraint. See
[the experiment record](PREFIX-EXPERIMENT.md) for completed and explicitly unrun work.

`--timings` adds a `timings` object to each response, including selector protocol 2 responses.
Without this argument the ready/response/version contracts stay unchanged. The request remains
exactly `{protocol,id,prompt}`. Diagnostics never print log lines to stdout; stdout contains only
the existing JSONL handshake and responses. The current host ignores extra response properties,
so these fields are compatible but require explicit host integration to expose measurements.

`timings.schemaVersion` is 1. All durations are nonnegative milliseconds measured with
`steady_clock`: `totalMs` covers validated request parsing through cleanup, excluding pipe reads,
JSON response serialization and pipe writes; `resetMs` covers initial zero-clear;
`samplerInitMs`, `templateMs`, `tokenizeMs` and `samplerAcceptMs` measure their named stages;
`prefillMs` covers prompt `llama_decode` calls; `decodeMs` covers generated-token `llama_decode`
calls; `sampleMs` covers sampling and sampler acceptance; `generationMs` covers the entire
generation loop (including decoding and token-to-text conversion); `cleanupMs` covers final
zero-clear. `generationMs` overlaps `decodeMs`/`sampleMs` and must not be added to them.

`inputTokens` counts the full model-facing prompt after the actual template and special-token
handling. `prefillTokens` counts prompt tokens evaluated this request. `outputTokens` excludes
EOG; `sampledTokens` includes EOG when complete. `prefixReusedTokens` is zero in the baseline.
These are worker measurements, not host end-to-end/song latency, model load time or GPU claims.

`benchmark_prefix.py --worker <executable> --model <verified-GGUF> --output <result.json>`
runs a resident CPU baseline over synthetic fixtures with both targets, repeated/shared source
prefixes, punctuation, nonofficial prompts and selector transitions. `--reference <baseline.json>`
records byte-for-byte output differences. It asserts bounded, correlated, complete JSON responses
and token accounting. `--no-timings` checks the default response has exactly the legacy fields.

### Fixed-prefix KV snapshot experiment

`--experimental-prefix-kv` enables an independent, default-off experiment. It adds only
`experimentalPrefixKv: true` to the ready frame. It does not enable `--timings` implicitly.
The production host adds neither flag; no settings, HTTP transport, model/sampler defaults,
runtime trust manifest or output admission behavior are changed by this experiment.

Only protocol 1 requests beginning with either exact frozen official target instruction qualify.
The worker renders both the full request and the source-free fixed instruction with the actual
loaded model template, checks that their bytes before the source agree, tokenizes that fixed
portion, drops its boundary token, and computes the longest common token prefix with the full
templated request. Dropping the boundary token prevents source-dependent tokenizer merges or
appended special tokens from entering the snapshot. Duplicate/changed instruction rendering
fails closed. At least one full prompt token is always evaluated fresh to produce valid logits.
The complete prompt is still templated/tokenized and accepted by a newly seeded sampler.

After a complete response, sequence 0's source and generation tail is removed and its remaining
position bounds are checked. Only then is `llama_state_seq_get_data` used to serialize the fixed
prefix into a process-owned buffer (maximum 16 MiB). The complete native memory is still cleared
with `llama_memory_clear(..., true)`, including data buffers, before reporting the experimental
result. On the next matching request the worker zero-clears memory, restores the prefix sequence
snapshot, checks positions, trims it to the actual shared token count, and evaluates the suffix.
It never serializes the whole context, sampler, logits or deleted source/generation cells.

Changing target, protocol 2 selection, nonofficial prompts, missing shared tokens, incomplete
responses or failed snapshot operations discard the snapshot. Restore/removal failure also
zero-clears any partial native state before falling back to full prefill. Exceptional request
exits retain RAII zero-clear. Process EOF/termination destroys the snapshot; host cancellation,
model/mode switching, idle cleanup, disable, maintenance and disposal keep their existing
owned-process drain responsibility. A killed or replaced worker cannot transfer snapshots to
another worker/model. No serialized state is read from or written to disk.

When `--timings` is also enabled, additional schema 1 fields report `prefixPlanMs`,
`prefixRestoreMs`, `prefixSaveMs`, `prefixRetainedTokens`, `prefixSnapshotBytes` and actual
`prefixReusedTokens`. `prefillTokens + prefixReusedTokens == inputTokens`. These extra durations
are separate from template/tokenization/reset/prefill/cleanup durations. Prefix planning includes
its own source-free rendering and tokenization. Snapshot transfer costs are part of the measured
total; the experiment does not claim that prefix reuse is free. Timed prefill/decode explicitly
synchronize backend work to avoid assigning asynchronous computation to the following sample.

`check_prefix_isolation.py --worker <executable> --model <verified-GGUF> --output <result.json>`
tests Linux CPU in-flight termination, confirmed exit, fresh-process cold inference, subsequent
warm inference and graceful EOF replacement. This is not proof of Windows Job cleanup or physical
7B/Vulkan execution. `test_prefix_policy.cpp` links against the same pinned llama-common build
without loading weights and checks exact instruction admission, source-dependent template
rejection, duplicate instructions and token/boundary limits. Compile with C++17 and treat the
pinned upstream include directories as system includes; its Jinja headers contain intentionally
unused static functions. Compile this test without `NDEBUG`, so its assertions remain enabled.
`test_prefix_state.cpp` retains real-model snapshot corruption, position mismatch and fallback
checks authored before the testing prohibition; it has not been compiled or run.

Primary sources inspected:
- https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/docs/build.md
- https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/ggml/src/ggml-vulkan/ggml-vulkan.cpp
- https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/tools/completion/completion.cpp

## Build and release verification

`Build-AiLyricsRuntime.ps1` builds all workers from a wrapper CMake project over the verified pinned
upstream tree. Windows x64 C++ tools and an official Vulkan SDK containing glslc, Vulkan import
library/headers and SPIRV-Headers are prerequisites; the script installs no SDK or driver. Vulkan
loader is provided by the installed driver; the worker delay-loads it explicitly from System32.
The build collects both pinned llama.cpp and actual SDK component license notices; missing
Vulkan-Headers, Vulkan-Hpp or SPIRV-Headers attribution fails packaging. SDK installer layouts
vary, so the native build must verify that this prerequisite is met. No vendor DLL is bundled.
All packaged workers are individually hash/size-bound in the trusted embedded manifest. Existing
completion/tokenizer binaries remain for diagnostics, not the default production backend.

The resident source digest is SHA256 over LF-normalized CMakeLists.txt, gpu-policy.h and main.cpp,
joined with one LF, in that order. CPU evidence explicitly disables GPU and identifies baseline or
AVX2. Hardware acceptance must separately establish actual Vulkan offload, memory use, cold/warm
latency, semantic results, cancellation, idle release, mode switching and CPU fallback on available
NVIDIA/AMD adapters. Missing hardware stays a recorded limitation.

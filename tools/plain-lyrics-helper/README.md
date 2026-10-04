# Private resident plaintext runtime

Pinned upstream: llama.cpp `7fe450e19305b828c199d602c23a8337aaa1f03b` (v0.5.0).
The host accepts only the pinned Hy-MT2 1.8B Q8_0 (default) and optional Hy-MT2 7B Q8_0
models with the existing official plaintext prompt and sampler. The helper accepts the legacy
`--model <path> --mode cpu|vulkan` command unchanged. The optional final pair
`--model-profile hy-mt2-7b-q8` selects 7B; `hy-mt2-1.8b-q8` explicitly selects the default.
Unknown profiles, extra arguments and arbitrary model-size/budget overrides are rejected.

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
  history are recreated per request. No cross-line prompt or generation history is reused
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

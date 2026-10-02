# Private resident plaintext runtime

Pinned upstream: llama.cpp `7fe450e19305b828c199d602c23a8337aaa1f03b` (v0.5.0).
Only the existing Hy-MT2 Q8 model, hash and official plaintext prompt are accepted by the host.

## Execution contract

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
- Windows Job committed-memory cap remains 3 GiB and one process; host working-set
  watchdog also remains. These are resource limits, not a security sandbox

## GPU admission and optimization

The worker queries actual Vulkan and ggml device properties. It selects one qualifying device,
preferentially discrete, with the largest free budget; multi-GPU splitting is disabled. NVIDIA
vendor ID 0x10de and AMD 0x1002 are eligible. Unknown vendor, missing memory-budget extension,
unavailable driver, unknown memory, or insufficient budget yields CPU fallback.

Admission requires >=4 GiB reported memory and free space for the exact model bytes +1 GiB
context/compute allowance +max(1 GiB,20% total) reserved for desktop/video. Integrated devices also
require separately measured Windows available physical RAM >=model bytes+3 GiB. A shared-memory
capacity number alone is insufficient. This is conservative admission, not a hard VRAM allocation
cap. Windows/driver allocation failure still terminates the worker and falls back safely.

Pinned ggml chooses AMD architecture/subgroup kernels and NVIDIA cooperative-matrix kernels from
reported capabilities. This integration does not force CUDA, HIP, FP16, vendor-name heuristics or
unsupported architecture overrides. Real acceleration/quality/performance must be measured on
actual hardware; CPU builds and mock policy tests do not establish that claim.

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
